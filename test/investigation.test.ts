// Engine parts of the investigation workflow: wildcard and picked-value queries, source links, grouped timelines and
// pinned rows in workspaces.

import * as esbuild from 'esbuild';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { LogDocument } from '../src/core/document';
import { fieldPicks, packagePrefixes, packageTree, pickClause, setFieldPicks } from '../src/core/fieldPicks';
import { GROUP_NONE, GROUP_OTHER, HistogramGroups, legendClause, SOURCE_FIELD } from '../src/core/histogramGroups';
import { compileQuery, describeQuery, parseQueryExpr } from '../src/core/query';
import { ScanPool } from '../src/core/scanPool';
import { editorTemplate, expandCommand, expandUrl, findSourceRefs, isUnsafeUrlTemplate, isUrlTemplate, resolveSource } from '../src/core/sourceLinks';
import { legendLabels } from '../src/renderer/histogram';
import { highlightTerm, occurrences } from '../src/renderer/autoHighlight';
import { parseWorkspace, resolveWorkspace, serializeWorkspace } from '../src/renderer/workspace';
import { tmpFile } from './helpers';

const E = (q: string, fields: Record<string, string>, cs = false) => {
  const lower = Object.fromEntries(Object.entries(fields).map(([k, v]) => [k.toLowerCase(), v]));
  return compileQuery(q, cs)('', (k) => lower[k.toLowerCase()] ?? '');
};
const logger = (l: string) => ({ logger: l });

describe('field wildcards', () => {
  it('match the whole value, and a trailing .* is a package', () => {
    expect(E('logger:com.acme.*', logger('com.acme.Db'))).toBe(true);
    expect(E('logger:com.acme.*', logger('com.acme.web.Api'))).toBe(true);
    expect(E('logger:com.acme.*', logger('com.acme'))).toBe(true); // the package itself
    expect(E('logger:com.acme.*', logger('COM.ACME.Db'))).toBe(true); // case-insensitive like other field matches
    expect(E('logger:com.acme.*', logger('com.acmeX.Db'))).toBe(false);
    expect(E('logger:com.acme.*', logger('org.com.acme.Db'))).toBe(false); // anchored, not "contains"
    expect(E('logger:com.acme.*', logger('COM.ACME.Db'), true)).toBe(false);
    expect(E('thread:worker-*', { thread: 'worker-12' })).toBe(true);
    expect(E('thread:worker-*', { thread: 'main' })).toBe(false);
    expect(E('logger:*Service', logger('com.shop.PaymentService'))).toBe(true);
    expect(E('-logger:com.acme.*', logger('org.other.X'))).toBe(true);
    // quoted values stay literal; values without "*" keep their "contains" meaning
    expect(E('logger:"com.acme.*"', logger('com.acme.Db'))).toBe(false);
    expect(E('logger:acme', logger('com.acme.Db'))).toBe(true);
    expect(describeQuery(parseQueryExpr('logger:com.acme.*'))).toBe('logger:/^com\\.acme(?:\\..*)?$/');
  });
});

describe('field picks', () => {
  it('become one clause of the query', () => {
    expect(setFieldPicks('error', 'logger', ['a', 'b'], [])).toBe('error logger in (a,b)');
    expect(setFieldPicks('', 'logger', [], ['com.acme.'])).toBe('logger:com.acme.*');
    expect(setFieldPicks('', 'logger', ['a'], ['com.acme.'])).toBe('(logger in (a) or logger:com.acme.*)');
    expect(setFieldPicks('', 'logger', ['two words', 'x'], [])).toBe('logger in ("two words",x)');
    // replaced in place, the rest of the query untouched
    expect(setFieldPicks('error logger in (a,b) -retry', 'logger', ['a', 'b', 'c'], [])).toBe('error logger in (a,b,c) -retry');
    expect(setFieldPicks('error logger in (a,b) -retry', 'logger', [], [])).toBe('error -retry');
    expect(setFieldPicks('x (logger in (a) or logger:com.*)', 'logger', [], []) + ' ' + pickClause('thread', ['main'], [])).toBe('x thread in (main)');
    // another field's clause is left alone
    expect(setFieldPicks('thread in (main)', 'logger', ['a'], [])).toBe('thread in (main) logger in (a)');
  });

  it('are read back from the query', () => {
    const r = fieldPicks('error (logger in (a, "b c") or logger:com.acme.* or LOGGER:org.*) -x', 'logger');
    expect(r.values).toEqual(['a', 'b c']);
    expect(r.prefixes).toEqual(['com.acme.', 'org.']);
    expect(fieldPicks('logger:acme', 'logger').values).toEqual([]); // a plain field match is not a pick clause
    expect(fieldPicks('(logger in (a) or level:error)', 'logger').values).toEqual([]); // the user's own expression
    expect(fieldPicks('thread in (main)', 'thread').values).toEqual(['main']);
    // what the clause selects
    const q = setFieldPicks('', 'logger', ['com.shop.Api'], ['com.acme.']);
    expect(E(q, logger('com.shop.Api'))).toBe(true);
    expect(E(q, logger('com.acme.Db'))).toBe(true);
    expect(E(q, logger('com.shop.Cache'))).toBe(false);
    expect(packagePrefixes('com.acme.web.Api')).toEqual(['com.', 'com.acme.', 'com.acme.web.']);
    expect(packagePrefixes('main')).toEqual([]);
  });

  it('offer package nodes for dotted names', () => {
    const tree = packageTree([['com.shop.Api', 10], ['com.shop.web.Cart', 4], ['com.shop.web.Pay', 3], ['org.x.Y', 1], ['1.2.3', 9]]);
    expect(tree).toEqual([['com.shop.', 0, 17], ['com.shop.web.', 1, 7]]);
    expect(packageTree([['main', 3], ['worker-1', 2]])).toEqual([]);
  });
});

describe('source links', () => {
  it('finds source locations in stack traces', () => {
    const text =
      'java.sql.SQLException: timeout\n\tat com.acme.Db.query(Db.java:42)\n\tat com.acme.App$Inner.main(App.java:10)\n' +
      '   at Shop.Payments.Charge() in C:\\src\\Shop\\Payments.cs:line 88\n' +
      '   at Shop.Run() in /home/me/My Shop/Run.cs:line 7\n' +
      '    at handler (/srv/app/src/server.ts:10:5)\n    at file:///C:/web/app.js:3:14\n' +
      '  File "/opt/app/main.py", line 3, in <module>\n' +
      'see Foo.kt:12 and http://host:8080/index.html and version 1.2:3';
    const refs = findSourceRefs(text);
    expect(refs.map((r) => [r.path, r.line, r.col, r.pkg ?? null])).toEqual([
      ['Db.java', 42, 0, 'com.acme'],
      ['App.java', 10, 0, 'com.acme'],
      ['C:\\src\\Shop\\Payments.cs', 88, 0, null],
      ['/home/me/My Shop/Run.cs', 7, 0, null],
      ['/srv/app/src/server.ts', 10, 5, null],
      ['C:/web/app.js', 3, 14, null],
      ['/opt/app/main.py', 3, 0, null],
      ['Foo.kt', 12, 0, null],
    ]);
    expect(text.substr(refs[0].start, refs[0].length)).toBe('Db.java:42');
    expect(text.substr(refs[6].start, refs[6].length)).toBe('File "/opt/app/main.py", line 3');
    expect(findSourceRefs('Handled request 42 for user:7 at 10:00:01')).toEqual([]);
  });

  it('resolves against the source roots, first match wins', () => {
    const files = new Set(['/code/shop/src/main/java/com/acme/Db.java', '/code/other/app/File.cs', '/code/shop/app/File.cs', '/abs/x.py']);
    const exists = (p: string) => files.has(p);
    const join = path.posix;
    const roots = ['/code/missing', '/code/shop/src/main/java', '/code/shop', '/code/other'];
    const ref = (p: string, pkg?: string) => ({ start: 0, length: 0, path: p, line: 1, col: 0, pkg });
    expect(resolveSource(ref('Db.java', 'com.acme'), roots, exists, join)).toBe('/code/shop/src/main/java/com/acme/Db.java');
    // build-machine path: the longest tail that exists under the first root that has it
    expect(resolveSource(ref('D:\\agent\\_work\\1\\s\\app\\File.cs'), roots, exists, join)).toBe('/code/shop/app/File.cs');
    expect(resolveSource(ref('/code/shop/app/File.cs'), roots, exists, join)).toBe('/code/shop/app/File.cs'); // absolute, under a root
    expect(resolveSource(ref('Nope.java'), roots, exists, join)).toBeNull();
  });

  it('never resolves a location outside the source roots', () => {
    // The path comes from the log: an absolute path elsewhere, "..", a drive or a stream in the middle, or a file
    // that is not source code must not reach the editor (or a command line).
    const files = new Set(['/abs/x.py', '/code/shop/x.py', '/etc/evil.py', '/code/evil.py', '/code/shop/run.sh', '/code/shop/a/b.cs']);
    const exists = (p: string) => files.has(p);
    const ref = (p: string) => ({ start: 0, length: 0, path: p, line: 1, col: 0 });
    const roots = ['/code/shop'];
    expect(resolveSource(ref('/abs/x.py'), [], exists, path.posix)).toBeNull();
    expect(resolveSource(ref('/etc/evil.py'), roots, exists, path.posix)).toBeNull();
    expect(resolveSource(ref('../evil.py'), roots, exists, path.posix)).toBeNull();
    expect(resolveSource(ref('../../etc/evil.py'), roots, exists, path.posix)).toBeNull();
    expect(resolveSource(ref('x/.../../evil.py'), roots, exists, path.posix)).toBeNull();
    expect(resolveSource(ref('run.sh'), roots, exists, path.posix)).toBeNull();
    // what follows the last unsafe segment still resolves inside the root
    expect(resolveSource(ref('../../a/b.cs'), roots, exists, path.posix)).toBe('/code/shop/a/b.cs');
    expect(resolveSource(ref('x/C:/a/b.cs'), roots, exists, path.posix)).toBe('/code/shop/a/b.cs');
    // Windows: a drive in the middle would make path.join drop the root
    const win = new Set(['C:\\code\\shop\\x.py', 'D:\\evil.py']);
    expect(resolveSource(ref('a\\D:\\evil.py'), ['C:\\code\\shop'], (p) => win.has(p), path.win32)).toBeNull();
    expect(resolveSource(ref('D:\\build\\x.py'), ['C:\\code\\shop'], (p) => win.has(p), path.win32)).toBe('C:\\code\\shop\\x.py');
  });

  it('fills editor templates', () => {
    expect(expandUrl(editorTemplate('vscode', ''), '/home/me/My Shop/Run.cs', 7, 0)).toBe('vscode://file/home/me/My%20Shop/Run.cs:7');
    expect(expandUrl(editorTemplate('vscode', ''), 'C:\\src\\A.cs', 3, 0)).toBe('vscode://file/C:/src/A.cs:3');
    expect(expandUrl(editorTemplate('intellij', ''), '/src/A.java', 3, 0)).toBe('idea://open?file=/src/A.java&line=3');
    expect(expandUrl(editorTemplate('rider', ''), 'C:\\src\\A.cs', 3, 0)).toBe('rider://open?file=C:/src/A.cs&line=3');
    expect(expandUrl(editorTemplate(undefined, undefined), '/x.cs', 1, 0)).toBe('vscode://file/x.cs:1');
    const c = expandCommand('"C:\\Program Files\\Ed\\ed.exe" --goto "{path}:{line}:{col}" -n', '/a b/x.cs', 9, 0);
    expect(c.program).toBe('C:\\Program Files\\Ed\\ed.exe');
    expect(c.args).toEqual(['--goto', '/a b/x.cs:9:1', '-n']);
    expect(isUrlTemplate('subl://open?url={path}')).toBe(true);
    expect(isUrlTemplate('code -g {path}:{line}')).toBe(false);
    expect(isUnsafeUrlTemplate(' FILE://{path}')).toBe(true);
    expect(isUnsafeUrlTemplate('vscode://file/{path}:{line}')).toBe(false);
  });
});

function log(entries: number): string {
  let s = '';
  const p2 = (x: number) => String(x).padStart(2, '0');
  for (let i = 0; i < entries; i++) {
    const lg = i % 10 === 9 ? `com.rare.L${i}` : `com.acme.L${i % 3}`;
    s += `2024-05-01 10:${p2(Math.floor(i / 60))}:${p2(i % 60)},000 [t-${i % 2}] INFO  ${lg} - message ${i}\n`;
  }
  return s;
}

let dir = '';
let pool: ScanPool;
beforeAll(async () => {
  dir = fs.mkdtempSync(path.join(os.tmpdir(), 'ulv-inv-'));
  await esbuild.build({ entryPoints: ['src/main/scanWorker.ts'], bundle: true, platform: 'node', format: 'cjs', outfile: path.join(dir, 'scanWorker.js'), logLevel: 'silent' });
  pool = new ScanPool(path.join(dir, 'scanWorker.js'), 3);
});
afterAll(() => {
  pool.close();
  fs.rmSync(dir, { recursive: true, force: true });
});

describe('grouped timeline', () => {
  it('keeps the top values and folds the rest into other', () => {
    const g = new HistogramGroups(0, 10, 3);
    for (const [t, v] of [[0, 'a'], [1, 'a'], [11, 'b'], [25, 'a'], [25, GROUP_NONE], [NaN, 'z']] as [number, string][]) g.add(t, v);
    const h = g.build('logger');
    expect(h.keys).toEqual(['a', '(none)', 'b']); // ties by name
    expect(h.totals).toEqual([3, 1, 1]);
    expect(h.counts[0]).toEqual([2, 0, 0]);
    expect(h.counts[2]).toEqual([1, 1, 0]);

    const many = new HistogramGroups(0, 1000, 1);
    for (let i = 0; i < 12; i++) for (let k = 0; k <= i; k++) many.add(5, `v${String(i).padStart(2, '0')}`);
    const top = many.build('thread');
    expect(top.keys.length).toBe(9);
    expect(top.keys[0]).toBe('v11');
    expect(top.keys[8]).toBe(GROUP_OTHER);
    expect(top.totals[8]).toBe(1 + 2 + 3 + 4); // v00..v03 fold into "other"
    expect(top.counts[0].reduce((a, b) => a + b)).toBe(top.totals.reduce((a, b) => a + b));
    expect(legendClause(top, 0)).toBe('thread==v11');
    expect(legendClause(top, 8).startsWith('-thread in (v11,v10,')).toBe(true);
    expect(legendClause(h, 1)).toBe('-exists(logger)');
    // the clauses are valid queries that select what the legend shows
    expect(E(legendClause(top, 8), { thread: 'v02' })).toBe(true);
    expect(E(legendClause(top, 8), { thread: 'v11' })).toBe(false);
  });

  it('labels the legend with short names unless they would clash', () => {
    expect(legendLabels(['com.acme.Db', 'org.x.Cache', '(other)', 'worker-1'])).toEqual(['Db', 'Cache', '(other)', 'worker-1']);
    expect(legendLabels(['com.a.Db', 'com.b.Db', 'com.a.Api'])).toEqual(['com.a.Db', 'com.b.Db', 'Api']);
  });

  it('follows the filter, matches the level timeline and gives the sequential answer on the scan pool', async () => {
    const file = tmpFile('g.log', log(600));
    const d = new LogDocument(1, 't');
    d.scanBatch = 64;
    await d.addSource(file, { scanPool: pool });
    const plain = d.histogram(40)!;
    const byLogger = (await d.histogramBy('logger', 40))!;
    expect([byLogger.start, byLogger.bucketMs, byLogger.counts.length]).toEqual([plain.start, plain.bucketMs, plain.counts.length]);
    // every bucket holds the same entries, only split differently
    expect(byLogger.counts.map((c) => c.reduce((a, b) => a + b, 0))).toEqual(plain.counts.map((c) => c[0] + c[1] + c[2]));
    expect(byLogger.keys.slice(0, 3)).toEqual(['com.acme.L0', 'com.acme.L1', 'com.acme.L2']);
    expect(byLogger.keys[byLogger.keys.length - 1]).toBe(GROUP_OTHER); // 60 rare loggers
    expect(byLogger.totals.reduce((a, b) => a + b)).toBe(600);
    expect(await d.histogramBy('logger', 40)).toBe(byLogger); // cached for this view
    expect((await d.histogramBy('THREAD', 40))!.keys).toEqual(['t-0', 't-1']);

    await d.applyFilter({ levels: [true, true, true, true, true, true, true], rules: [], query: 'logger:com.acme.*' });
    const filtered = (await d.histogramBy('logger', 40))!;
    expect(filtered).not.toBe(byLogger);
    expect(filtered.totals.reduce((a, b) => a + b)).toBe(540);
    expect(filtered.keys.includes(GROUP_OTHER)).toBe(false);
    // a newer run cancels an older one
    let stop = false;
    const cancelled = d.histogramBy('thread', 41, () => stop);
    stop = true;
    expect(await cancelled).toBeNull();

    const seq = new LogDocument(2, 's');
    await seq.addSource(tmpFile('g2.log', log(600)), {});
    const s = (await seq.histogramBy('logger', 40))!;
    expect(s.keys).toEqual(byLogger.keys);
    expect(s.counts).toEqual(byLogger.counts);
    d.close();
    seq.close();
  });

  it('groups a merged view by source file from the index', async () => {
    const d = new LogDocument(3, 'm');
    await d.addSources([tmpFile('a.log', log(30)), tmpFile('b.log', log(10))], () => ({}));
    const h = (await d.histogramBy(SOURCE_FIELD, 20))!;
    expect(h.keys.length).toBe(2);
    expect(h.keys[0].endsWith('a.log')).toBe(true);
    expect(h.totals).toEqual([30, 10]);
    d.close();
  });
});

describe('auto-highlight', () => {
  it('takes one-line selections and finds every occurrence ignoring case', () => {
    expect(highlightTerm(' Cache miss ')).toBe('Cache miss');
    expect(highlightTerm('x')).toBeNull();
    expect(highlightTerm('two\nlines')).toBeNull();
    expect(highlightTerm('y'.repeat(201))).toBeNull();
    expect(occurrences('Cache miss, cache MISS; cachemiss', 'cache miss')).toEqual([[0, 10], [12, 22]]);
    expect(occurrences('aaaa', 'aa')).toEqual([[0, 2], [2, 4]]);
    expect(occurrences('aaaa', 'aa', 1)).toEqual([[0, 2]]);
  });
});

describe('pinned rows in workspaces', () => {
  const tab = {
    paths: ['/home/u/logs/app.log'],
    formatId: '',
    filter: { levels: [true, true, true, true, true, true, true], rules: [], query: '' },
    search: { text: '', regex: false, caseSensitive: false, wholeWord: false },
    bookmarks: [],
    pins: [{ path: '/home/u/logs/app.log', line: 13 }],
  };
  it('are saved, read back and follow a moved file', () => {
    const ws = serializeWorkspace([tab], 0, [], '/home/u/ws/my.ulvw');
    expect(ws.tabs[0].pins).toEqual([{ path: '/home/u/logs/app.log', line: 13 }]);
    const back = parseWorkspace(JSON.stringify(ws));
    expect(back.tabs[0].pins).toEqual([{ path: '/home/u/logs/app.log', line: 13 }]);
    const r = resolveWorkspace(back, '/mnt/u/ws/my.ulvw', (p) => p === '/mnt/u/logs/app.log');
    expect(r.tabs[0].pins).toEqual([{ path: '/mnt/u/logs/app.log', line: 13 }]);
    // older workspaces have none
    const old = JSON.parse(JSON.stringify(ws));
    delete old.tabs[0].pins;
    expect(parseWorkspace(JSON.stringify(old)).tabs[0].pins).toEqual([]);
  });
});
