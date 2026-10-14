import { describe, expect, it } from 'vitest';
import * as fs from 'node:fs';
import { LogDocument } from '../src/core/document';
import { DEFAULT_FRAMEWORK_PACKAGES, fold } from '../src/core/folding';
import { fnv1a64, messageOf, repeatKey, template } from '../src/core/patterns';
import { FilterSpec, Level } from '../src/core/types';
import { rankCommands, PaletteCommand } from '../src/renderer/paletteRank';
import { parseWorkspace, relativePath, resolvePath, resolveWorkspace, serializeWorkspace } from '../src/renderer/workspace';
import { tmpFile } from './helpers';

const allLevels = () => [true, true, true, true, true, true, true];
const spec = (over: Partial<FilterSpec> = {}): FilterSpec => ({ levels: allLevels(), rules: [], ...over });
const rule = (text: string, mode: 'include' | 'exclude', extra = {}) => ({ id: text, text, mode, enabled: true, regex: false, caseSensitive: false, wholeWord: false, ...extra });

async function doc(content: string) {
  const d = new LogDocument(1, 'test');
  await d.addSource(tmpFile('x.log', content), {});
  return d;
}
const lines = (...l: string[]) => l.map((x) => x + '\n').join('');
const at = (s: number, rest: string) => `2024-01-01 00:00:${String(s).padStart(2, '0')} ${rest}`;

describe('templates', () => {
  it('replaces variable parts with placeholders', () => {
    expect(template('Payment 42 failed after 30000 ms (req 133)')).toBe('Payment <num> failed after <num> ms (req <num>)');
    expect(template('user=bob id=550e8400-e29b-41d4-a716-446655440000 from 10.0.0.12:8080')).toBe('user=bob id=<guid> from <ip>');
    expect(template('GET https://x.io/a?b=1 took 35ms')).toBe('GET <url> took <num>');
    expect(template('token "abc def" for deadbeef42')).toBe('token <str> for <hex>');
    expect(template('at 2024-01-02T03:04:05Z ok')).toBe('at <time> ok');
  });
  it('uses the first line, collapses whitespace and keeps exceptions and paths', () => {
    expect(template('  java.lang.IllegalStateException:   bad  /var/x/y\n\tat a.b(C.java:1)')).toBe('java.lang.IllegalStateException: bad /var/x/y');
    expect(template('x'.repeat(600)).length).toBe(500);
  });
  it('takes the message field, or the text without its timestamp', () => {
    expect(messageOf('2024-05-01 10:00:00 [main] hello', {})).toBe('[main] hello');
    expect(messageOf('2024-05-01 10:00:00 - | hello', {})).toBe('hello');
    expect(messageOf('[2024-05-01 10:00:00] hello', {})).toBe('hello');
    expect(messageOf('no time here', {})).toBe('no time here');
    expect(messageOf('2024-05-01 10:00:00 x', { message: 'from field' })).toBe('from field');
    expect(messageOf('2024-05-01 10:00:00 x', { message: '' })).toBe('x');
  });
});

describe('repeatKey', () => {
  it('builds exact and pattern keys', () => {
    const f = { logger: 'com.acme.Db', message: 'Query 1 failed\nstack' };
    expect(repeatKey(Level.Error, f, '', 'exact')).toBe('5|com.acme.Db|Query 1 failed\nstack');
    expect(repeatKey(Level.Error, f, '', 'pattern')).toBe('5|Query <num> failed');
    expect(repeatKey(Level.Info, {}, '2024-01-01 00:00:00 hi 7', 'exact')).toBe('3||hi 7');
  });
  it('hashes with 64-bit FNV-1a', () => {
    const hex = (s: string) => fnv1a64(s).map((x) => x.toString(16).padStart(8, '0')).join('');
    expect(hex('')).toBe('cbf29ce484222325');
    expect(hex('a')).toBe('af63dc4c8601ec8c');
    expect(hex('foobar')).toBe('85944171f73967e8');
  });
});

describe('stack folding', () => {
  const P = DEFAULT_FRAMEWORK_PACKAGES;
  it('folds runs of framework frames', () => {
    const r = fold(
      ['\tat com.acme.A.a(A.java:1)', '\tat com.acme.B.b(B.java:2)', '\tat java.lang.reflect.Method.invoke(Method.java:3)', '\tat java.base/java.lang.Thread.run(Thread.java:4)', '\tat javax.servlet.X.y(X.java:5)', '\tat com.acme.C.c(C.java:6)'],
      P,
    );
    expect(r.map((x) => x.folded)).toEqual([0, 0, 3, 0]);
    expect(r[2].line).toBe('… 3 framework frames');
    expect(r[3].line).toBe('\tat com.acme.C.c(C.java:6)');
  });
  it('keeps a single framework frame', () => {
    const r = fold(['at com.acme.A.a(A.java:1)', 'at java.lang.Thread.run(Thread.java:4)', 'at com.acme.C.c(C.java:6)'], P);
    expect(r.map((x) => x.folded)).toEqual([0, 0, 0]);
  });
  it('folds Python frames with their source lines', () => {
    const r = fold(
      [
        'Traceback (most recent call last):',
        '  File "/app/main.py", line 3, in <module>',
        '    run()',
        '  File "/usr/lib/python3/site-packages/flask/app.py", line 10, in run',
        '    self.go()',
        '  File "/usr/lib/python3/site-packages/flask/app.py", line 20, in go',
        '    raise X',
        'ValueError: boom',
      ],
      P,
    );
    expect(r.map((x) => x.line)).toEqual(['Traceback (most recent call last):', '  File "/app/main.py", line 3, in <module>', '    run()', '… 4 framework lines', 'ValueError: boom']);
    expect(r[3].folded).toBe(4);
  });
  it('matches packages after "(" and anywhere for path-like packages', () => {
    const r = fold(['    at Object.<anonymous> (node:internal/main:1:2)', '    at Module._compile (node:internal/modules/cjs:3:4)', '    at foo (/app/x.js:1:1)'], P);
    expect(r.map((x) => x.folded)).toEqual([2, 0]);
  });
});

describe('pattern rules', () => {
  it('include and exclude by template', async () => {
    const d = await doc(lines(at(1, 'INFO Payment 42 failed after 30 ms'), at(2, 'INFO Payment 7 failed after 12 ms'), at(3, 'INFO other 1'), at(4, 'ERROR Payment 9 failed after 1 ms')));
    const tpl = template(messageOf(d.getRows(0, 1)[0].text, d.getRows(0, 1)[0].fields));
    expect(tpl).toBe('Payment <num> failed after <num> ms');
    await d.applyFilter(spec({ rules: [rule(tpl, 'include', { patternMatch: true, column: 'logger' })] }));
    expect(d.getRows(0, 10).map((r) => r.entry)).toEqual([0, 1, 3]);
    await d.applyFilter(spec({ rules: [rule(tpl, 'exclude', { patternMatch: true })] }));
    expect(d.getRows(0, 10).map((r) => r.entry)).toEqual([2]);
  });
});

describe('group repeats', () => {
  const text = lines(
    at(1, 'INFO a.B - tick 1'),
    at(2, 'INFO a.B - tick 1'),
    at(3, 'INFO a.B - tick 2'),
    at(4, 'WARN a.B - tick 2'),
    at(5, 'INFO a.B - boom'),
    at(6, 'INFO a.B - tick 3'),
    at(7, 'INFO a.B - tick 3'),
  );
  it('collapses identical consecutive entries', async () => {
    const d = await doc(text);
    await d.applyFilter(spec({ groupRepeats: 'exact' }));
    expect(d.getRows(0, 10).map((r) => [r.entry, r.repeat])).toEqual([[0, 2], [2, 1], [3, 1], [4, 1], [5, 2]]);
    expect(d.info().viewCount).toBe(5);
  });
  it('collapses entries with the same pattern', async () => {
    const d = await doc(text);
    await d.applyFilter(spec({ groupRepeats: 'pattern' }));
    expect(d.getRows(0, 10).map((r) => [r.entry, r.repeat])).toEqual([[0, 3], [3, 1], [4, 1], [5, 2]]);
    await d.applyFilter(spec({ groupRepeats: 'pattern', rules: [rule('boom', 'exclude')] }));
    expect(d.getRows(0, 10).map((r) => [r.entry, r.repeat])).toEqual([[0, 3], [3, 1], [5, 2]]);
    await d.applyFilter(spec({ groupRepeats: 'off' }));
    expect(d.viewCount()).toBe(7);
    expect(d.getRows(0, 1)[0].repeat).toBe(1);
  });
  it('never merges context rows', async () => {
    const d = await doc(lines(at(1, 'INFO x - hit'), at(2, 'INFO x - ctx'), at(3, 'INFO x - ctx'), at(4, 'INFO x - hit'), at(5, 'INFO x - hit')));
    await d.applyFilter(spec({ groupRepeats: 'exact', rules: [rule('hit', 'include')], context: 1 }));
    expect(d.getRows(0, 10).map((r) => [r.entry, r.repeat, !!r.context])).toEqual([
      [0, 1, false],
      [1, 1, true],
      [2, 1, true],
      [3, 2, false],
    ]);
  });
  it('ignores grouping in show-in-context mode', async () => {
    const d = await doc(text);
    await d.applyFilter(spec({ groupRepeats: 'exact' }));
    d.ignoreFilter = true;
    expect(d.viewCount()).toBe(7);
    expect(d.getRows(0, 10).every((r) => r.repeat === 1)).toBe(true);
  });
  it('extends and splits runs as the tail grows', async () => {
    const p = tmpFile('t.log', lines(at(1, 'INFO x - same'), at(2, 'INFO x - same')));
    const d = new LogDocument(1, 't');
    await d.addSource(p, {});
    await d.applyFilter(spec({ groupRepeats: 'exact' }));
    expect(d.getRows(0, 10).map((r) => r.repeat)).toEqual([2]);
    fs.appendFileSync(p, lines(at(3, 'INFO x - same')));
    await d.refresh();
    expect(d.getRows(0, 10).map((r) => r.repeat)).toEqual([3]);
    fs.appendFileSync(p, lines(at(4, 'INFO x - other'), at(5, 'INFO x - same')));
    await d.refresh();
    expect(d.getRows(0, 10).map((r) => [r.entry, r.repeat])).toEqual([[0, 3], [3, 1], [4, 1]]);
  });
});

describe('clearedBefore', () => {
  it('hides entries below the per-source counts', async () => {
    const p = tmpFile('c.log', lines(at(1, 'INFO a'), at(2, 'INFO b'), at(3, 'ERROR c')));
    const d = new LogDocument(1, 'c');
    await d.addSource(p, {});
    await d.applyFilter(spec({ clearedBefore: [2] }));
    expect(d.getRows(0, 10).map((r) => r.entry)).toEqual([2]);
    fs.appendFileSync(p, lines(at(4, 'INFO d')));
    await d.refresh();
    expect(d.getRows(0, 10).map((r) => r.entry)).toEqual([2, 3]);
    await d.applyFilter(spec({ clearedBefore: [4] }));
    expect(d.viewCount()).toBe(0);
    await d.applyFilter(spec({ clearedBefore: null }));
    expect(d.viewCount()).toBe(4);
  });
});

describe('patterns scan', () => {
  it('counts templates with levels, samples and sparklines', async () => {
    let text = '';
    for (let i = 0; i < 48; i++) text += at(i % 60, i % 4 === 0 ? `ERROR x - Payment ${i} failed\n` : `INFO x - tick ${i}\n`);
    const d = await doc(text);
    const r = await d.patterns();
    expect([r.scanned, r.viewCount, r.sampled, r.total]).toEqual([48, 48, false, 2]);
    expect(r.patterns.map((p) => [p.template, p.count])).toEqual([['tick <num>', 36], ['Payment <num> failed', 12]]);
    const pay = r.patterns[1];
    expect(pay.levels[Level.Error]).toBe(12);
    expect(pay.levels.length).toBe(7);
    expect(pay.sample).toBe('Payment 0 failed');
    expect(pay.spark.length).toBe(24);
    expect(pay.spark.reduce((a, b) => a + b, 0)).toBe(12);
    expect(pay.spark[0]).toBe(1); // rows 0-1 → bucket 0; row 0 is a payment
    expect(r.patterns[0].spark.every((n) => n >= 1)).toBe(true);
  });
  it('sorts ties by template, keeps the top and respects filters', async () => {
    const d = await doc(lines(at(1, 'INFO b'), at(2, 'INFO a'), at(3, 'INFO c'), at(4, 'INFO c')));
    const r = await d.patterns(200_000, 2);
    expect(r.patterns.map((p) => p.template)).toEqual(['c', 'a']);
    expect(r.total).toBe(3);
    await d.applyFilter(spec({ rules: [rule('c', 'exclude')] }));
    expect((await d.patterns()).patterns.map((p) => p.template)).toEqual(['a', 'b']);
  });
  it('samples large views and folds overflow into (other)', async () => {
    let text = '';
    for (let i = 0; i < 25_000; i++) text += `2024-01-01 00:00:00 INFO w${i.toString(36)}x\n`;
    const d = await doc(text);
    const sampled = await d.patterns(5000);
    expect(sampled.sampled).toBe(true);
    expect(sampled.scanned).toBe(5000);
    const all = await d.patterns(1_000_000, 5);
    expect(all.total).toBe(20_001);
    expect(all.patterns[0]).toMatchObject({ template: '(other)', count: 5000 });
    expect(all.patterns.length).toBe(5);
  });
  it('can be cancelled', async () => {
    const d = await doc(lines(at(1, 'INFO a')));
    expect((await d.patterns(undefined, undefined, () => true)).cancelled).toBe(true);
  });
});

describe('workspace', () => {
  const tab = {
    paths: ['/home/u/logs/app.log', '/var/log/sys.log'],
    formatId: 'timestamped',
    encoding: 'utf-8',
    filter: { levels: allLevels(), rules: [rule('x', 'include')], query: 'q', bookmarks: ['0:1'], clearedBefore: [5], groupRepeats: 'exact' as const },
    search: { text: 'needle', regex: true, caseSensitive: false, wholeWord: true },
    bookmarks: [{ path: '/home/u/logs/app.log', line: 12, note: 'look' }],
    extraColumns: ['user'],
    columnOrder: ['time', 'message'],
    hiddenColumns: ['thread'],
  };
  it('relative paths', () => {
    expect(relativePath('/home/u/ws', '/home/u/logs/app.log')).toBe('../logs/app.log');
    expect(relativePath('C:\\work', 'c:\\work\\a\\b.log')).toBe('a/b.log');
    expect(relativePath('C:\\work', 'D:\\b.log')).toBe('D:\\b.log');
    expect(resolvePath('/x/ws', '../logs/app.log')).toBe('/x/logs/app.log');
    expect(resolvePath('D:\\x\\ws', 'a/b.log')).toBe('D:\\x\\ws\\a\\b.log');
  });
  it('serializes tabs without bookmark keys or clear', () => {
    const ws = serializeWorkspace([tab], 0, [{ id: 'h', text: 'err' }], '/home/u/ws/my.ulvw');
    expect(ws.version).toBe(1);
    expect(ws.tabs[0].relPaths).toEqual(['../logs/app.log', '../../../var/log/sys.log']);
    expect(ws.tabs[0].filter.bookmarks).toBeUndefined();
    expect(ws.tabs[0].filter.clearedBefore).toBeUndefined();
    expect(ws.tabs[0].filter.groupRepeats).toBe('exact');
    expect(ws.tabs[0]).toMatchObject({ format: 'timestamped', encoding: 'utf-8', search: tab.search, bookmarks: tab.bookmarks, extraColumns: ['user'], hiddenColumns: ['thread'] });
    expect(tab.filter.bookmarks).toEqual(['0:1']); // input untouched
    const back = parseWorkspace(JSON.stringify(ws));
    expect(back.tabs[0].filter.rules[0].text).toBe('x');
    expect(back.highlights).toEqual([{ id: 'h', text: 'err' }]);
    expect(() => parseWorkspace('{"version":2,"tabs":[]}')).toThrow();
  });
  it('resolves moved files through relative paths and skips missing tabs', () => {
    const ws = serializeWorkspace([{ ...tab, paths: ['/gone/a.log'] }, tab, { ...tab, paths: ['/home/u/logs/app.log'] }], 1, [], '/home/u/ws/my.ulvw');
    // the whole folder moved from /home/u to /mnt/u
    const moved = { ...ws, tabs: ws.tabs.map((t) => ({ ...t })) };
    const exists = (p: string) => ['/mnt/u/logs/app.log', '/var/log/sys.log'].includes(p);
    const r = resolveWorkspace(moved, '/mnt/u/ws/my.ulvw', exists);
    expect(r.tabs.length).toBe(2);
    expect(r.tabs[0].paths).toEqual(['/mnt/u/logs/app.log', '/var/log/sys.log']);
    expect(r.tabs[0].bookmarks[0].path).toBe('/mnt/u/logs/app.log');
    expect(r.missing).toBe(1);
    expect(r.active).toBe(0);
  });
});

describe('command palette ranking', () => {
  const cmds: PaletteCommand[] = [
    { id: '1', label: 'File › Open…', enabled: true },
    { id: '2', label: 'View › Time column › Delta from previous row', enabled: true },
    { id: '3', label: 'View › Details pane', enabled: true },
    { id: '4', label: 'Edit › Find…', enabled: false },
    { id: '5', label: 'View › Time column › Time of day', enabled: true },
    { id: '6', label: 'View › Hidden details', enabled: true },
  ];
  const ids = (q: string) => rankCommands(cmds, q).map((c) => c.id);
  it('needs every word and hides disabled items', () => {
    expect(ids('')).toEqual(['1', '2', '3', '5', '6']);
    expect(ids('find')).toEqual([]);
    expect(ids('time delta')).toEqual(['2']);
    expect(ids('TIME')).toEqual(['2', '5']);
  });
  it('ranks prefix, then word starts, then substrings', () => {
    expect(ids('view')).toEqual(['2', '3', '5', '6']);
    expect(ids('tail')).toEqual(['3', '6']); // "de-tail-s" substring only: menu order
    expect(ids('det')).toEqual(['3', '6']);
    expect(ids('ails')).toEqual(['3', '6']);
    expect(ids('pane view')).toEqual(['3']);
    const r = rankCommands(
      [
        { id: 'a', label: 'View › Wrap long messages', enabled: true },
        { id: 'b', label: 'View › Patterns', enabled: true },
        { id: 'c', label: 'Patterns everywhere', enabled: true },
      ],
      'pat',
    );
    expect(r.map((c) => c.id)).toEqual(['c', 'b']);
    expect(rankCommands([{ id: 'x', label: 'Edit › Copy', enabled: true }, { id: 'y', label: 'Help › Keyboard shortcuts', enabled: true }], 'o').map((c) => c.id)).toEqual(['x', 'y']);
    expect(rankCommands([{ id: 'x', label: 'Edit › xopen', enabled: true }, { id: 'y', label: 'File › Open', enabled: true }], 'open').map((c) => c.id)).toEqual(['y', 'x']);
  });
});
