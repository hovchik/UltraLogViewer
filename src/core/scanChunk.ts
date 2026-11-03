// Scanning one chunk of a log file for line starts and entry starts. Pure (no state carried between chunks), so
// chunks can be scanned in any order, on any thread, and committed in file order afterwards.

import { detectFormatInfo, LogFormat, QuickInfo } from './formats';
import { CustomFormat } from './types';

export interface ChunkScan {
  /** Absolute byte offset of each line. */
  starts: Float64Array;
  lines: number;
  /** Line (relative to the chunk) where each entry starts. */
  entryLines: Uint32Array;
  entryLevels: Uint8Array;
  entryTimes: Float64Array;
  /** 1 when the entry has its own timestamp; otherwise it inherits the previous entry's time at commit. */
  entryHasTime: Uint8Array;
  entries: number;
  /** quick() of line 0 when it does not start an entry (the file's first line always does). */
  first: QuickInfo | null;
}

/** What a scanning thread needs to rebuild exactly the format the file was detected with. */
export interface FormatSpec {
  sample: string[];
  year: number;
  customFormats?: CustomFormat[];
  formatId: string;
}

export function formatFromSpec(spec: FormatSpec): LogFormat {
  const f = detectFormatInfo(spec.sample, { year: spec.year, customFormats: spec.customFormats, force: spec.formatId }).format;
  if (f.id !== spec.formatId) throw new Error(`Format ${spec.formatId} is not available`);
  return f;
}

/** Scans `buf`, which starts at file offset `pos` and ends at a line boundary (or the end of the data). */
export function scanChunk(fmt: LogFormat, decoder: TextDecoder, bytes: Uint8Array, pos: number): ChunkScan {
  const buf = Buffer.isBuffer(bytes) ? bytes : Buffer.from(bytes.buffer, bytes.byteOffset, bytes.byteLength); // memchr-backed indexOf
  const text = decoder.decode(buf);
  const lines = text.split('\n');
  if (text.endsWith('\n')) lines.pop();
  const n = lines.length;
  const r: ChunkScan = {
    starts: new Float64Array(n),
    lines: n,
    entryLines: new Uint32Array(n),
    entryLevels: new Uint8Array(n),
    entryTimes: new Float64Array(n),
    entryHasTime: new Uint8Array(n),
    entries: 0,
    first: null,
  };
  const scan = fmt.scan?.bind(fmt);
  let off = 0;
  for (let i = 0; i < n; i++) {
    let line = lines[i];
    if (line.charCodeAt(line.length - 1) === 13) line = line.slice(0, -1);
    r.starts[i] = pos + off;
    let q: QuickInfo | null;
    if (!fmt.multiline) q = fmt.quick(line);
    else if (scan) q = scan(line);
    else q = fmt.isEntryStart(line) ? fmt.quick(line) : null;
    if (q) {
      const e = r.entries++;
      r.entryLines[e] = i;
      r.entryLevels[e] = q.level;
      if (q.time !== null) {
        r.entryTimes[e] = q.time;
        r.entryHasTime[e] = 1;
      }
    } else if (i === 0) r.first = fmt.quick(line);
    const nl = buf.indexOf(10, off);
    off = nl < 0 ? buf.length : nl + 1;
  }
  return r;
}

/** The typed arrays of a scan, for transferring it between threads without copying. */
export function scanBuffers(r: ChunkScan): ArrayBuffer[] {
  return [r.starts.buffer, r.entryLines.buffer, r.entryLevels.buffer, r.entryTimes.buffer, r.entryHasTime.buffer] as ArrayBuffer[];
}
