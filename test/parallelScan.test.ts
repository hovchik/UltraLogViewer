// Search, filtering, grouping, patterns, field stats and export read and test entry batches on the scan pool's worker
// threads and merge the results in order. Whatever the batch size, with or without workers, they must give exactly what
// a sequential pass on the engine thread gives.

import * as esbuild from 'esbuild';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { LogDocument } from '../src/core/document';
import { splitEntries } from '../src/core/entryScan';
import { FileSource } from '../src/core/fileSource';
import { ScanPool } from '../src/core/scanPool';
import { FilterRule, FilterSpec, Level, TextPattern } from '../src/core/types';
import { tmpFile } from './helpers';

function rnd(seed: number) {
  return () => ((seed = (seed * 1103515245 + 12345) & 0x7fffffff) / 0x7fffffff);
}

/** Multi-line entries, CRLF and stray CRs, a header that is not an entry, long lines and an unterminated tail. */
function messy(n = 3000, seed = 5, minute = 0): string {
  const r = rnd(seed);
  const pick = <T>(a: T[]) => a[Math.floor(r() * a.length)];
  const p2 = (x: number) => String(x).padStart(2, '0');
  let s = 'header that is not an entry\r\nsecond header\n';
  for (let i = 0; i < n; i++) {
    const kind = Math.floor(r() * 6);
    const msg =
      kind === 0 ? `request ${Math.floor(r() * 1000)} timed out after ${Math.floor(r() * 5000)}ms`
      : kind === 1 ? `payment id=${Math.floor(r() * 1e9).toString(16)}-${i} amount=${Math.floor(r() * 100)}.${p2(Math.floor(r() * 100))} ok`
      : kind === 2 ? `user "u${Math.floor(r() * 50)}" logged in from 10.0.${Math.floor(r() * 256)}.${Math.floor(r() * 256)}`
      : kind === 3 ? 'cache warm'
      : kind === 4 ? `msg ${i} ${'é'.repeat(Math.floor(r() * 80))}`
      : `Timed Out waiting for lock ${Math.floor(r() * 10)}`;
    const t = `2024-05-01 10:${p2((minute + Math.floor(i / 60)) % 60)}:${p2(i % 60)},${String(i % 1000).padStart(3, '0')}`;
    s += `${t} [t${i % 3}] ${pick(['INFO', 'DEBUG', 'WARN', 'ERROR', 'INFO', 'TRACE'])} ${pick(['com.acme.Db', 'com.acme.Api', 'org.pay.Payment', 'c.x'])} - ${msg}`;
    s += r() < 0.25 ? '\r\n' : '\n';
    if (r() < 0.16) s += 'java.lang.IllegalStateException: timed out\n\tat com.acme.Db.query(Db.java:42)\r\n\tat x.y(z.java:1)\n';
    if (r() < 0.012) s += 'x'.repeat(100 + Math.floor(r() * 6000)) + ' payment\n';
    if (r() < 0.025) s += 'no timestamp here WARN\r\r\n';
    if (r() < 0.03) s += 'cache warm\n';
  }
  return s + '2024-05-01 11:00:00,000 [t0] ERROR c.x - unterminated timed out';
}

const allLevels = () => [true, true, true, true, true, true, true];
const spec = (over: Partial<FilterSpec> = {}): FilterSpec => ({ levels: allLevels(), rules: [], ...over });
const rule = (text: string, extra: Partial<FilterRule> = {}): FilterRule => ({ id: text, text, mode: 'include', enabled: true, regex: false, caseSensitive: false, wholeWord: false, ...extra });
const pat = (text: string, extra: Partial<TextPattern> = {}): TextPattern => ({ text, regex: false, caseSensitive: false, wholeWord: false, ...extra });

function specs(): FilterSpec[] {
  const noDebug = allLevels();
  noDebug[Level.Debug] = false;
  return [
    spec({ query: 'payment' }),
    spec({ query: 'timed out', context: 2 }),
    spec({ query: 'logger:Db OR level:error -warm' }),
    spec({ rules: [rule('timed out'), rule('lock', { mode: 'exclude' })] }),
    spec({ rules: [rule('t[12]\\]', { regex: true })], context: 1 }),
    spec({ rules: [rule('Payment', { column: 'logger' })] }),
    spec({ rules: [rule('cache warm', { patternMatch: true })] }),
    spec({ query: 'timed out', levels: noDebug }),
    spec({ query: 'timed out', groupRepeats: 'pattern' }),
    spec({ query: 'warm OR lock', groupRepeats: 'exact', context: 1 }),
    spec({ query: 'level:error or since:2024-05-01T10:40' }),
    spec({ query: 'not logger:Db and until:2024-05-01T10:10 timed' }),
  ];
}

const rows = (d: LogDocument) =>
  d
    .getRows(0, d.viewCount())
    .map((r) => `${r.key}${r.context ? 'c' : ''}${r.repeat > 1 ? 'x' + r.repeat : ''}`)
    .join(';');

let dir = '';
let pool: ScanPool;
beforeAll(async () => {
  // The pool runs the bundled worker script, as the app does.
  dir = fs.mkdtempSync(path.join(os.tmpdir(), 'ulv-par-'));
  await esbuild.build({ entryPoints: ['src/main/scanWorker.ts'], bundle: true, platform: 'node', format: 'cjs', outfile: path.join(dir, 'scanWorker.js'), logLevel: 'silent' });
  pool = new ScanPool(path.join(dir, 'scanWorker.js'), 3);
});
afterAll(() => {
  pool.close();
  fs.rmSync(dir, { recursive: true, force: true });
});

async function open(files: string[], batch: number, scanPool?: ScanPool) {
  const d = new LogDocument(1, 't');
  d.scanBatch = batch;
  for (const f of files) await d.addSource(f, { scanPool });
  return d;
}

/** Every scan the document offers, as strings. */
async function everything(d: LogDocument, prefix: string): Promise<string[]> {
  const out: string[] = [];
  for (const p of [pat('timed out'), pat('TIMED', { caseSensitive: true }), pat('\\d{4}ms', { regex: true }), pat('lock', { wholeWord: true })])
    out.push(JSON.stringify(await d.search(p)));
  let i = 0;
  for (const s of specs()) {
    await d.applyFilter(s);
    out.push(rows(d));
    out.push(JSON.stringify(await d.search(pat('timed out'))));
    d.ignoreFilter = true; // show in context
    out.push(rows(d));
    out.push(JSON.stringify(await d.search(pat('payment'))));
    d.ignoreFilter = false;
    out.push(JSON.stringify(await d.patterns(900, 50)));
    out.push(JSON.stringify(await d.fieldStats(700)));
    for (const fmt of ['text', 'csv', 'jsonl'] as const) {
      const file = `${prefix}${i++}.${fmt}`;
      await d.export(file, fmt);
      out.push(fs.readFileSync(file, 'utf-8'));
    }
  }
  await d.applyFilter(null);
  out.push(JSON.stringify(await d.patterns()));
  out.push(JSON.stringify(await d.fieldStats()));
  return out;
}

describe('parallel find and filter', () => {
  it('gives the sequential result for every scan, any batch size, with or without workers', async () => {
    const file = tmpFile('messy.log', messy());
    const reference = await everything(await open([file], 1 << 20), path.join(dir, 'ref-'));
    expect(reference.some((s) => /c;/.test(s))).toBe(true); // context rows were produced
    for (const batch of [2, 64, 4096]) {
      for (const scanPool of [pool, undefined]) {
        const actual = await everything(await open([file], batch, scanPool), path.join(dir, `${batch}-${!!scanPool}-`));
        expect(actual.length).toBe(reference.length);
        actual.forEach((a, k) => expect(a, `batch ${batch}, pool ${!!scanPool}, result ${k}`).toBe(reference[k]));
      }
    }
  });

  it('evaluates since:/until: inside or/not on worker threads', async () => {
    const file = tmpFile('messy-time.log', messy());
    for (const scanPool of [pool, undefined]) {
      const d = await open([file], 64, scanPool);
      await d.applyFilter(spec({ query: 'level:error' }));
      const errors = d.viewCount();
      await d.applyFilter(spec({ query: 'since:2024-05-01T10:40' }));
      const late = d.viewCount();
      await d.applyFilter(spec({ query: 'level:error or since:2024-05-01T10:40' }));
      const either = d.viewCount();
      expect(late).toBeGreaterThan(0);
      expect(either).toBeGreaterThan(errors);
      expect(either).toBeLessThan(errors + late + 1);
      d.close();
    }
  });

  it('gives the sequential result in merged views', async () => {
    const a = tmpFile('a.log', messy(1500, 8));
    const b = tmpFile('b.log', messy(1200, 9, 30));
    const reference = await everything(await open([a, b], 1 << 20), path.join(dir, 'mref-'));
    for (const batch of [5, 300]) expect(await everything(await open([a, b], batch, pool), path.join(dir, `m${batch}-`))).toEqual(reference);
  });

  it('keeps tail filtering equal to a sequential run', async () => {
    const content = messy(2000, 11);
    const cut = content.indexOf('\n2024-05-01 10:20') + 1;
    const fSeq = tmpFile('tail-seq.log', content.slice(0, cut));
    const fPar = tmpFile('tail-par.log', content.slice(0, cut));
    const seq = await open([fSeq], 1 << 20);
    const par = await open([fPar], 3, pool);
    const s = spec({ query: 'timed out OR payment', context: 1 });
    await seq.applyFilter(s);
    await par.applyFilter(s);
    expect(rows(par)).toBe(rows(seq));
    // Grows in a few steps, the first one mid-entry (continuation lines arrive later).
    const rest = content.slice(cut);
    for (const piece of [rest.slice(0, 40), rest.slice(40, rest.length >> 1), rest.slice(rest.length >> 1)]) {
      fs.appendFileSync(fSeq, piece);
      fs.appendFileSync(fPar, piece);
      expect(await seq.refresh()).toBe(true);
      expect(await par.refresh()).toBe(true);
      expect(rows(par)).toBe(rows(seq));
    }
    const whole = await open([tmpFile('whole.log', content)], 1 << 20);
    await whole.applyFilter(s);
    expect(rows(par)).toBe(rows(whole));
  });

  it('builds context rows exactly like the simple algorithm', async () => {
    const d = await open([tmpFile('ctx.log', messy(2500, 31))], 64, pool);
    const texts = d.sources[0].readEntries(0, d.baseCount());
    for (const needle of ['timed out', 'payment', 'cache warm']) {
      const hit = texts.map((t) => t.toLowerCase().includes(needle));
      for (const context of [0, 1, 2, 5]) {
        await d.applyFilter(spec({ rules: [rule(needle)], context }));
        // grep -C as it was built before views could be built progressively
        const expected: string[] = [];
        let lastAdded = -1;
        for (let b = 0; b < hit.length; b++) {
          if (!hit[b]) continue;
          for (let c = Math.max(lastAdded + 1, b - context); c < b; c++) expected.push(`0:${c}c`);
          expected.push(`0:${b}`);
          lastAdded = b;
          for (let c = b + 1; c <= Math.min(hit.length - 1, b + context) && !hit[c]; c++) {
            expected.push(`0:${c}c`);
            lastAdded = c;
          }
        }
        expect(rows(d)).toBe(expected.join(';'));
      }
    }
  });

  it('falls back to the engine thread when the pool is broken', async () => {
    const broken = new ScanPool(path.join(dir, 'missing.js'), 2);
    const file = tmpFile('m.log', messy(800, 2));
    const reference = await everything(await open([file], 1 << 20), path.join(dir, 'bref-'));
    expect(await everything(await open([file], 50, broken), path.join(dir, 'b-'))).toEqual(reference);
    broken.close();
  });

  it('reads a file replaced under the same name through its own handle', async () => {
    const file = tmpFile('rot.log', messy(600, 4));
    const d = await open([file], 40, pool);
    const before = JSON.stringify(await d.search(pat('timed out')));
    fs.renameSync(file, file + '.1'); // rotated: the index still describes the old file
    fs.writeFileSync(file, 'something else entirely\n'.repeat(5000));
    expect(JSON.stringify(await d.search(pat('timed out')))).toBe(before);
  });
});

describe('progressive filtering', () => {
  it('shows rows while filtering, and they never move', async () => {
    for (const context of [0, 2]) {
      const d = await open([tmpFile('p.log', messy(4000, 13))], 50, pool);
      d.partialViewMs = 0; // publish after every batch
      const startEpoch = d.viewEpoch;
      const partials: { rows: string; epoch: number }[] = [];
      await d.applyFilter(spec({ query: 'timed out', context }), false, () => partials.push({ rows: rows(d), epoch: d.viewEpoch }));
      const final = rows(d);
      const shown = partials.filter((p) => p.epoch !== startEpoch);
      expect(shown.length).toBeGreaterThan(5);
      expect(shown.some((p) => p.rows.length > 0 && p.rows.length < final.length)).toBe(true);
      for (const p of shown) {
        expect(final.startsWith(p.rows)).toBe(true); // every partial view is a prefix of the final one ...
        expect(p.epoch).toBe(d.viewEpoch); // ... so all of them, and the final view, share one epoch
      }
      for (let k = 1; k < shown.length; k++) expect(shown[k].rows.startsWith(shown[k - 1].rows)).toBe(true);
    }
  });

  it('only shows the settled top of a merged view while filtering', async () => {
    const d = await open([tmpFile('a.log', messy(1500, 21)), tmpFile('b.log', messy(1500, 22))], 40, pool);
    d.partialViewMs = 0;
    const partials: string[] = [];
    await d.applyFilter(spec({ query: 'payment', context: 1 }), false, () => partials.push(rows(d)));
    const final = rows(d);
    expect(partials.some((p) => p.length > 0 && p.length < final.length)).toBe(true);
    for (const p of partials.slice(1)) expect(final.startsWith(p)).toBe(true);
  });

  it('swaps a grouped view only at the end', async () => {
    const d = await open([tmpFile('g.log', messy(2000, 17))], 30, pool);
    d.partialViewMs = 0;
    const before = d.viewEpoch;
    const epochs = new Set<number>();
    await d.applyFilter(spec({ query: 'warm', groupRepeats: 'exact' }), false, () => epochs.add(d.viewEpoch));
    expect([...epochs]).toEqual([before]);
    expect(d.viewEpoch).not.toBe(before);
    expect(rows(d)).toContain('x');
  });
});

describe('entry texts', () => {
  it('split like the lines they are made of', () => {
    expect(splitEntries('a\r\nb\n\tc\r\r\nd', [1, 2, 1])).toEqual(['a', 'b\n\tc\r', 'd']);
    expect(splitEntries('a\n', [1, 1, 2])).toEqual(['a', '', '']); // the file shrank under us
  });

  it('keep a byte-order mark that starts a batch', async () => {
    const file = tmpFile('bom.log', '2024-05-01 10:00:00 INFO a\n﻿2024-05-01 10:00:01 INFO b\n');
    const s = new FileSource(0, file, {});
    await s.open();
    expect(s.readEntries(1, 2)).toEqual(s.readEntries(0, 2).slice(1));
    s.close();
  });
});
