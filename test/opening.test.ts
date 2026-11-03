// The file-opening paths: index cache, time windows, compressed inputs, parallel merges, stdin and the command line.

import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { PassThrough } from 'node:stream';
import * as zlib from 'node:zlib';
import { describe, expect, it } from 'vitest';
import { expandArchives, memberPath } from '../src/core/archives';
import { Bzip2Decoder } from '../src/core/bzip2';
import { LogDocument } from '../src/core/document';
import { FileSource, SourceOptions } from '../src/core/fileSource';
import { cacheFileFor, cacheSize, clearCache } from '../src/core/indexCache';
import { captureStdin } from '../src/core/stdinCapture';
import { describeWindow, parseCommandLine, parseDuration, parsePoint } from '../src/core/timeWindow';
import { LOG4J, tmpFile } from './helpers';

const cacheDir = () => fs.mkdtempSync(path.join(os.tmpdir(), 'ulv-cache-'));
const local = (y: number, mo: number, d: number, h = 0, mi = 0, s = 0) => new Date(y, mo - 1, d, h, mi, s).getTime();
const p2 = (n: number) => String(n).padStart(2, '0');
const stamp = (ms: number) => {
  const d = new Date(ms);
  return `${d.getFullYear()}-${p2(d.getMonth() + 1)}-${p2(d.getDate())} ${p2(d.getHours())}:${p2(d.getMinutes())}:${p2(d.getSeconds())},000`;
};

/** One entry per second from 2024-05-01 00:00 local; every 7th has a three-line stack trace. */
function bigLog(entries: number, startSecond = 0): string {
  const t0 = local(2024, 5, 1);
  const out: string[] = [];
  for (let i = startSecond; i < startSecond + entries; i++) {
    out.push(`${stamp(t0 + i * 1000)}${i % 5 === 0 ? ' [main] ERROR com.acme.Svc - failed ' : ' [main] INFO  com.acme.Svc - handled '}${i}\n`);
    if (i % 7 === 0) out.push('java.lang.IllegalStateException: at 2024-05-01 00:00:00\n\tat com.acme.Svc.run(Svc.java:10)\n\tat java.lang.Thread.run(Thread.java:833)\n');
  }
  return out.join('');
}

async function open(file: string, opts: SourceOptions = {}) {
  const s = new FileSource(0, file, opts);
  await s.open();
  return s;
}

function sameIndex(a: FileSource, b: FileSource) {
  expect(b.lineCount).toBe(a.lineCount);
  expect(b.entryCount).toBe(a.entryCount);
  expect(b.dataEnd).toBe(a.dataEnd);
  expect(b.entryLevel.view()).toEqual(a.entryLevel.view());
  expect(b.entryTime.view()).toEqual(a.entryTime.view());
  expect(b.entryLine.view()).toEqual(a.entryLine.view());
  expect(b.readEntries(b.entryCount - 3, b.entryCount)).toEqual(a.readEntries(a.entryCount - 3, a.entryCount));
}

/** A minimal zip writer for the tests (stored or deflated members). */
function writeZip(file: string, members: [string, string | Buffer | null][]) {
  const parts: Buffer[] = [];
  const central: Buffer[] = [];
  let off = 0;
  for (const [name, content] of members) {
    const data = content === null ? Buffer.alloc(0) : Buffer.from(content);
    const deflate = data.length > 100;
    const body = deflate ? zlib.deflateRawSync(data) : data;
    const crc = zlib.crc32(data);
    const nameB = Buffer.from(name);
    const local = Buffer.alloc(30);
    local.writeUInt32LE(0x04034b50, 0);
    local.writeUInt16LE(20, 4);
    local.writeUInt16LE(0x800, 6);
    local.writeUInt16LE(deflate ? 8 : 0, 8);
    local.writeUInt32LE(crc, 14);
    local.writeUInt32LE(body.length, 18);
    local.writeUInt32LE(data.length, 22);
    local.writeUInt16LE(nameB.length, 26);
    const cd = Buffer.alloc(46);
    cd.writeUInt32LE(0x02014b50, 0);
    cd.writeUInt16LE(20, 4);
    cd.writeUInt16LE(20, 6);
    cd.writeUInt16LE(0x800, 8);
    cd.writeUInt16LE(deflate ? 8 : 0, 10);
    cd.writeUInt32LE(crc, 16);
    cd.writeUInt32LE(body.length, 20);
    cd.writeUInt32LE(data.length, 24);
    cd.writeUInt16LE(nameB.length, 28);
    cd.writeUInt32LE(off, 42);
    parts.push(local, nameB, body);
    central.push(cd, nameB);
    off += 30 + nameB.length + body.length;
  }
  const cdBuf = Buffer.concat(central);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0);
  end.writeUInt16LE(members.length, 8);
  end.writeUInt16LE(members.length, 10);
  end.writeUInt32LE(cdBuf.length, 12);
  end.writeUInt32LE(off, 16);
  fs.writeFileSync(file, Buffer.concat([...parts, cdBuf, end]));
  return file;
}

describe('index cache', () => {
  const opts = (dir: string, more: SourceOptions = {}): SourceOptions => ({ cacheDir: dir, cacheMinBytes: 0, ...more });

  it('is reused when the file is unchanged', async () => {
    const dir = cacheDir();
    const p = tmpFile('cache.log', bigLog(3000));
    const first = await open(p, opts(dir));
    expect(first.cacheState).toBe('');
    await first.cacheSave;
    expect(fs.readdirSync(dir).filter((f) => f.endsWith('.idx'))).toHaveLength(1);
    const second = await open(p, opts(dir));
    expect(second.cacheState).toBe('hit');
    expect(second.cacheSave).toBeNull();
    sameIndex(first, second);
    expect(second.stats()).toEqual(first.stats());
    expect(second.info().cacheState).toBe('hit');
  });

  it('indexes only the new bytes of a grown file', async () => {
    const dir = cacheDir();
    // The last entry is multi-line and unterminated, so the cached tail must be re-read when the file grows.
    const p = tmpFile('grow.log', bigLog(3000) + '2024-05-02 00:00:00,000 [main] WARN  x - tail\n\tcontinued');
    await (await open(p, opts(dir))).cacheSave;
    fs.appendFileSync(p, ' more\n\tline two\n' + bigLog(500, 90_000));
    const grown = await open(p, opts(dir));
    expect(grown.cacheState).toBe('grown');
    sameIndex(await open(p), grown);
    expect(grown.readEntries(0, grown.entryCount)).toContain('2024-05-02 00:00:00,000 [main] WARN  x - tail\n\tcontinued more\n\tline two');
    await grown.cacheSave;
    expect((await open(p, opts(dir))).cacheState).toBe('hit');
  });

  it('is ignored for a modified file or other settings', async () => {
    const dir = cacheDir();
    const text = bigLog(3000);
    const p = tmpFile('mod.log', text);
    await (await open(p, opts(dir))).cacheSave;
    const mtime = fs.statSync(p).mtime;
    // Same size and modification time, different bytes at the start: the head hash catches it.
    fs.writeFileSync(p, 'X' + text.slice(1));
    fs.utimesSync(p, mtime, mtime);
    let s = await open(p, opts(dir));
    expect(s.cacheState).toBe('');
    expect(s.readEntries(0, 1)[0].startsWith('X')).toBe(true);
    await s.cacheSave;
    // Same bytes, newer modification time.
    const later = new Date(mtime.getTime() + 300_000);
    fs.utimesSync(p, later, later);
    expect((await open(p, opts(dir))).cacheState).toBe('');
    fs.utimesSync(p, mtime, mtime);
    // A shrunk file is a different file.
    fs.writeFileSync(p, text.slice(0, text.length / 2));
    s = await open(p, opts(dir));
    expect(s.cacheState).toBe('');
    await s.cacheSave;
    // A forced format or encoding needs its own index.
    expect((await open(p, opts(dir, { format: 'plain' }))).cacheState).toBe('');
    expect((await open(p, opts(dir, { encoding: 'windows-1252' }))).cacheState).toBe('');
    expect((await open(p, opts(dir, { customFormats: [{ name: 'mine', pattern: '^(?<time>\\S+ \\S+) (?<message>.*)' }] }))).cacheState).toBe('');
    // Small files are not worth a cache file.
    expect((await open(tmpFile('small.log', LOG4J), { cacheDir: dir })).cacheSave).toBeNull();
  });

  it('falls back to a full index when the cache file is corrupt', async () => {
    const dir = cacheDir();
    const p = tmpFile('corrupt.log', bigLog(3000));
    const reference = await open(p, opts(dir));
    await reference.cacheSave;
    const file = cacheFileFor(dir, p);
    const good = fs.readFileSync(file);
    let seed = 5;
    const rnd = (n: number) => ((seed = (seed * 1103515245 + 12345) & 0x7fffffff) % n);
    const variants: Buffer[] = [good.subarray(0, good.length / 2), good.subarray(0, good.length - 1), Buffer.concat([good, Buffer.from([1, 2, 3])]), Buffer.alloc(0), Buffer.from('ULVIDX not really')];
    for (let i = 0; i < 40; i++) {
      const b = Buffer.from(good);
      b[6 + rnd(b.length - 6)] ^= 1 << rnd(8); // one flipped bit anywhere after the magic
      variants.push(b);
    }
    for (const v of variants) {
      fs.writeFileSync(file, v);
      const s = await open(p, opts(dir));
      // Either rejected (full index) or, for a flip that leaves every check intact, still an index of this file.
      if (s.cacheState === 'hit') expect(s.entryCount).toBe(reference.entryCount);
      else sameIndex(reference, s);
      await s.cacheSave;
    }
  });

  it('stays under its size cap', async () => {
    const dir = cacheDir();
    const paths = [0, 1, 2, 3].map((i) => tmpFile(`cap${i}.log`, bigLog(3000, i * 10_000)));
    await (await open(paths[0], opts(dir))).cacheSave;
    const one = cacheSize(dir);
    const old = new Date(Date.now() - 3_600_000);
    fs.utimesSync(cacheFileFor(dir, paths[0]), old, old);
    for (const p of paths.slice(1)) await (await open(p, opts(dir, { cacheMaxBytes: one * 2.5 }))).cacheSave;
    expect(cacheSize(dir)).toBeLessThanOrEqual(one * 2.5);
    expect(fs.readdirSync(dir).filter((f) => f.endsWith('.idx'))).toHaveLength(2);
    expect(fs.existsSync(cacheFileFor(dir, paths[0]))).toBe(false); // the least recently used went first
    expect(fs.existsSync(cacheFileFor(dir, paths[3]))).toBe(true);
    expect(clearCache(dir)).toBeGreaterThan(0);
    expect(cacheSize(dir)).toBe(0);
  });
});

describe('time window', () => {
  it.each([4096, 4 * 1024 * 1024])('opens only a window without cutting multi-line entries (chunk %i)', async (chunkSize) => {
    const p = tmpFile('window.log', bigLog(40_000)); // ~2.6 MB, so the binary search really probes
    const full = await open(p);
    const t0 = local(2024, 5, 1);
    // 7000 s is an entry with a stack trace (7000 % 7 === 0) right at the start boundary.
    const since = t0 + 7_000_000, until = t0 + 21_000_500;
    const w = await open(p, { chunkSize, window: { since, until } });
    const expected: number[] = [];
    for (let e = 0; e < full.entryCount; e++) if (full.time(e) >= since && full.time(e) <= until) expected.push(e);
    expect(w.entryCount).toBe(expected.length);
    expect(w.readEntries(0, 1)).toEqual(full.readEntries(expected[0], expected[0] + 1));
    expect(w.readEntries(0, 1)[0]).toContain('\tat java.lang.Thread.run');
    expect(w.readEntries(w.entryCount - 1, w.entryCount)).toEqual(full.readEntries(expected[expected.length - 1], expected[expected.length - 1] + 1));
    expect(w.partial).toBe(true);
    expect(w.live).toBe(false); // the window ends before the end of the file
    expect([w.windowFrom, w.windowTo]).toEqual([since, until]);
    expect(w.info()).toMatchObject({ partial: true, windowFrom: since });
  });

  it('handles the edges and "last"', async () => {
    const p = tmpFile('edges.log', bigLog(40_000));
    const t0 = local(2024, 5, 1);
    const all = await open(p, { window: { since: t0 - 1000, until: t0 + 100_000_000 } });
    expect(all.partial).toBe(false);
    expect(all.entryCount).toBe(40_000);
    const none = await open(p, { window: { since: t0 + 100_000_000 } });
    expect(none.entryCount).toBe(0);
    expect(none.live).toBe(true); // open-ended: new lines are still tailed
    const last = await open(p, { window: { last: 600_000 } });
    expect(last.entryCount).toBe(601); // the newest entry and the 600 s before it
    expect(last.live && last.partial).toBe(true);
    expect(last.windowFrom).toBe(t0 + 39_999_000 - 600_000);
    fs.appendFileSync(p, bigLog(3, 40_000));
    expect((await last.refresh()).changed).toBe(true);
    expect(last.entryCount).toBe(604);
  });
});

describe('compressed files', () => {
  function bz2Text(): string {
    const lv = ['INFO', 'WARN', 'ERROR'];
    let s = '';
    for (let i = 0; i < 3600; i++) {
      s += `2024-05-01 10:${p2(Math.floor(i / 60) % 60)}:${p2(i % 60)},000 ${lv[i % 3]} app - message ${i % 10}\n`;
      if (i % 10 === 0) s += `\tat X.y(X.java:${i % 5})\n`;
    }
    return s;
  }
  /** bzip2 -1 of bz2Text() (two blocks), made with the reference implementation. */
  const BZ2_A =
  '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' +    '';
  /** A second stream (as pbzip2 or cat-ing two .bz2 files produces). */
  const BZ2_B = 'QlpoOTFBWSZTWWIkklwAAAxdgAAQQAZ2EAEhrgPcACAAIinqfqmnpMyj0RmkKAAaBkyL4RPJ3E1UbMpox8ElGEjIPJ39B/ZjERka6Ln8XckU4UJBiJJJcA==';
  const BZ2_B_TEXT = '2024-05-01 11:00:00,000 INFO app - second stream\n';
  const bz2Bytes = () => Buffer.concat([Buffer.from(BZ2_A, 'base64'), Buffer.from(BZ2_B, 'base64')]);

  function decodeAll(data: Buffer, readSize = 777): Buffer {
    let pos = 0;
    const dec = new Bzip2Decoder((buf) => {
      const n = Math.min(buf.length, data.length - pos);
      data.copy(buf, 0, pos, pos + n);
      pos += n;
      return n;
    });
    const out: Buffer[] = [];
    const chunk = Buffer.alloc(readSize); // odd reads cross run-length and block boundaries
    for (let n; (n = dec.read(chunk)) > 0; ) out.push(Buffer.from(chunk.subarray(0, n)));
    expect(dec.consumed).toBe(data.length);
    return Buffer.concat(out);
  }

  it('decodes multi-block and concatenated bzip2 streams', () => {
    expect(decodeAll(bz2Bytes()).toString()).toBe(bz2Text() + BZ2_B_TEXT);
  });

  it('rejects corrupt bzip2 data', () => {
    const bad = Buffer.from(BZ2_A, 'base64');
    bad[bad.length >> 1] ^= 0x10;
    expect(() => decodeAll(bad)).toThrow();
    expect(() => decodeAll(Buffer.from('plain text'))).toThrow(/Not a bzip2/);
    expect(() => decodeAll(Buffer.from(BZ2_A, 'base64').subarray(0, 4000))).toThrow();
  });

  it('opens bzip2 and zip files', async () => {
    const text = bz2Text() + BZ2_B_TEXT;
    const plain = await open(tmpFile('ref.log', text));
    const bz = await open(tmpFile('a.log.bz2', bz2Bytes()));
    expect([bz.compression, bz.live]).toEqual(['bzip2', false]);
    sameIndex(plain, bz);

    const zip = writeZip(tmpFile('bundle.zip', ''), [
      ['logs/app.log', text],
      ['logs/other.txt', LOG4J],
      ['logs/', null],
      ['logs/screenshot.png', Buffer.from([1, 2, 3])],
      ['empty.log', null],
    ]);
    const members = expandArchives([zip]);
    expect(members).toEqual([memberPath(zip, 'logs/app.log'), memberPath(zip, 'logs/other.txt')]);
    const m = await open(members[0]);
    expect(m.compression).toBe('zip');
    expect(m.info().displayName).toBe('app.log');
    sameIndex(plain, m);
    sameIndex(plain, await open(zip)); // a zip opened directly reads its first text member
    expect((await open(members[1])).entryCount).toBe(6); // a stored (not deflated) member

    const doc = new LogDocument(1, 'bundle');
    expect(await doc.addSources(members, () => ({}))).toEqual([]);
    expect(doc.baseCount()).toBe(plain.entryCount + 6);
    expect(doc.info().sources.map((s) => s.displayName)).toEqual(['app.log', 'other.txt']);
    expect(() => expandArchives([writeZip(tmpFile('empty.zip', ''), [['x.png', 'xx']])])).toThrow(/no text files/);
  });

  it.each([4096, 4 * 1024 * 1024])('indexes gzip while decompressing (chunk %i)', async (chunkSize) => {
    // Big enough that the index runs while the data is still arriving, with an unterminated last line.
    const text = bigLog(60_000) + '2024-05-02 00:00:00,000 [main] INFO  x - no newline';
    const gz = zlib.gzipSync(text, { level: 1 });
    const plain = await open(tmpFile('ref.log', text));
    const seen: number[] = [];
    const g = new FileSource(0, tmpFile('stream.log.gz', gz), { chunkSize });
    await g.open((f) => seen.push(f));
    expect(g.compression).toBe('gzip');
    sameIndex(plain, g);
    expect(g.readEntries(g.entryCount - 1, g.entryCount)[0]).toBe('2024-05-02 00:00:00,000 [main] INFO  x - no newline');
    expect(seen.length).toBeGreaterThan(1);
    const t0 = local(2024, 5, 1);
    const w = await open(tmpFile('w.log.gz', gz), { window: { since: t0 + 1_000_000, until: t0 + 1_999_000 } });
    expect(w.entryCount).toBe(1000);
  });

  it('fails to open a corrupt gzip file', async () => {
    const gz = zlib.gzipSync(bigLog(20_000));
    for (let i = 0; i < 64; i++) gz[Math.floor(gz.length / 3) + i] = 0xff;
    await expect(open(tmpFile('bad.log.gz', gz))).rejects.toThrow();
  });
});

describe('merges', () => {
  it('indexes the files of a merge in parallel', async () => {
    const a = tmpFile('a.log', bigLog(20_000));
    const b = tmpFile('b.log', bigLog(20_000, 5));
    const missing = path.join(os.tmpdir(), `missing-${Date.now()}.log`);
    const doc = new LogDocument(1, 'merged');
    const seen: number[] = [];
    const failed = await doc.addSources([a, missing, b], () => ({ chunkSize: 64 * 1024 }), (_, f) => seen.push(f));
    expect(failed.map((f) => f.path)).toEqual([missing]);
    expect(doc.sources.map((s) => [s.id, s.path])).toEqual([[0, a], [1, b]]);
    expect(doc.baseCount()).toBe(40_000);
    for (const s of doc.info().sources) expect([s.indexing, s.progress]).toEqual([false, 1]);
    expect(doc.getRows(0, 4).map((r) => r.source)).toEqual([0, 0, 0, 0]);
    expect(new Set(doc.getRows(10, 2).map((r) => r.source)).size).toBe(2);
    expect(seen.length).toBeGreaterThan(2);
  });
});

describe('stdin and the command line', () => {
  it('captures stdin into a growing file', async () => {
    const input = new PassThrough();
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'ulv-stdin-'));
    input.write(LOG4J);
    const cap = await captureStdin(input, dir, 5000);
    expect(cap.path.startsWith(dir)).toBe(true);
    const s = await open(cap.path);
    expect(s.format.id).toBe('timestamped');
    expect(s.entryCount).toBe(6);
    expect(s.live).toBe(true);
    input.end('2024-05-01 10:00:06,007 [main] INFO  com.acme.App - Later\n');
    await cap.done;
    expect((await s.refresh()).changed).toBe(true);
    expect(s.entryCount).toBe(7);
    s.close();
    cap.dispose();
    expect(fs.existsSync(cap.path)).toBe(false);
  });

  it('parses the command line', () => {
    const now = local(2024, 5, 1, 12);
    const f = tmpFile('cli.log', 'x');
    let c = parseCommandLine([f, '--since', '-1h', '-', '--until=2024-05-01T11:30', '--unknown'], now);
    expect(c.files).toEqual([f]);
    expect(c.stdin).toBe(true);
    expect(c.window).toEqual({ since: now - 3_600_000, until: local(2024, 5, 1, 11, 30), last: null });
    expect(c.errors).toEqual([]);
    c = parseCommandLine(['--last', '15m'], now);
    expect(c.window).toEqual({ since: null, until: null, last: 900_000 });
    expect(parseCommandLine(['--since', 'yesterday-ish', '--last'], now).errors).toHaveLength(2);
    expect(parseCommandLine([f], now).window).toBeNull();
  });

  it('parses durations and points in time', () => {
    const cases: [string, number | null][] = [['90s', 90_000], ['15m', 900_000], ['1h30m', 5_400_000], ['2d', 172_800_000], ['1.5h', 5_400_000], ['1w', 604_800_000], ['h', null], ['10', null], ['-1h', null]];
    for (const [s, ms] of cases) expect(parseDuration(s)).toBe(ms);
    const now = local(2024, 5, 1, 12);
    expect(parsePoint('now', now)).toBe(now);
    expect(parsePoint('-30m', now)).toBe(now - 1_800_000);
    expect(parsePoint('2024-04-30', now)).toBe(local(2024, 4, 30));
    expect(parsePoint('2024-04-30 10:05', now)).toBe(local(2024, 4, 30, 10, 5));
    expect(parsePoint('2024-04-30T10:05:07', now)).toBe(local(2024, 4, 30, 10, 5, 7));
    expect(parsePoint('9:15', now)).toBe(local(2024, 5, 1, 9, 15));
    expect(parsePoint('2024-04-30T10:00:00Z', now)).toBe(Date.UTC(2024, 3, 30, 10));
    expect(parsePoint('soon', now)).toBeNull();
    expect(parsePoint('25:00', now)).toBeNull();
    expect(describeWindow(local(2024, 5, 1, 10), local(2024, 5, 1, 11))).toBe('2024-05-01 10:00–11:00');
    expect(describeWindow(local(2024, 5, 1, 10), null)).toBe('since 2024-05-01 10:00');
  });
});
