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
import { detectFormatInfo, FormatDetection, formatLabel, LogFormat } from './formats';
import { ChunkScan, FormatSpec, scanChunk } from './scanChunk';
import { EntryJob, evaluate, RangeOp, RangeResult, RowsOp, RowsResult, splitEntries } from './entryScan';
import type { ScanPool } from './scanPool';
import { GrowF64, GrowU32, GrowU8, lowerBound } from './grow';
import { CustomFormat, Level, LEVEL_COUNT, SourceInfo } from './types';

export type Encoding = 'auto' | 'utf-8' | 'windows-1252' | 'utf-16le' | 'utf-16be';

export interface SourceOptions {
  encoding?: Encoding;
  customFormats?: CustomFormat[];
  format?: string;
  chunkSize?: number;
  /** Worker threads for scanning big files in parallel; without one, chunks are scanned on the calling thread. */
  scanPool?: ScanPool;
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
  /** Decodes entry texts. It keeps a U+FEFF at the start of a batch, so a text never depends on where a batch starts. */
  private textDecoder!: TextDecoder;
  private tempFile: string | null = null;
  private lastTime = NaN;
  private chunkSize: number;
  /** How workers rebuild this.format (set with it). */
  private formatSpec!: FormatSpec;
  // Level counts and time range of entries [0, statsCount), extended lazily so info() stays cheap while indexing.
  private levelCounts: number[] = [];
  private minTime = Infinity;
  private maxTime = -Infinity;
  private statsCount = 0;
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
    this.textDecoder = new TextDecoder(this.decoder.encoding, { ignoreBOM: true });
    this.fd = fs.openSync(this.readPath, 'r');
    const st = fs.fstatSync(this.fd);
    this.ino = st.ino;
    this.detect(st.mtime.getFullYear(), this.opts.format);
    this.dataEnd = this.dataStart;
    await this.exclusive(() => this.indexFrom(st.size, onProgress));
  }

  private detect(year: number, force: string | undefined) {
    const sample = this.sampleLines(256 * 1024);
    this.detection = detectFormatInfo(sample, { year, customFormats: this.opts.customFormats, force });
    this.format = this.detection.format;
    this.formatSpec = { sample, year, customFormats: this.opts.customFormats, formatId: this.format.id };
  }

  /** Entries per level and the time range ([Infinity, -Infinity] without entries), updated incrementally. */
  stats(): { levelCounts: number[]; min: number; max: number } {
    const count = this.entryCount;
    if (this.statsCount > count || this.levelCounts.length === 0) this.resetStats();
    const lv = this.entryLevel.data;
    const t = this.entryTime.data;
    for (let i = this.statsCount; i < count; i++) {
      this.levelCounts[lv[i]]++;
      const x = t[i];
      if (x < this.minTime) this.minTime = x;
      if (x > this.maxTime) this.maxTime = x;
    }
    this.statsCount = count;
    return { levelCounts: this.levelCounts.slice(), min: this.minTime, max: this.maxTime };
  }

  private resetStats() {
    this.levelCounts = new Array(LEVEL_COUNT).fill(0);
    this.minTime = Infinity;
    this.maxTime = -Infinity;
    this.statsCount = 0;
  }

  /** Takes entries [first, ...) out of the stats before they are removed from the index. */
  private unfoldStats(first: number) {
    for (let e = first; e < this.statsCount; e++) {
      const x = this.entryTime.get(e);
      if (x <= this.minTime || x >= this.maxTime) {
        this.resetStats(); // the range may shrink: recompute it on the next call
        return;
      }
      this.levelCounts[this.entryLevel.get(e)]--;
    }
    this.statsCount = Math.min(this.statsCount, first);
  }

  /** Re-detects nothing; re-indexes with a different format id. */
  async setFormat(formatId: string | undefined, customFormats?: CustomFormat[]): Promise<void> {
    this.opts.format = formatId;
    if (customFormats) this.opts.customFormats = customFormats;
    await this.exclusive(async () => {
      const st = fs.fstatSync(this.fd);
      this.detect(st.mtime.getFullYear(), formatId);
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
        this.unfoldStats(first);
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
    this.resetStats();
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

  /**
   * Indexes [dataEnd, size). With a scan pool, chunks are read in order, scanned on the pool's threads and committed
   * in file order, so rows still appear from the top while the rest of the file is being indexed.
   */
  private async indexFrom(size: number, onProgress?: (f: number) => void) {
    const fmt = this.format;
    const pool = this.opts.scanPool?.usable && size - this.dataEnd > 2 * this.chunkSize ? this.opts.scanPool : null;
    const encoding = this.decoder.encoding;
    const maxInFlight = pool ? pool.size * 2 : 1;
    const inFlight: { scan: Promise<ChunkScan | null>; pos: number; end: number }[] = [];
    let chunkSize = this.chunkSize;
    let pos = this.dataEnd;
    const startPos = pos;
    const commitNext = async () => {
      const item = inFlight.shift()!;
      // A failed worker: scan that chunk again here.
      const r = (await item.scan) ?? scanChunk(fmt, this.decoder, this.readBytes(item.pos, item.end - item.pos), item.pos);
      if (this.closed) return;
      if (this.lineStart.length === 0 && r.lines > 0 && item.end < size) {
        // Size the index once from the first chunk instead of doubling it a dozen times.
        const lines = Math.ceil(((size - startPos) / (item.end - startPos)) * r.lines * 1.05);
        this.lineStart.reserve(lines);
        const entries = Math.ceil((lines * Math.max(1, r.entries)) / r.lines);
        this.entryLine.reserve(entries);
        this.entryLevel.reserve(entries);
        this.entryTime.reserve(entries);
      }
      this.commit(r, item.end);
      if (onProgress && size > startPos) onProgress((item.end - startPos) / (size - startPos));
    };
    while (pos < size && !this.closed) {
      const want = Math.min(chunkSize, size - pos);
      // Each chunk gets its own buffer: it is handed to a worker thread.
      const buf = pool ? Buffer.allocUnsafeSlow(want) : (this.scratch.length >= want ? this.scratch : (this.scratch = Buffer.allocUnsafe(chunkSize)));
      const n = fs.readSync(this.fd, buf, 0, want, pos);
      if (n <= 0) break;
      const eof = pos + n >= size;
      let end = buf.lastIndexOf(10, n - 1) + 1; // bytes up to and including the last '\n'
      if (end === 0 && !eof && chunkSize < MAX_LINE_BYTES) {
        // A line longer than the chunk: read again with a bigger one.
        chunkSize = Math.min(MAX_LINE_BYTES, chunkSize * 2);
        continue;
      }
      if (end === 0) end = n; // the unterminated tail, or a pathological line split at MAX_LINE_BYTES
      else if (eof && end < n) end = n; // include the unterminated last line
      const chunk = buf.subarray(0, end);
      const scan = pool
        ? pool.scan(this.formatSpec, encoding, chunk, pos).catch(() => null)
        : Promise.resolve(scanChunk(fmt, this.decoder, chunk, pos));
      inFlight.push({ scan, pos, end: pos + end });
      pos += end;
      while (inFlight.length >= maxInFlight) await commitNext();
      if (!pool) await yieldNow();
    }
    while (inFlight.length) await commitNext();
  }

  private scratch = Buffer.alloc(0);

  private readBytes(pos: number, len: number): Buffer {
    const buf = Buffer.allocUnsafe(len);
    let got = 0;
    while (got < len) {
      const n = fs.readSync(this.fd, buf, got, len - got, pos + got);
      if (n <= 0) break;
      got += n;
    }
    return buf.subarray(0, got);
  }

  /** Appends a scanned chunk to the index; an entry without a timestamp inherits the previous entry's time. */
  private commit(r: ChunkScan, end: number) {
    const lineBase = this.lineStart.length;
    // The file's first line starts an entry even when it does not look like one.
    if (this.entryLine.length === 0 && r.lines > 0 && (r.entries === 0 || r.entryLines[0] !== 0) && r.first) {
      this.entryLine.push(lineBase);
      this.entryLevel.push(r.first.level);
      if (r.first.time !== null) this.lastTime = r.first.time;
      this.entryTime.push(r.first.time ?? this.lastTime);
    }
    const lines = r.entryLines.subarray(0, r.entries);
    const times = r.entryTimes.subarray(0, r.entries);
    for (let e = 0; e < lines.length; e++) {
      lines[e] += lineBase;
      if (r.entryHasTime[e]) this.lastTime = times[e];
      else times[e] = this.lastTime;
    }
    this.lineStart.pushAll(r.starts.subarray(0, r.lines));
    this.entryLine.pushAll(lines);
    this.entryLevel.pushAll(r.entryLevels.subarray(0, r.entries));
    this.entryTime.pushAll(times);
    this.dataEnd = end;
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

  /** Byte range of entries [from, to) (to <= entryCount) and the number of lines of each. */
  private entrySpan(from: number, to: number): { start: number; end: number; counts: Uint32Array } {
    const counts = new Uint32Array(to - from);
    const lines = this.entryLine.data;
    for (let e = from; e < to; e++) counts[e - from] = (e + 1 < this.entryCount ? lines[e + 1] : this.lineCount) - lines[e];
    const endLine = to < this.entryCount ? lines[to] : this.lineCount;
    return { start: this.byteOfLine(lines[from]), end: this.byteOfLine(endLine), counts };
  }

  /** Texts of entries [from, to). Continuation lines are joined with "\n". */
  readEntries(from: number, to: number): string[] {
    to = Math.min(to, this.entryCount);
    if (from >= to || this.fd < 0) return [];
    const { start, end, counts } = this.entrySpan(from, to);
    return splitEntries(this.textDecoder.decode(this.readBytes(start, end - start)), counts);
  }

  /**
   * A job for the scan pool over the given entries (ascending): byte runs of consecutive entries, which a worker reads
   * from the file itself, plus the line count, level and line number of each entry.
   */
  private entryJob(n: number, at: (i: number) => number, op: RangeOp | RowsOp, candidates: Uint8Array | null = null): EntryJob {
    const runs: number[] = [];
    const runEntries: number[] = [];
    const counts = new Uint32Array(n);
    const levels = new Uint8Array(n);
    const lines = new Uint32Array(n);
    const entryLine = this.entryLine.data;
    for (let i = 0; i < n; ) {
      let j = i + 1;
      while (j < n && at(j) === at(j - 1) + 1) j++;
      const span = this.entrySpan(at(i), at(j - 1) + 1);
      runs.push(span.start, span.end);
      runEntries.push(j - i);
      counts.set(span.counts, i);
      for (let k = i; k < j; k++) {
        levels[k] = this.entryLevel.data[at(k)];
        lines[k] = entryLine[at(k)] + 1;
      }
      i = j;
    }
    return { path: this.readPath, ino: this.ino, encoding: this.decoder.encoding, runs: Float64Array.from(runs), runEntries: Uint32Array.from(runEntries), counts, levels, lines, candidates, op };
  }

  /** Runs a job here: reads the entries through our own handle and evaluates the op exactly as a worker would. */
  private runJob(job: EntryJob, at: (i: number) => number): RangeResult | RowsResult {
    const texts: string[] = [];
    let i = 0;
    for (const len of job.runEntries) {
      for (const t of this.readEntries(at(i), at(i) + len)) texts.push(t);
      i += len;
      while (texts.length < i) texts.push(''); // the file shrank under us
    }
    return evaluate(job, this.format, texts);
  }

  /** Runs a job on the scan pool if there is one (falling back to this thread if a worker fails), else here. */
  private async evalJob(job: () => EntryJob, at: (i: number) => number, pool: ScanPool | null): Promise<RangeResult | RowsResult> {
    if (!pool) return this.runJob(job(), at);
    // A failed worker: run the batch here instead (with a new job: the first one's arrays went to the worker).
    return (await pool.entries(this.formatSpec, job()).catch(() => null)) ?? this.runJob(job(), at);
  }

  /**
   * Evaluates `op` over entries [from, to) in batches and passes each batch's result to `commit` in entry order (return
   * false to stop). With a scan pool, batches are read and evaluated on its threads, several at a time, and committed
   * in order, so the result is exactly what a sequential pass gives. `candidate(e)` false skips an entry (its result is
   * 0 without reading it); without it every entry is tested.
   */
  async scanEntries(
    from: number,
    to: number,
    op: RangeOp,
    candidate: ((e: number) => boolean) | null,
    commit: (first: number, result: RangeResult) => boolean | void,
    batch = 8192,
  ): Promise<void> {
    const pool = this.opts.scanPool?.usable && to - from > batch ? this.opts.scanPool : null;
    const maxInFlight = pool ? pool.size * 2 : 1;
    const inFlight: { first: number; result: Promise<RangeResult | RowsResult> }[] = [];
    let next = from;
    let stopped = false;
    try {
      while (!stopped && !this.closed && (next < to || inFlight.length)) {
        while (next < to && inFlight.length < maxInFlight) {
          const first = next;
          const end = Math.min(to, first + batch);
          next = end;
          const at = (i: number) => first + i;
          const mask = () => {
            if (!candidate) return null;
            const m = new Uint8Array(end - first);
            for (let e = first; e < end; e++) m[e - first] = candidate(e) ? 1 : 0;
            return m;
          };
          inFlight.push({ first, result: this.evalJob(() => this.entryJob(end - first, at, op, mask()), at, pool) });
        }
        const item = inFlight.shift()!;
        const result = (await item.result) as RangeResult;
        if (this.closed || commit(item.first, result) === false) stopped = true;
        else if (!pool) await yieldNow();
      }
    } finally {
      // Stopped early (or failed): the batches still running are dropped.
      for (const item of inFlight) item.result.catch(() => undefined);
    }
  }

  /** Evaluates a rows op over `entries` (ascending), on the scan pool when there is one. */
  evalRows(entries: number[], op: RowsOp): Promise<RowsResult> {
    const pool = this.opts.scanPool?.usable ? this.opts.scanPool : null;
    const at = (i: number) => entries[i];
    return this.evalJob(() => this.entryJob(entries.length, at, op), at, pool) as Promise<RowsResult>;
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
