import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import * as esbuild from 'esbuild';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { LogDocument } from '../src/core/document';
import { LogSql } from '../src/core/logSql';
import { ScanPool } from '../src/core/scanPool';
import { buildSqlColumns, guardSql, mentionedColumns, sanitizeColumn, sqlChartable, sqlExamples, sqlFieldValue } from '../src/core/sqlSchema';
import { FilterSpec, Level } from '../src/core/types';
import { LOG4J, tmpFile } from './helpers';
import { sqlStatusText } from '../src/renderer/sqlPanel';

/** JSON lines with nested fields, mixed with plain lines that are not JSON (same file as SqlTests.cs). */
const MESSY_JSON =
  '{"@timestamp":"2024-05-01T10:00:00.000Z","level":"INFO","logger":"api","message":"GET /a completed","http":{"status":200,"path":"/a"},"duration_ms":12,"traceId":"007"}\n' +
  'plain startup line without json\n' +
  '{"@timestamp":"2024-05-01T10:00:01.500Z","level":"ERROR","logger":"db","message":"query failed","http":{"status":500,"path":"/b"},"duration_ms":950.5,"order":"x"}\n' +
  '{"@timestamp":"2024-05-01T10:00:03.000Z","level":"WARN","logger":"api","message":"slow","http":{"status":200,"path":"/c"},"duration_ms":400}\n' +
  '{"@timestamp":"2024-05-01T10:00:07.000Z","level":"ERROR","logger":"api","message":"boom","http":{"status":503,"path":"/d"}}\n';

async function doc(content: string, name = 'x.log', scanPool?: ScanPool) {
  const d = new LogDocument(1, 'test');
  await d.addSource(tmpFile(name, content), { scanPool });
  return d;
}

const lines = (n: number) => Array.from({ length: n }, (_, i) => `2024-05-01 10:00:00,000 [main] INFO  com.acme.App - entry ${i}\n`).join('');

let dir = '';
let pool: ScanPool;
beforeAll(async () => {
  dir = fs.mkdtempSync(path.join(os.tmpdir(), 'ulv-sql-'));
  await esbuild.build({ entryPoints: ['src/main/scanWorker.ts'], bundle: true, platform: 'node', format: 'cjs', outfile: path.join(dir, 'scanWorker.js'), logLevel: 'silent' });
  pool = new ScanPool(path.join(dir, 'scanWorker.js'), 3);
});
afterAll(() => {
  pool.close();
  fs.rmSync(dir, { recursive: true, force: true });
});

describe('SQL schema', () => {
  it('sanitises column names', () => {
    expect(['http.status', '@t', '1st', 'order', 'a -- b', '¤', 'traceId'].map(sanitizeColumn)).toEqual(['http_status', 't', 'f_1st', 'order_', 'a_b', 'field', 'traceId']);
  });

  it('builds the schema', () => {
    const cols = buildSqlColumns(['time', 'level', 'logger', 'message', 'http.status', 'http_status', 'source', 'Level', '__source', 'x']);
    expect(cols.slice(0, 10).map((c) => c.name)).toEqual(['row', 'line', 'source', 'time', 'time_iso', 'level', 'logger', 'thread', 'message', 'text']);
    expect(cols.slice(0, 10).every((c) => c.field === null)).toBe(true);
    expect(cols.slice(10).map((c) => [c.name, c.field])).toEqual([['http_status', 'http.status'], ['http_status_2', 'http_status'], ['source_2', 'source'], ['x', 'x']]);
  });

  it.each([
    ['SELECT 1', null],
    ['  with x as (select 1) select * from x;  ', null],
    ["select replace(message, 'a', 'b') from logs -- delete", null],
    ['select \'attach\' as "drop" from logs', null],
    ["ATTACH DATABASE '/tmp/x.db' AS x", 'ATTACH is not allowed: queries cannot open other files.'],
    ["select * from logs; attach 'x' as y", 'Run one statement at a time.'],
    ["select load_extension('/tmp/evil')", 'load_extension is not allowed.'],
    ['with x as (select 1) delete from logs_data', 'DELETE is not allowed: the table is read-only.'],
    ['PRAGMA temp_store = FILE', 'PRAGMA is not allowed: the table is read-only.'],
    ["VACUUM INTO '/tmp/copy.db'", 'VACUUM is not allowed: the table is read-only.'],
    ['explain select 1', 'Only SELECT queries can run here: the table is read-only.'],
    [' -- nothing', 'Enter a query.'],
  ])('guards %s', (sql, error) => expect(guardSql(sql)).toBe(error));

  it('finds the columns a query names', () => {
    let m = mentionedColumns(`select count(*), "Logger" from logs where message like '%text%'`);
    expect(m.star).toBe(false);
    expect(m.names.has('logger') && m.names.has('message') && !m.names.has('text')).toBe(true);
    expect(mentionedColumns('select l.* from logs l').star).toBe(true);
    m = mentionedColumns('select line * 2 from logs');
    expect(m.star).toBe(false);
  });

  it('stores canonical numbers as numbers', () => {
    expect([sqlFieldValue('200'), sqlFieldValue('950.5'), sqlFieldValue('007'), sqlFieldValue('12ms'), sqlFieldValue('')]).toEqual([200n, 950.5, '007', '12ms', null]);
  });
});

describe('LogSql', () => {
  it('loads a messy JSON file', async () => {
    const d = await doc(MESSY_JSON);
    const sql = new LogSql(d);
    const cols = await sql.schemaColumns();
    expect(cols.filter((c) => c.field !== null).map((c) => c.name)).toEqual(['http_status', 'http_path', 'duration_ms', 'traceId', 'order_']);
    const r = await sql.query('SELECT row, line, source, time, time_iso, level, logger, message, http_status, duration_ms, traceId FROM logs ORDER BY line');
    expect(r.error).toBeNull();
    expect(r.rows.length).toBe(5);
    expect(r.rows[0].map((v) => (typeof v === 'string' && v.endsWith('x.log') ? 'x.log' : v))).toEqual([0, 1, 'x.log', 1714557600000, '2024-05-01T10:00:00.000Z', 'INFO', 'api', 'GET /a completed', 200, 12, '007']);
    // The plain line: its message is the line, no level or fields (the index gives it the time of the entry before).
    expect([0, 1, 3, 5, 6, 7].map((i) => r.rows[1][i])).toEqual([1, 2, 1714557600000, null, null, 'plain startup line without json']);
    expect(r.rows[1][8]).toBeNull();
    expect(r.rows[2][9]).toBe(950.5);
    expect(sql.loadedColumns.sort()).toEqual(['duration_ms', 'http_status', 'logger', 'message', 'traceid']);
    expect([r.loaded, r.capped]).toEqual([5, false]);
    expect((await sql.query('SELECT count(*) FROM logs WHERE http_status >= 500')).rows[0][0]).toBe(2);
    sql.close();
  });

  it('loads multi-line entries and only the named columns, on the scan pool too', async () => {
    for (const scanPool of [undefined, pool]) {
      const d = await doc(LOG4J, 'x.log', scanPool);
      const sql = new LogSql(d);
      sql.batch = 2; // several batches, so the pool runs some in parallel
      let r = await sql.query('SELECT line, level FROM logs');
      expect(r.rows.map((x) => x[0])).toEqual([1, 2, 3, 4, 8, 9]);
      expect(sql.loadedColumns).toEqual([]);
      r = await sql.query("SELECT logger, thread, message, text FROM logs WHERE level = 'ERROR'");
      expect(sql.loadedColumns.sort()).toEqual(['logger', 'message', 'text', 'thread']);
      expect(r.rows[0].slice(0, 2)).toEqual(['com.acme.Db', 'worker-2']);
      expect(r.rows[0][2]).toMatch(/^Query failed\njava.sql.SQLException: timeout\n\tat com.acme.Db.query/);
      expect(r.rows[0][3]).toMatch(/^2024-05-01 10:00:03,004 \[worker-2\] ERROR[\s\S]*\(App\.java:10\)$/);
      const all = await sql.query('SELECT * FROM logs');
      expect(all.columns).toEqual(['row', 'line', 'source', 'time', 'time_iso', 'level', 'logger', 'thread', 'message', 'text']);
      sql.close();
      d.close();
    }
  });

  it('runs window functions and the regex helpers', async () => {
    const d = await doc(LOG4J);
    const sql = new LogSql(d);
    let r = await sql.query('SELECT line, time - LAG(time) OVER (ORDER BY time, line) AS gap, count(*) OVER (PARTITION BY level) AS same FROM logs ORDER BY line');
    expect(r.error).toBeNull();
    expect(r.rows.map((x) => x[1])).toEqual([null, 1001, 1001, 1001, 1001, 1001]);
    expect(r.rows.map((x) => x[2])).toEqual([2, 1, 1, 1, 2, 1]);
    r = await sql.query("SELECT regexp_extract(message, 'Db\\.java:(\\d+)') AS at FROM logs WHERE message REGEXP 'time(out|d)'");
    expect(r.rows).toEqual([['42']]);
    for (const ex of sqlExamples(await sql.schemaColumns())) expect((await sql.query(ex.sql)).error, ex.title).toBeNull();
    sql.close();
  });

  it('row and view follow the filter', async () => {
    const d = await doc(LOG4J);
    const sql = new LogSql(d);
    const levels = [true, true, true, true, true, true, true];
    levels[Level.Info] = false;
    await d.applyFilter({ levels, rules: [] } as FilterSpec);
    expect((await sql.query('SELECT line, row FROM logs ORDER BY line')).rows.map((x) => x[1])).toEqual([null, 0, 1, 2, null, 3]);
    expect((await sql.query('SELECT count(*) FROM view')).rows[0][0]).toBe(4);
    await d.applyFilter(null);
    expect((await sql.query('SELECT count(*) FROM view')).rows[0][0]).toBe(6);
    sql.close();
  });

  it('shows SQL errors', async () => {
    const sql = new LogSql(await doc(LOG4J));
    expect((await sql.query('SELECT nope FROM logs')).error).toBe('no such column: nope');
    expect((await sql.query('DELETE FROM logs_data')).error).toBe('DELETE is not allowed: the table is read-only.');
    sql.close();
  });

  it('blocks ATTACH, file writes and extensions on its own', async () => {
    const sql = new LogSql(await doc(LOG4J));
    sql.skipGuard = true;
    const file = path.join(os.tmpdir(), `ulv-attach-${process.pid}-${Date.now()}.db`);
    for (const q of [`ATTACH DATABASE '${file}' AS x`, "SELECT load_extension('/tmp/nothing')", 'DELETE FROM logs_data', 'PRAGMA temp_store = FILE', `VACUUM INTO '${file}'`, 'CREATE TABLE t(a)']) {
      const r = await sql.query(q);
      expect(r.error, q).toContain('not authorized');
    }
    expect(fs.existsSync(file)).toBe(false);
    expect((await sql.query('SELECT count(*) FROM logs')).rows[0][0]).toBe(6);
    sql.close();
  });

  it('caps big files and loads everything on request', async () => {
    const d = await doc(lines(3000));
    const sql = new LogSql(d);
    sql.cap = 1000;
    sql.slice = 300;
    sql.batch = 64;
    let r = await sql.query("SELECT count(*), max(line) FROM logs WHERE message LIKE 'entry%'");
    expect(r.rows[0]).toEqual([1000, 1000]);
    expect([r.capped, r.loaded, r.total]).toEqual([true, 1000, 3000]);
    sql.cap = 0;
    r = await sql.query('SELECT count(*) FROM logs');
    expect(r.rows[0][0]).toBe(3000);
    expect(r.capped).toBe(false);
    sql.close();
  });

  it('cancelling a load keeps the previous table', async () => {
    const d = await doc(lines(5000));
    const sql = new LogSql(d);
    sql.slice = 500;
    sql.batch = 50;
    expect((await sql.query('SELECT count(*) FROM logs')).rows[0][0]).toBe(5000);
    const r = await sql.query('SELECT count(message) FROM logs', (f) => f > 0.2 && sql.cancel());
    expect(r.cancelled).toBe(true);
    expect(sql.loadedColumns).toEqual([]);
    expect((await sql.query('SELECT count(*) FROM logs')).rows[0][0]).toBe(5000);
    expect((await sql.query('SELECT count(message) FROM logs')).rows[0][0]).toBe(5000);
    sql.close();
  });

  it('loads appended entries on the next query after a cancelled append', async () => {
    const p = tmpFile('grow-cancel.log', lines(2000));
    const d = new LogDocument(1, 'grow-cancel');
    await d.addSource(p, {});
    const sql = new LogSql(d);
    sql.slice = 500;
    sql.batch = 50;
    expect((await sql.query('SELECT count(message) FROM logs')).rows[0][0]).toBe(2000);
    fs.appendFileSync(p, lines(3000));
    expect(await d.refresh()).toBe(true);
    const r = await sql.query('SELECT count(message) FROM logs', (f) => f > 0.2 && sql.cancel());
    expect(r.cancelled).toBe(true);
    expect((await sql.query('SELECT count(message) FROM logs')).rows[0][0]).toBe(5000);
    sql.close();
  });

  it('follows a growing file and a new format', async () => {
    const p = tmpFile('grow.log', LOG4J);
    const d = new LogDocument(1, 'grow');
    await d.addSource(p, {});
    const sql = new LogSql(d);
    expect((await sql.query('SELECT count(*) FROM logs')).rows[0][0]).toBe(6);
    fs.appendFileSync(p, '\tat com.acme.More(More.java:1)\n2024-05-01 10:00:06,007 [main] INFO  com.acme.App - Again\n');
    expect(await d.refresh()).toBe(true);
    let r = await sql.query('SELECT line, message FROM logs ORDER BY line DESC LIMIT 2');
    expect(r.rows[0]).toEqual([11, 'Again']);
    expect(r.rows[1][0]).toBe(9);
    expect(r.rows[1][1]).toContain('More.java');
    expect((await sql.query('SELECT count(*) FROM logs')).rows[0][0]).toBe(7);
    await d.reformat('plain');
    r = await sql.query('SELECT count(*), count(logger) FROM logs');
    expect(r.rows[0]).toEqual([11, 0]);
    sql.close();
  });

  it('keeps the source and base order of merged files', async () => {
    const a = tmpFile('a.log', '2024-05-01 10:00:00,000 INFO a1\n2024-05-01 10:00:02,000 INFO a2\n');
    const b = tmpFile('b.log', '2024-05-01 10:00:01,000 WARN b1\n');
    const d = new LogDocument(1, 'm');
    await d.addSources([a, b], () => ({}));
    const sql = new LogSql(d);
    const r = await sql.query('SELECT row, line, source, level FROM logs');
    expect(r.rows.map((x) => x[0])).toEqual([0, 1, 2]);
    expect(r.rows.map((x) => x[2])).toEqual([path.basename(a), path.basename(b), path.basename(a)]);
    expect(r.rows.map((x) => x[1])).toEqual([1, 1, 2]);
    expect(sqlChartable(await sql.query('SELECT source, count(*) FROM logs GROUP BY source'))).toBe(true);
    expect(sqlChartable(await sql.query('SELECT source, level FROM logs'))).toBe(false);
    sql.close();
  });
});

describe('SQL panel status', () => {
  it('says rows, time, load time and the cap, like the Avalonia panel', () => {
    const base = { columns: ['a'], rows: [[1]], truncated: false, error: null, queryMs: 4.2, loadMs: 0, loaded: 400, total: 400, capped: false };
    expect(sqlStatusText(base)).toBe('1 row in 4 ms  ·  400 entries');
    expect(sqlStatusText({ ...base, rows: [[1], [2]], truncated: true, loadMs: 1234, loaded: 2_000_000, total: 8_695_680, capped: true })).toBe(
      '2+ rows in 4 ms  ·  table loaded in 1.2 s  ·  first 2,000,000 of 8,695,680 entries',
    );
    expect(sqlStatusText({ ...base, rows: [], error: 'no such column: x' })).toBe('400 entries');
  });
});
