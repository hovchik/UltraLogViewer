import { describe, expect, it } from 'vitest';
import * as fs from 'node:fs';
import { LogDocument } from '../src/core/document';
import { AnalyticsJob, durationMs, findBursts } from '../src/core/analytics';
import { template } from '../src/core/patterns';
import { AnalyticsResult, Level, LEVEL_COUNT } from '../src/core/types';
import { tmpFile } from './helpers';

const pad = (n: number, w = 2) => String(n).padStart(w, '0');
/** 2024-05-01 10:mm:ss,000 */
const ts = (sec: number) => `2024-05-01 ${pad(10 + Math.floor(sec / 3600))}:${pad(Math.floor(sec / 60) % 60)}:${pad(sec % 60)},000`;

function sampleLog(): string {
  const out: string[] = [];
  for (let i = 0; i < 600; i++) {
    const sec = i * 5;
    if (i % 10 === 0) out.push(`${ts(sec)} [worker-${i % 3}] WARN  com.acme.Cache - Cache miss for key ${i}`);
    else out.push(`${ts(sec)} [main] INFO  com.acme.Http - GET https://api.acme.io/users/${i}?x=1 from 10.0.0.${i % 4} took ${i % 50} ms`);
  }
  // a burst of errors at the end, with a stack trace
  for (let i = 0; i < 40; i++) {
    out.push(`${ts(3000 + i)} [worker-1] ERROR com.acme.Db - Query ${i} failed`);
    out.push('java.sql.SQLTimeoutException: timeout');
    out.push('\tat com.acme.Db.query(Db.java:42)');
  }
  return out.join('\n') + '\n';
}

async function analyse(content: string, opts: { busy?: () => boolean } = {}) {
  const d = new LogDocument(1, 'test');
  await d.addSource(tmpFile('a.log', content), {});
  const snaps: AnalyticsResult[] = [];
  let done!: (r: AnalyticsResult) => void;
  const finished = new Promise<AnalyticsResult>((r) => (done = r));
  const job = new AnalyticsJob(d, {
    busy: opts.busy ?? (() => false),
    emit: (r) => {
      snaps.push(r);
      if (r.state === 'done') done(r);
    },
    batch: 100,
    slice: 50,
    emitMs: 0,
  });
  return { d, job, snaps, finished, run: () => (job.start(), finished) };
}

describe('analytics helpers', () => {
  it('parses durations', () => {
    expect(durationMs('120ms')).toBe(120);
    expect(durationMs('1.5s')).toBe(1500);
    expect(durationMs('2min')).toBe(120_000);
    expect(durationMs('3x')).toBeNaN();
  });
  it('finds error bursts against the typical bucket', () => {
    const counts = Array.from({ length: 20 }, (_, i) => {
      const c = new Array(LEVEL_COUNT).fill(0);
      c[Level.Info] = 10;
      c[Level.Error] = i === 15 || i === 16 ? 50 : 1;
      return c;
    });
    const b = findBursts({ start: 0, bucketMs: 1000, counts });
    expect(b).toHaveLength(1);
    expect(b[0]).toMatchObject({ from: 15_000, to: 17_000, errors: 100 });
    expect(b[0].factor).toBeGreaterThan(10);
  });
});

describe('analytics job', () => {
  it('analyses the whole log with incremental snapshots', async () => {
    const a = await analyse(sampleLog());
    const r = await a.run();
    expect(r.state).toBe('done');
    expect(r.progress).toBe(1);
    expect(r.scanned).toBe(640);
    expect(r.total).toBe(640);
    // snapshots while running, with progress growing
    const running = a.snaps.filter((s) => s.state === 'running');
    expect(running.length).toBeGreaterThan(3);
    for (let i = 1; i < running.length; i++) expect(running[i].progress).toBeGreaterThanOrEqual(running[i - 1].progress);

    expect(r.levels[Level.Info]).toBe(540);
    expect(r.levels[Level.Warn]).toBe(60);
    expect(r.levels[Level.Error]).toBe(40);
    expect(r.summary.multiline).toBe(40);
    expect(r.summary.timed).toBe(640);
    expect(r.timeline!.counts.reduce((n, c) => n + c.reduce((a, b) => a + b, 0), 0)).toBe(640);

    const top = r.patterns[0];
    expect(top.count).toBe(540);
    expect(top.template).toBe(template('GET https://api.acme.io/users/1?x=1 from 10.0.0.1 took 1 ms'));
    expect(top.durCount).toBe(540);
    expect(top.spark.reduce((a, b) => a + b, 0)).toBe(540);
    expect(r.errorPatterns[0].template).toBe('Query <num> failed');
    expect(r.errorPatterns[0].levels[Level.Error]).toBe(40);
    // the errors only start at the end: a new pattern, and a burst
    expect(r.newPatterns.map((p) => p.template)).toContain('Query <num> failed');
    expect(r.bursts.length).toBeGreaterThan(0);
    expect(r.exceptions[0]).toMatchObject({ name: 'java.sql.SQLTimeoutException', count: 40 });

    const thread = r.fields.find((f) => f.key === 'thread')!;
    expect(thread.top[0]).toEqual(['main', 540]);
    expect(r.ips.map((x) => x[0]).sort()).toEqual(['10.0.0.0', '10.0.0.1', '10.0.0.2', '10.0.0.3']);
    expect(r.urls[0][0]).toMatch(/^https:\/\/api\.acme\.io\/users\/\d+$/);
    expect(r.methods[0]).toEqual(['GET', 540]);
    expect(r.durations!.count).toBe(540);
    expect(r.durations!.max).toBe(49);
    expect(r.sources).toHaveLength(1);
    // the log goes quiet between the last request (2995 s) and the errors (3000 s)? no: 5 s like the rest
    expect(r.gaps.every((g) => g.ms >= 1000)).toBe(true);
    a.d.close();
  });

  it('waits while the engine is busy and continues afterwards', async () => {
    let busy = true;
    const a = await analyse(sampleLog(), { busy: () => busy });
    a.job.start();
    await new Promise((r) => setTimeout(r, 400));
    expect(a.job.snapshot().scanned).toBe(0);
    expect(a.job.snapshot().state).toBe('waiting');
    busy = false;
    const r = await a.finished;
    expect(r.scanned).toBe(640);
    a.d.close();
  });

  it('pauses, resumes and restarts', async () => {
    const a = await analyse(sampleLog());
    a.job.start();
    a.job.pause();
    expect(a.job.snapshot().state).toBe('paused');
    a.job.start();
    const r = await a.finished;
    expect(r.scanned).toBe(640);
    const run = r.run;
    let again!: (r: AnalyticsResult) => void;
    const p = new Promise<AnalyticsResult>((res) => (again = res));
    const job2 = new AnalyticsJob(a.d, { busy: () => false, emit: (x) => x.state === 'done' && again(x), batch: 100, emitMs: 0 });
    job2.start();
    job2.restart();
    expect((await p).scanned).toBe(640);
    expect(run).toBe(1);
    job2.cancel();
    a.d.close();
  });

  it('reads only the new entries when the file grows, without counting the re-read last entry twice', async () => {
    const file = tmpFile('tail.log', `${ts(0)} [main] INFO  a.B - hello 1\n${ts(1)} [main] INFO  a.B - hello 2\n`);
    const d = new LogDocument(1, 't');
    await d.addSource(file, {});
    let resolve: (r: AnalyticsResult) => void = () => undefined;
    const job = new AnalyticsJob(d, { busy: () => false, emit: (r) => r.state === 'done' && resolve(r), emitMs: 0 });
    let r = await new Promise<AnalyticsResult>((res) => ((resolve = res), job.start()));
    expect(r.scanned).toBe(2);
    fs.appendFileSync(file, `${ts(2)} [main] ERROR a.B - boom 3\n`);
    const next = new Promise<AnalyticsResult>((res) => (resolve = res));
    await d.refresh();
    r = await next;
    expect(r.scanned).toBe(3);
    expect(r.patterns.find((p) => p.template === 'hello <num>')!.count).toBe(2);
    expect(r.errorPatterns[0].template).toBe('boom <num>');
    expect(r.run).toBe(1); // no restart
    job.cancel();
    d.close();
  });
});
