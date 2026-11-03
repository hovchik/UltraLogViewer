// A single log file on disk: byte-offset line index, entry index (multi-line grouping with
// level + timestamp per entry), on-demand reads and incremental tail refresh.
//
// Nothing but the index is kept in memory, so files far larger than RAM open fine.

import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import * as zlib from 'node:zlib';
import { pipeline } from 'node:stream/promises';
import { Transform } from 'node:stream';
import { detectFormatInfo, FormatDetection, formatLabel, LogFormat, QuickInfo } from './formats';
import { GrowF64, GrowU32, GrowU8, lowerBound } from './grow';
import { CustomFormat, Level, SourceInfo } from './types';

export type Encoding = 'auto' | 'utf-8' | 'windows-1252' | 'utf-16le' | 'utf-16be';

export interface SourceOptions {
  encoding?: Encoding;
  customFormats?: CustomFormat[];
  format?: string;
  chunkSize?: number;
}

export interface RefreshResult {
  changed: boolean;
  /** The file was truncated or replaced: everything was re-indexed. */
  reset: boolean;
  /** First entry whose content may have changed. */
  firstChangedEntry: number;
}

const MAX_LINE_BYTES = 64 * 1024 * 1024;
const yieldNow = () => new Promise<void>((r) => setImmediate(r));

export class FileSource {
  readonly lineStart = new GrowF64(4096);
  readonly entryLine = new GrowU32(4096);
  readonly entryLevel = new GrowU8(4096);
  readonly entryTime = new GrowF64(4096);
  /** Byte offset just after the last indexed line. */
  dataEnd = 0;
  format!: LogFormat;
  detection!: FormatDetection;
  encoding: Exclude<Encoding, 'auto'> = 'utf-8';
  compressed = false;
  /** Path actually read (a temp file for gzip / UTF-16 input). */
  private readPath: string;
  private fd = -1;
  private ino = 0;
  private dataStart = 0;
  private decoder!: TextDecoder;
  private tempFile: string | null = null;
  private lastTime = NaN;
  private chunkSize: number;
  private busy: Promise<unknown> = Promise.resolve();
  closed = false;

  constructor(readonly id: number, readonly path: string, private opts: SourceOptions = {}) {
    this.readPath = path;
    this.chunkSize = opts.chunkSize ?? 4 * 1024 * 1024;
  }

  get lineCount() {
    return this.lineStart.length;
  }
  get entryCount() {
    return this.entryLine.length;
  }
  get live() {
    return this.tempFile === null;
  }

  info(): SourceInfo {
    return {
      id: this.id,
      path: this.path,
      displayName: path.basename(this.path),
      size: this.dataEnd,
      lines: this.lineCount,
      entries: this.entryCount,
      format: this.format?.name ?? '',
      formatId: this.format?.id ?? '',
      formatLabel: this.format ? formatLabel(this.format.id, this.format.name) : '',
      formatAuto: this.detection?.auto ?? true,
      formatMatched: this.detection?.matched ?? 0,
      formatSampled: this.detection?.sampled ?? 0,
      encoding: this.encoding,
      compressed: this.compressed,
      live: this.live,
    };
  }

  /** Prepares the file (decompress / transcode if needed), detects the format and indexes it. */
  async open(onProgress?: (fraction: number) => void): Promise<void> {
    const head = readHead(this.path, 4);
    if (head[0] === 0x1f && head[1] === 0x8b) {
      this.compressed = true;
      this.tempFile = await gunzipToTemp(this.path, onProgress);
      this.readPath = this.tempFile;
    }
    const enc = this.opts.encoding && this.opts.encoding !== 'auto' ? { encoding: this.opts.encoding, bom: 0 } : detectEncoding(this.readPath);
    if (enc.encoding === 'utf-16le' || enc.encoding === 'utf-16be') {
      const tmp = await transcodeToUtf8(this.readPath, enc.encoding, enc.bom);
      if (this.tempFile) fs.rmSync(this.tempFile, { force: true });
      this.tempFile = tmp;
      this.readPath = tmp;
      this.encoding = enc.encoding;
      this.dataStart = 0;
    } else {
      this.encoding = enc.encoding;
      this.dataStart = enc.bom;
    }
    this.decoder = new TextDecoder(this.encoding === 'utf-8' || this.encoding.startsWith('utf-16') ? 'utf-8' : 'windows-1252');
    this.fd = fs.openSync(this.readPath, 'r');
    const st = fs.fstatSync(this.fd);
    this.ino = st.ino;
    this.detection = detectFormatInfo(this.sampleLines(256 * 1024), {
      year: st.mtime.getFullYear(),
      customFormats: this.opts.customFormats,
      force: this.opts.format,
    });
    this.format = this.detection.format;
    this.dataEnd = this.dataStart;
    await this.exclusive(() => this.indexFrom(st.size, onProgress));
  }

  /** Re-detects nothing; re-indexes with a different format id. */
  async setFormat(formatId: string | undefined, customFormats?: CustomFormat[]): Promise<void> {
    this.opts.format = formatId;
    if (customFormats) this.opts.customFormats = customFormats;
    await this.exclusive(async () => {
      const st = fs.fstatSync(this.fd);
      this.detection = detectFormatInfo(this.sampleLines(256 * 1024), { year: st.mtime.getFullYear(), customFormats: this.opts.customFormats, force: formatId });
      this.format = this.detection.format;
      this.resetIndex();
      await this.indexFrom(st.size);
    });
  }

  /** Picks up appended data, truncation or replacement (log rotation). */
  async refresh(): Promise<RefreshResult> {
    if (!this.live || this.closed) return { changed: false, reset: false, firstChangedEntry: this.entryCount };
    return this.exclusive(async () => {
      let st: fs.Stats;
      try {
        st = fs.statSync(this.path);
      } catch {
        return { changed: false, reset: false, firstChangedEntry: this.entryCount };
      }
      if (st.ino !== this.ino || st.size < this.dataEnd) {
        // Rotated or truncated: start over on the new file.
        fs.closeSync(this.fd);
        this.fd = fs.openSync(this.readPath, 'r');
        this.ino = fs.fstatSync(this.fd).ino;
        this.resetIndex();
        await this.indexFrom(st.size);
        return { changed: true, reset: true, firstChangedEntry: 0 };
      }
      if (st.size === this.dataEnd) return { changed: false, reset: false, firstChangedEntry: this.entryCount };
      // Roll back the last entry: it may have been an unterminated line or may gain continuation lines.
      const first = Math.max(0, this.entryCount - 1);
      if (this.entryCount) {
        const line = this.entryLine.get(first);
        this.dataEnd = this.lineStart.get(line);
        this.lineStart.truncate(line);
        this.entryLine.truncate(first);
        this.entryLevel.truncate(first);
        this.entryTime.truncate(first);
        this.lastTime = first ? this.entryTime.get(first - 1) : NaN;
      }
      await this.indexFrom(st.size);
      return { changed: true, reset: false, firstChangedEntry: first };
    });
  }

  private resetIndex() {
    this.lineStart.truncate(0);
    this.entryLine.truncate(0);
    this.entryLevel.truncate(0);
    this.entryTime.truncate(0);
    this.dataEnd = this.dataStart;
    this.lastTime = NaN;
  }

  private exclusive<T>(fn: () => Promise<T>): Promise<T> {
    const run = this.busy.then(fn, fn);
    this.busy = run.catch(() => undefined);
    return run;
  }

  private sampleLines(bytes: number): string[] {
    const buf = Buffer.alloc(Math.min(bytes, Math.max(0, fs.fstatSync(this.fd).size - this.dataStart)));
    const n = fs.readSync(this.fd, buf, 0, buf.length, this.dataStart);
    const text = this.decoder.decode(buf.subarray(0, n));
    const lines = text.split('\n').map((l) => (l.endsWith('\r') ? l.slice(0, -1) : l));
    if (n === bytes) lines.pop();
    return lines;
  }

  private async indexFrom(size: number, onProgress?: (f: number) => void) {
    const fmt = this.format;
    const scan = fmt.scan?.bind(fmt);
    let buf = Buffer.allocUnsafe(this.chunkSize);
    let pos = this.dataEnd;
    const startPos = pos;
    while (pos < size && !this.closed) {
      const want = Math.min(buf.length, size - pos);
      const n = fs.readSync(this.fd, buf, 0, want, pos);
      if (n <= 0) break;
      const eof = pos + n >= size;
      let end = buf.lastIndexOf(10, n - 1) + 1; // bytes up to and including the last '\n'
      if (end === 0 && !eof && buf.length < MAX_LINE_BYTES) {
        // A line longer than the buffer: grow it and read again.
        buf = Buffer.allocUnsafe(Math.min(MAX_LINE_BYTES, buf.length * 2));
        continue;
      }
      if (end === 0) end = n; // the unterminated tail, or a pathological line split at MAX_LINE_BYTES
      else if (eof && end < n) end = n; // include the unterminated last line
      const text = this.decoder.decode(buf.subarray(0, end));
      const lines = text.split('\n');
      if (text.endsWith('\n')) lines.pop();
      let off = 0;
      for (let i = 0; i < lines.length; i++) {
        let line = lines[i];
        if (line.endsWith('\r')) line = line.slice(0, -1);
        const lineIdx = this.lineStart.length;
        this.lineStart.push(pos + off);
        let q: QuickInfo | null;
        if (!fmt.multiline) q = fmt.quick(line);
        else if (scan) q = scan(line);
        else q = fmt.isEntryStart(line) ? fmt.quick(line) : null;
        if (!q && this.entryLine.length === 0) q = fmt.quick(line);
        if (q) {
          this.entryLine.push(lineIdx);
          this.entryLevel.push(q.level);
          if (q.time !== null) this.lastTime = q.time;
          this.entryTime.push(q.time ?? this.lastTime);
        }
        const nl = buf.indexOf(10, off);
        off = nl < 0 || nl >= end ? end : nl + 1;
      }
      pos += end;
      this.dataEnd = pos;
      if (onProgress && size > startPos) onProgress((pos - startPos) / (size - startPos));
      await yieldNow();
    }
  }

  /** Line range [first, last] (0-based, inclusive) of an entry. */
  entryLines(entry: number): [number, number] {
    const first = this.entryLine.get(entry);
    const last = entry + 1 < this.entryCount ? this.entryLine.get(entry + 1) - 1 : this.lineCount - 1;
    return [first, last];
  }

  /** Entry containing a 0-based line. */
  entryForLine(line: number): number {
    const e = lowerBound(0, this.entryCount, (i) => this.entryLine.get(i) > line) - 1;
    return Math.max(0, e);
  }

  private byteOfLine(line: number) {
    return line < this.lineCount ? this.lineStart.get(line) : this.dataEnd;
  }

  /** Texts of entries [from, to). Continuation lines are joined with "\n". */
  readEntries(from: number, to: number): string[] {
    to = Math.min(to, this.entryCount);
    if (from >= to || this.fd < 0) return [];
    const firstLine = this.entryLine.get(from);
    const endLine = to < this.entryCount ? this.entryLine.get(to) : this.lineCount;
    const start = this.byteOfLine(firstLine);
    const end = this.byteOfLine(endLine);
    const buf = Buffer.allocUnsafe(end - start);
    let got = 0;
    while (got < buf.length) {
      const n = fs.readSync(this.fd, buf, got, buf.length - got, start + got);
      if (n <= 0) break;
      got += n;
    }
    const lines = this.decoder.decode(buf.subarray(0, got)).split('\n');
    const out: string[] = [];
    let li = 0;
    const strip = (l: string) => (l.charCodeAt(l.length - 1) === 13 ? l.slice(0, -1) : l);
    for (let e = from; e < to; e++) {
      const count = (e + 1 < this.entryCount ? this.entryLine.get(e + 1) : this.lineCount) - this.entryLine.get(e);
      if (count === 1) out.push(strip(lines[li] ?? ''));
      else out.push(lines.slice(li, li + count).map(strip).join('\n'));
      li += count;
    }
    return out;
  }

  /** Streams entries [from, to) in batches, for filtering/searching/exporting large ranges. */
  async forEachEntryBatch(from: number, to: number, fn: (first: number, texts: string[]) => void | false, batch = 4096): Promise<void> {
    for (let e = from; e < to && !this.closed; e += batch) {
      const texts = this.readEntries(e, Math.min(to, e + batch));
      if (fn(e, texts) === false) return;
      if (((e - from) / batch) % 16 === 15) await yieldNow();
    }
  }

  level(entry: number): Level {
    return this.entryLevel.get(entry);
  }
  time(entry: number): number {
    return this.entryTime.get(entry);
  }

  close() {
    this.closed = true;
    if (this.fd >= 0) fs.closeSync(this.fd);
    this.fd = -1;
    if (this.tempFile) fs.rmSync(this.tempFile, { force: true });
  }
}

function readHead(file: string, n: number): Buffer {
  const fd = fs.openSync(file, 'r');
  try {
    const b = Buffer.alloc(n);
    const got = fs.readSync(fd, b, 0, n, 0);
    return b.subarray(0, got);
  } finally {
    fs.closeSync(fd);
  }
}

export function detectEncoding(file: string): { encoding: Exclude<Encoding, 'auto'>; bom: number } {
  const b = readHead(file, 64 * 1024);
  if (b[0] === 0xef && b[1] === 0xbb && b[2] === 0xbf) return { encoding: 'utf-8', bom: 3 };
  if (b[0] === 0xff && b[1] === 0xfe) return { encoding: 'utf-16le', bom: 2 };
  if (b[0] === 0xfe && b[1] === 0xff) return { encoding: 'utf-16be', bom: 2 };
  if (b.length >= 4) {
    let zEven = 0, zOdd = 0;
    const n = Math.min(b.length, 4096) & ~1;
    for (let i = 0; i < n; i += 2) {
      if (b[i] === 0) zEven++;
      if (b[i + 1] === 0) zOdd++;
    }
    if (zOdd > n / 8 && zEven < n / 64) return { encoding: 'utf-16le', bom: 0 };
    if (zEven > n / 8 && zOdd < n / 64) return { encoding: 'utf-16be', bom: 0 };
  }
  // Drop a possibly cut multi-byte sequence at the end of the sample before validating.
  let end = b.length;
  for (let k = 0; k < 3 && end > 0 && (b[end - 1] & 0xc0) === 0x80; k++) end--;
  if (end > 0 && b[end - 1] >= 0xc0) end--;
  try {
    new TextDecoder('utf-8', { fatal: true }).decode(b.subarray(0, end));
    return { encoding: 'utf-8', bom: 0 };
  } catch {
    return { encoding: 'windows-1252', bom: 0 };
  }
}

function tempPath(src: string, ext: string) {
  return path.join(os.tmpdir(), `ultralogviewer-${process.pid}-${Date.now()}-${Math.random().toString(36).slice(2)}-${path.basename(src)}${ext}`);
}

async function gunzipToTemp(file: string, onProgress?: (f: number) => void): Promise<string> {
  const out = tempPath(file, '.log');
  const total = fs.statSync(file).size || 1;
  let read = 0;
  const input = fs.createReadStream(file);
  input.on('data', (c) => {
    read += c.length;
    onProgress?.(Math.min(0.99, read / total) * 0.5);
  });
  await pipeline(input, zlib.createGunzip(), fs.createWriteStream(out));
  return out;
}

async function transcodeToUtf8(file: string, enc: 'utf-16le' | 'utf-16be', bom: number): Promise<string> {
  const out = tempPath(file, '.utf8.log');
  const dec = new TextDecoder(enc);
  const t = new Transform({
    transform(chunk: Buffer, _e, cb) {
      cb(null, Buffer.from(dec.decode(chunk, { stream: true }), 'utf-8'));
    },
    flush(cb) {
      cb(null, Buffer.from(dec.decode(), 'utf-8'));
    },
  });
  await pipeline(fs.createReadStream(file, { start: bom }), t, fs.createWriteStream(out));
  return out;
}
