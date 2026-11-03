// On-disk cache of file indexes, so reopening a big file is instant and a grown file only indexes the new bytes.
// One file per indexed path, in a compact binary format: a versioned header, then line starts and entry lines as
// varint deltas, levels as bytes and times as tagged zigzag varint deltas. A cache file that fails any check is
// ignored. The total size is capped by deleting the least recently used files.

import * as crypto from 'node:crypto';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { LEVEL_COUNT } from './types';

/** Bump whenever the scan rules or the layout change: older files are then ignored (and age out). */
export const CACHE_VERSION = 1;
export const HASH_BYTES = 64 * 1024;
const MAGIC = Buffer.from('ULVIDX');

export interface CacheKey {
  path: string;
  size: number;
  mtimeMs: number;
  headLen: number;
  headHash: Buffer;
  tailLen: number;
  /** Hash of the last tailLen bytes before size. */
  tailHash: Buffer;
  /** The forced format and encoding ('' = detect) and a fingerprint of the custom formats. */
  formatOption: string;
  encodingOption: string;
  customKey: string;
}

export interface CacheHeader {
  key: CacheKey;
  formatId: string;
  encoding: string;
  dataStart: number;
  detectAuto: boolean;
  detectMatched: number;
  detectSampled: number;
  dataEnd: number;
}

export interface CacheEntry extends CacheHeader {
  lineStarts: Float64Array;
  entryLines: Uint32Array;
  levels: Uint8Array;
  times: Float64Array;
}

export const cacheFileFor = (dir: string, file: string) => path.join(dir, crypto.createHash('sha256').update(file).digest('hex').slice(0, 32) + '.idx');

/** SHA-256 of [offset, offset + len) of an open file. */
export function hashRange(fd: number, offset: number, len: number): Buffer {
  const b = Buffer.alloc(len);
  let got = 0;
  while (got < len) {
    const n = fs.readSync(fd, b, got, len - got, offset + got);
    if (n <= 0) break;
    got += n;
  }
  return crypto.createHash('sha256').update(b.subarray(0, got)).digest();
}

/** Fingerprint of the custom formats, which auto-detection and custom format ids depend on. */
export function customFormatsKey(formats: { name: string; pattern: string }[] | undefined): string {
  if (!formats?.length) return '';
  return crypto.createHash('sha256').update(formats.map((f) => `${f.name}\u0001${f.pattern}\u0002`).join('')).digest('hex').slice(0, 16);
}

// --------------------------------------------------------------------- write

class Writer {
  buf: Buffer;
  n = 0;
  constructor(capacity: number) {
    this.buf = Buffer.allocUnsafe(Math.max(1024, capacity));
  }
  private room(k: number) {
    if (this.n + k <= this.buf.length) return;
    const next = Buffer.allocUnsafe(Math.max(this.buf.length * 2, this.n + k));
    this.buf.copy(next, 0, 0, this.n);
    this.buf = next;
  }
  /** A non-negative integer up to 2^53. */
  u(v: number) {
    this.room(10);
    const b = this.buf;
    if (v < 128) {
      b[this.n++] = v;
      return;
    }
    if (v < 0x80000000) {
      // 31-bit fast path with bit operations
      while (v >= 128) {
        b[this.n++] = (v & 127) | 128;
        v >>>= 7;
      }
      b[this.n++] = v;
      return;
    }
    while (v >= 128) {
      b[this.n++] = (v % 128) | 128;
      v = Math.floor(v / 128);
    }
    b[this.n++] = v;
  }
  bytes(src: Uint8Array) {
    this.room(src.length);
    this.buf.set(src, this.n);
    this.n += src.length;
  }
  blob(src: Uint8Array) {
    this.u(src.length);
    this.bytes(src);
  }
  str(s: string) {
    this.blob(Buffer.from(s, 'utf-8'));
  }
  f64(v: number) {
    this.room(8);
    this.buf.writeDoubleLE(v, this.n);
    this.n += 8;
  }
}

const MAX_TIME_DELTA = 2 ** 50;

export function writeCache(file: string, h: CacheHeader, lineStarts: Float64Array, entryLines: Uint32Array, levels: Uint8Array, times: Float64Array) {
  const w = new Writer(lineStarts.length * 2 + entryLines.length * 4 + 4096);
  const k = h.key;
  w.bytes(MAGIC);
  w.u(CACHE_VERSION);
  w.str(k.path);
  w.u(k.size);
  w.u(Math.round(k.mtimeMs * 1000)); // µs: sub-millisecond mtimes survive the round trip
  w.u(k.headLen);
  w.blob(k.headHash);
  w.u(k.tailLen);
  w.blob(k.tailHash);
  w.str(k.formatOption);
  w.str(k.encodingOption);
  w.str(k.customKey);
  w.str(h.formatId);
  w.str(h.encoding);
  w.u(h.dataStart);
  w.u(h.detectAuto ? 1 : 0);
  w.u(h.detectMatched);
  w.u(h.detectSampled);
  w.u(h.dataEnd);
  w.u(lineStarts.length);
  w.u(entryLines.length);
  let prev = 0;
  for (let i = 0; i < lineStarts.length; i++) {
    w.u(lineStarts[i] - prev);
    prev = lineStarts[i];
  }
  prev = 0;
  for (let i = 0; i < entryLines.length; i++) {
    w.u(entryLines[i] - prev);
    prev = entryLines[i];
  }
  w.bytes(levels);
  // Times: (zigzag delta << 1) for whole milliseconds; 1 for anything else (no time, fractions), whose raw value
  // follows in a list of doubles after the varints.
  prev = 0;
  const raw: number[] = [];
  for (let i = 0; i < times.length; i++) {
    const t = times[i];
    const d = t - prev;
    if (Number.isInteger(d) && Math.abs(d) < MAX_TIME_DELTA) {
      w.u((d >= 0 ? d * 2 : -d * 2 - 1) * 2);
      prev = t;
    } else {
      w.u(1);
      raw.push(t);
    }
  }
  w.u(raw.length);
  for (const t of raw) w.f64(t);
  w.bytes(MAGIC); // trailer: a truncated file never passes
  fs.mkdirSync(path.dirname(file), { recursive: true });
  const tmp = `${file}.${process.pid}.tmp`;
  try {
    fs.writeFileSync(tmp, w.buf.subarray(0, w.n));
    fs.renameSync(tmp, file);
  } catch (e) {
    fs.rmSync(tmp, { force: true });
    throw e;
  }
}

// ---------------------------------------------------------------------- read

class Reader {
  p = 0;
  constructor(readonly d: Buffer) {}
  u(): number {
    const d = this.d;
    let b = d[this.p++];
    if (b === undefined) throw new Error('truncated');
    if (b < 128) return b;
    let v = b & 127;
    let mul = 128;
    for (;;) {
      b = d[this.p++];
      if (b === undefined || mul > 2 ** 56) throw new Error('bad varint');
      v += (b & 127) * mul;
      if (b < 128) return v;
      mul *= 128;
    }
  }
  int(max: number): number {
    const v = this.u();
    if (v > max) throw new Error('value out of range');
    return v;
  }
  blob(): Buffer {
    const n = this.int(1 << 20);
    if (this.p + n > this.d.length) throw new Error('truncated');
    const b = this.d.subarray(this.p, this.p + n);
    this.p += n;
    return b;
  }
  str() {
    return this.blob().toString('utf-8');
  }
}

/** Reads and checks a cache file; null when it is missing, from another version or corrupt. */
export function readCache(file: string): CacheEntry | null {
  let d: Buffer;
  try {
    d = fs.readFileSync(file);
  } catch {
    return null;
  }
  try {
    return decode(d);
  } catch {
    return null;
  }
}

function decode(d: Buffer): CacheEntry {
  if (d.length < MAGIC.length * 2 || !d.subarray(0, MAGIC.length).equals(MAGIC)) throw new Error('not an index cache file');
  if (!d.subarray(d.length - MAGIC.length).equals(MAGIC)) throw new Error('bad trailer');
  const r = new Reader(d);
  r.p = MAGIC.length;
  if (r.u() !== CACHE_VERSION) throw new Error('other version');
  const key: CacheKey = {
    path: r.str(),
    size: r.u(),
    mtimeMs: r.u() / 1000,
    headLen: r.int(HASH_BYTES),
    headHash: Buffer.from(r.blob()),
    tailLen: r.int(HASH_BYTES),
    tailHash: Buffer.from(r.blob()),
    formatOption: r.str(),
    encodingOption: r.str(),
    customKey: r.str(),
  };
  const formatId = r.str();
  const encoding = r.str();
  const dataStart = r.int(4);
  const detectAuto = r.u() === 1;
  const detectMatched = r.u();
  const detectSampled = r.u();
  const dataEnd = r.u();
  const lines = r.int(2 ** 31);
  const entries = r.int(lines);
  if (dataEnd > key.size || dataEnd < dataStart || (lines > 0 && entries === 0)) throw new Error('bad header');
  // Every line takes at least one byte, so impossible counts fail before anything is allocated.
  if (lines > dataEnd - dataStart + 1 || d.length - r.p < lines + 3 * entries) throw new Error('bad counts');
  // The bulk of the file: raw varints first, then prefix sums and checks, each a tight loop in a small function
  // (this function runs once, so its own loops would not get fully optimised).
  const lineStarts = new Float64Array(lines);
  r.p = varints(d, r.p, lineStarts);
  prefixLines(lineStarts, dataStart, dataEnd);
  const entryDeltas = new Float64Array(entries);
  r.p = varints(d, r.p, entryDeltas);
  const entryLines = prefixEntries(entryDeltas, lines);
  if (r.p + entries > d.length) throw new Error('truncated');
  const levels = new Uint8Array(d.subarray(r.p, r.p + entries));
  r.p += entries;
  checkLevels(levels);
  const times = new Float64Array(entries);
  r.p = varints(d, r.p, times);
  const rawCount = r.int(entries);
  if (r.p + rawCount * 8 > d.length) throw new Error('truncated');
  const raw = new Float64Array(rawCount);
  for (let i = 0; i < rawCount; i++) raw[i] = d.readDoubleLE(r.p + i * 8);
  r.p += rawCount * 8;
  decodeTimes(times, raw);
  if (r.p !== d.length - MAGIC.length) throw new Error('bad trailer');
  return { key, formatId, encoding, dataStart, detectAuto, detectMatched, detectSampled, dataEnd, lineStarts, entryLines, levels, times };
}

function checkLevels(levels: Uint8Array) {
  for (let i = 0; i < levels.length; i++) if (levels[i] >= LEVEL_COUNT) throw new Error('bad level');
}

function prefixLines(a: Float64Array, dataStart: number, dataEnd: number) {
  let prev = 0;
  for (let i = 0; i < a.length; i++) {
    const v = prev + a[i];
    if (i > 0 ? v <= prev : v !== dataStart) throw new Error('line starts out of order');
    a[i] = prev = v;
  }
  if (a.length > 0 && a[a.length - 1] >= Math.max(dataEnd, dataStart + 1)) throw new Error('line start past the data');
}

function prefixEntries(deltas: Float64Array, lines: number): Uint32Array {
  const out = new Uint32Array(deltas.length);
  let prev = 0;
  for (let i = 0; i < deltas.length; i++) {
    const v = prev + deltas[i];
    if (i > 0 ? v <= prev : v !== 0) throw new Error('entry lines out of order');
    if (v >= lines) throw new Error('entry line past the end');
    out[i] = prev = v;
  }
  return out;
}

/** Turns the time varints (in place) into times; 1 takes the next raw value. */
function decodeTimes(t: Float64Array, raw: Float64Array) {
  let prev = 0;
  let r = 0;
  for (let i = 0; i < t.length; i++) {
    const v = t[i];
    if (v === 1) {
      if (r >= raw.length) throw new Error('bad time');
      t[i] = raw[r++];
      continue;
    }
    const z = v / 2;
    if (!Number.isInteger(z)) throw new Error('bad time');
    prev += Number.isInteger(z / 2) ? z / 2 : -(z + 1) / 2;
    t[i] = prev;
  }
  if (r !== raw.length) throw new Error('bad time');
}

/** Reads out.length varints starting at p; returns the position after them. */
function varints(d: Buffer, p: number, out: Float64Array): number {
  const n = out.length;
  const end = d.length;
  for (let i = 0; i < n; i++) {
    let b = d[p++];
    if (b < 128) {
      out[i] = b;
      continue;
    }
    let v = b & 127; // also for undefined (past the end): the loop below then throws
    let mul = 128;
    do {
      if (p >= end || mul > 2 ** 56) throw new Error('bad varint');
      b = d[p++];
      v += (b & 127) * mul;
      mul *= 128;
    } while (b >= 128);
    out[i] = v;
  }
  return p;
}

// -------------------------------------------------------------------- upkeep

/** Marks a cache file as recently used, so eviction keeps it. */
export function touchCache(file: string) {
  try {
    const now = new Date();
    fs.utimesSync(file, now, now);
  } catch {
    // ignore
  }
}

function cacheFiles(dir: string): { file: string; size: number; mtimeMs: number }[] {
  try {
    return fs
      .readdirSync(dir)
      .filter((f) => f.endsWith('.idx'))
      .map((f) => {
        const st = fs.statSync(path.join(dir, f));
        return { file: path.join(dir, f), size: st.size, mtimeMs: st.mtimeMs };
      });
  } catch {
    return [];
  }
}

/** Deletes the least recently used cache files until the total is at most maxBytes. */
export function evictCache(dir: string, maxBytes: number) {
  const files = cacheFiles(dir).sort((a, b) => a.mtimeMs - b.mtimeMs);
  let total = files.reduce((n, f) => n + f.size, 0);
  for (const f of files) {
    if (total <= maxBytes) break;
    try {
      fs.rmSync(f.file);
      total -= f.size;
    } catch {
      // in use or gone
    }
  }
}

export const cacheSize = (dir: string) => cacheFiles(dir).reduce((n, f) => n + f.size, 0);

/** Deletes every cache file; returns the bytes freed. */
export function clearCache(dir: string): number {
  let freed = 0;
  let names: string[];
  try {
    names = fs.readdirSync(dir);
  } catch {
    return 0;
  }
  for (const n of names) {
    if (!n.endsWith('.idx') && !n.endsWith('.tmp')) continue;
    try {
      const f = path.join(dir, n);
      const size = fs.statSync(f).size;
      fs.rmSync(f);
      freed += size;
    } catch {
      // in use or gone
    }
  }
  return freed;
}
