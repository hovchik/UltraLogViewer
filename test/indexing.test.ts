import * as esbuild from 'esbuild';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { LogDocument } from '../src/core/document';
import { FileSource, SourceOptions } from '../src/core/fileSource';
import { ScanPool } from '../src/core/scanPool';
import { Level } from '../src/core/types';
import { tmpFile } from './helpers';

function rnd(seed: number) {
  return () => ((seed = (seed * 1103515245 + 12345) & 0x7fffffff) / 0x7fffffff);
}

function messy(): string {
  const r = rnd(3);
  let s = 'header that is not an entry\r\nsecond header\n';
  for (let i = 0; i < 4000; i++) {
    const p2 = (n: number) => String(n).padStart(2, '0');
    s += `2024-05-01 10:${p2(Math.floor(i / 60) % 60)}:${p2(i % 60)},${String(i % 1000).padStart(3, '0')} [t${i % 3}] ${i % 7 ? 'INFO' : 'ERROR'} c.x - msg ${i} ${'é'.repeat(Math.floor(r() * 200))}\n`;
    if (r() < 0.2) s += '\tat frame\r\n';
    if (r() < 0.01) s += 'x'.repeat(100 + Math.floor(r() * 9000)) + '\n';
    if (r() < 0.02) s += 'no timestamp here WARN\n';
  }
  return s + '2024-05-01 11:00:00,000 ERROR unterminated';
}

function jsonLines(): string {
  let s = 'not json first\n';
  for (let i = 0; i < 3000; i++) {
    const lv = ['info', 'warn', 'error', 30, 'F'][i % 5];
    s += i % 13 === 0 ? 'plain error: here\n' : JSON.stringify({ '@timestamp': `2024-05-01T10:00:${String(i % 60).padStart(2, '0')}Z`, level: lv, message: `m ${i}` }) + '\n';
  }
  return s;
}

async function index(file: string, opts: SourceOptions) {
  const s = new FileSource(0, file, opts);
  await s.open();
  const out: [number, number, Level, number][] = [];
  for (let e = 0; e < s.entryCount; e++) {
    const [a, b] = s.entryLines(e);
    out.push([a, b, s.level(e), s.time(e)]);
  }
  const lines = s.lineCount;
  s.close();
  return { out, lines };
}

describe('parallel indexing', () => {
  let dir = '';
  let pool: ScanPool;
  beforeAll(async () => {
    // The pool runs the bundled worker script, as the app does.
    dir = fs.mkdtempSync(path.join(os.tmpdir(), 'ulv-scan-'));
    await esbuild.build({ entryPoints: ['src/main/scanWorker.ts'], bundle: true, platform: 'node', format: 'cjs', outfile: path.join(dir, 'scanWorker.js'), logLevel: 'silent' });
    pool = new ScanPool(path.join(dir, 'scanWorker.js'), 3);
  });
  afterAll(() => {
    pool.close();
    fs.rmSync(dir, { recursive: true, force: true });
  });

  for (const [name, content] of [['messy.log', messy()], ['json.log', jsonLines()]] as const) {
    it(`gives the same index for any chunk size, with or without worker threads (${name})`, async () => {
      const file = tmpFile(name, content);
      const reference = await index(file, { chunkSize: 64 << 20 });
      expect(reference.out[0].slice(0, 2)).toEqual([0, name === 'messy.log' ? 1 : 0]); // header lines form the first entry
      for (const chunkSize of [64, 1000, 4096, 65536]) {
        expect(await index(file, { chunkSize })).toEqual(reference);
        expect(await index(file, { chunkSize, scanPool: pool })).toEqual(reference);
      }
    });
  }

  it('falls back to scanning on its own thread when the pool is broken', async () => {
    const broken = new ScanPool(path.join(dir, 'missing.js'), 2);
    const file = tmpFile('m.log', messy());
    const reference = await index(file, { chunkSize: 64 << 20 });
    expect(await index(file, { chunkSize: 4096, scanPool: broken })).toEqual(reference);
    broken.close();
  });
});

describe('document stats', () => {
  it('follow appends and truncation', async () => {
    const file = tmpFile('stats.log', '2024-01-01 00:00:01 INFO a\n2024-01-01 00:00:05 ERROR b\n');
    const d = new LogDocument(1, 't');
    await d.addSource(file, {});
    expect(d.info().levelCounts[Level.Info]).toBe(1);
    expect(d.info().levelCounts[Level.Error]).toBe(1);
    fs.appendFileSync(file, '2024-01-01 00:00:09 WARN c\n');
    expect(await d.refresh()).toBe(true);
    let info = d.info();
    expect(info.levelCounts).toEqual([0, 0, 0, 1, 1, 1, 0]);
    expect(info.timeMax! - info.timeMin!).toBe(8000);
    fs.writeFileSync(file, '2024-01-01 00:00:03 DEBUG new\n'); // truncated
    expect(await d.refresh()).toBe(true);
    info = d.info();
    expect(info.levelCounts.reduce((a, b) => a + b, 0)).toBe(1);
    expect(info.timeMin).toBe(info.timeMax);
    d.close();
  });
});
