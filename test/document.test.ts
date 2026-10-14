import { describe, expect, it } from 'vitest';
import * as fs from 'node:fs';
import { LogDocument } from '../src/core/document';
import { FieldStats, FilterSpec, Level } from '../src/core/types';
import { LOG4J, tmpFile } from './helpers';

const allLevels = () => [true, true, true, true, true, true, true];
const spec = (over: Partial<FilterSpec> = {}): FilterSpec => ({ levels: allLevels(), rules: [], ...over });
const rule = (text: string, mode: 'include' | 'exclude', extra = {}) => ({ id: text, text, mode, enabled: true, regex: false, caseSensitive: false, wholeWord: false, ...extra });

const entries = (d: LogDocument) => d.getRows(0, 100).map((r) => r.entry);

async function doc(content = LOG4J) {
  const d = new LogDocument(1, 'test');
  await d.addSource(tmpFile('x.log', content), {});
  return d;
}

describe('LogDocument', () => {
  it('returns parsed rows', async () => {
    const d = await doc();
    expect(d.viewCount()).toBe(6);
    const rows = d.getRows(2, 2);
    expect(rows.map((r) => r.fields.level)).toEqual(['WARN', 'ERROR']);
    expect(rows[1]).toMatchObject({ line: 4, lineCount: 4, level: Level.Error, key: '0:3' });
    expect(d.info().levelCounts[Level.Info]).toBe(2);
  });

  it('filters by level, include/exclude rules, columns, query and time', async () => {
    const d = await doc();
    const levels = allLevels();
    levels[Level.Info] = false;
    levels[Level.Debug] = false;
    await d.applyFilter(spec({ levels }));
    expect(d.getRows(0, 10).map((r) => r.entry)).toEqual([2, 3, 5]);

    await d.applyFilter(spec({ rules: [rule('db', 'include')] }));
    expect(d.getRows(0, 10).map((r) => r.entry)).toEqual([1, 3]);

    await d.applyFilter(spec({ rules: [rule('db', 'include'), rule('timeout', 'exclude')] }));
    expect(d.getRows(0, 10).map((r) => r.entry)).toEqual([1]);

    await d.applyFilter(spec({ rules: [rule('main', 'include', { column: 'thread' })] }));
    expect(d.getRows(0, 10).map((r) => r.entry)).toEqual([0, 1, 4, 5]);

    await d.applyFilter(spec({ query: 'logger:Db OR level:fatal' }));
    expect(d.getRows(0, 10).map((r) => r.entry)).toEqual([1, 3, 5]);

    const t = (s: number) => new Date(2024, 4, 1, 10, 0, s).getTime();
    await d.applyFilter(spec({ timeFrom: t(2), timeTo: t(5) - 1 }));
    expect(d.getRows(0, 10).map((r) => r.entry)).toEqual([2, 3, 4]);

    await d.applyFilter(spec({ bookmarksOnly: true, bookmarks: ['0:5', '0:0'] }));
    expect(d.getRows(0, 10).map((r) => r.entry)).toEqual([0, 5]);

    await d.applyFilter(null);
    expect(d.viewCount()).toBe(6);
  });

  it('adds context rows around matches', async () => {
    const d = await doc();
    await d.applyFilter(spec({ rules: [rule('Cache', 'include')], context: 1 }));
    const rows = d.getRows(0, 10);
    expect(rows.map((r) => [r.entry, !!r.context])).toEqual([[1, true], [2, false], [3, true]]);
  });

  it('searches within the current view and navigates', async () => {
    const d = await doc();
    expect((await d.search({ text: 'acme.db', regex: false, caseSensitive: false, wholeWord: false })).rows).toEqual([1, 3]);
    expect((await d.search({ text: 'Db\\.java:\\d+', regex: true, caseSensitive: true, wholeWord: false })).rows).toEqual([3]);
    const levels = allLevels();
    levels[Level.Debug] = false;
    await d.applyFilter(spec({ levels }));
    expect((await d.search({ text: 'acme.db', regex: false, caseSensitive: false, wholeWord: false })).rows).toEqual([2]);
    await d.applyFilter(null);
    expect(d.findLevel(0, 1, Level.Error)).toBe(3);
    expect(d.findLevel(3, 1, Level.Error)).toBe(5);
    expect(d.findLevel(5, -1, Level.Warn)).toBe(2);
    expect(d.rowForLine(0, 6)).toBe(3);
    expect(d.rowForTime(new Date(2024, 4, 1, 10, 0, 3, 500).getTime())).toBe(4);
  });

  it('merges files by timestamp', async () => {
    const a = tmpFile('a.log', '2024-01-01 00:00:01 INFO a1\n2024-01-01 00:00:03 INFO a3\n');
    const b = tmpFile('b.log', '2024-01-01 00:00:02 ERROR b2\n2024-01-01 00:00:04 INFO b4\n');
    const d = new LogDocument(1, 'merged');
    await d.addSource(a, {});
    await d.addSource(b, {});
    expect(d.getRows(0, 10).map((r) => r.fields.message)).toEqual(['a1', 'b2', 'a3', 'b4']);
    expect(d.info().columns[0].key).toBe('__source');
    await d.applyFilter(spec({ rules: [rule('b4', 'include')] }));
    expect(d.getRows(0, 10).map((r) => r.key)).toEqual(['1:1']);
    expect(d.rowForKey('1:1')).toBe(0);
    fs.appendFileSync(a, '2024-01-01 00:00:05 INFO a5 b4\n');
    expect(await d.refresh()).toBe(true);
    expect(d.getRows(0, 10).map((r) => r.key)).toEqual(['1:1', '0:2']);
  });

  it('builds histogram, overview and exports', async () => {
    const d = await doc();
    const h = d.histogram(10)!;
    expect(h.bucketMs).toBe(1000);
    expect(h.counts.reduce((n, c) => n + c[0] + c[1] + c[2], 0)).toBe(6);
    expect(h.counts[3][2]).toBe(1);
    expect(d.overview(3)).toEqual([0, 3, 2]);
    const out = tmpFile('out.csv', '');
    await d.export(out, 'csv');
    const csv = fs.readFileSync(out, 'utf-8').trim().split('\n');
    expect(csv[0]).toBe('"line","Time","Level","Thread","Logger","Message"');
    expect(csv[1]).toContain('"Starting app"');
    const txt = tmpFile('out.txt', '');
    await d.export(txt, 'text');
    expect(fs.readFileSync(txt, 'utf-8')).toBe(LOG4J);
  });

  it('keeps filters current while tailing', async () => {
    const p = tmpFile('t.log', '2024-01-01 00:00:01 INFO ok\n');
    const d = new LogDocument(1, 't');
    await d.addSource(p, {});
    await d.applyFilter(spec({ rules: [rule('boom', 'include')] }));
    expect(d.viewCount()).toBe(0);
    fs.appendFileSync(p, '2024-01-01 00:00:02 ERROR boom\n2024-01-01 00:00:03 INFO fine\n');
    await d.refresh();
    expect(d.getRows(0, 5).map((r) => r.entry)).toEqual([1]);
  });

  it('matches exact rules against the whole value', async () => {
    const d = await doc();
    await d.applyFilter(spec({ rules: [rule('com.acme.App', 'include', { column: 'logger', exact: true })] }));
    expect(entries(d)).toEqual([0, 4, 5]);
    await d.applyFilter(spec({ rules: [rule('com.acme', 'include', { column: 'logger', exact: true })] }));
    expect(d.viewCount()).toBe(0);
    await d.applyFilter(spec({ rules: [rule('worker-1', 'exclude', { column: 'thread', exact: true })] }));
    expect(entries(d)).toEqual([0, 1, 3, 4, 5]);
  });

  it('shows all entries in context without losing the filter', async () => {
    const d = await doc();
    await d.applyFilter(spec({ rules: [rule('Db', 'include')] }));
    expect(entries(d)).toEqual([1, 3]);
    d.ignoreFilter = true;
    expect(d.viewCount()).toBe(6);
    expect(d.info().viewCount).toBe(6);
    expect(d.getRows(0, 10).map((r) => r.context)).toEqual([true, false, true, false, true, true]);
    expect(d.rowForKey('0:3')).toBe(3);
    expect((await d.search({ text: 'Cache', regex: false, caseSensitive: false, wholeWord: false })).rows).toEqual([2]);
    d.ignoreFilter = false;
    expect(entries(d)).toEqual([1, 3]);
    expect(d.rowForKey('0:2')).toBeNull();
    expect(d.rowForKey('0:2', true)).toBe(1);
  });

  it('counts top field values', async () => {
    const d = await doc();
    const stats = await d.fieldStats();
    const get = (s: FieldStats, k: string) => s.fields.find((f) => f.key.toLowerCase() === k);
    expect([stats.scanned, stats.viewCount, stats.sampled]).toEqual([6, 6, false]);
    expect(get(stats, 'message')).toBeUndefined();
    expect(get(stats, 'time')).toBeUndefined();
    const logger = get(stats, 'logger')!;
    expect(logger.top[0]).toEqual(['com.acme.App', 3]);
    expect(logger.distinct).toBe(3);
    expect(logger.present).toBe(6);
    expect(get(stats, 'thread')!.top[0]).toEqual(['main', 4]);

    await d.applyFilter(spec({ rules: [rule('Db', 'include')] }));
    expect(get(await d.fieldStats(), 'logger')!.top).toEqual([['com.acme.Db', 2]]);
    const sampled = await d.fieldStats(1);
    expect(sampled.sampled || sampled.viewCount <= 1000).toBe(true);
  });

  it('samples large views in blocks', async () => {
    let text = '';
    for (let i = 0; i < 5000; i++) text += `2024-01-01 00:00:00 [t${i % 3}] INFO x - m${i}\n`;
    const d = await doc(text);
    const stats = await d.fieldStats(2000);
    expect(stats.sampled).toBe(true);
    expect(stats.scanned).toBe(2000);
    expect(stats.fields.find((f) => f.key === 'thread')!.distinct).toBe(3);
  });

  it('exposes every JSON property as a field', async () => {
    const d = await doc('{"@t":"2024-01-01T00:00:00Z","@l":"Warning","@m":"slow","Elapsed":812,"http":{"path":"/api","status":503},"tags":["a"]}\n');
    const row = d.getRows(0, 1)[0];
    expect(row.fields.level).toBe('WARN');
    expect(row.fields.message).toBe('slow');
    expect(row.fields.Elapsed).toBe('812');
    expect(row.fields['http.path']).toBe('/api');
    expect(row.fields['http.status']).toBe('503');
    expect(row.fields.tags).toBe('["a"]');
    expect('@m' in row.fields).toBe(false);
    await d.applyFilter(spec({ query: 'http.status:503' }));
    expect(d.viewCount()).toBe(1);
  });
});
