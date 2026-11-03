// SQL over a document's entries: an in-memory SQLite database (node:sqlite, engine thread only) with a `logs` table
// (every entry) and a `view` table (the entries the current filter shows).
//
// The table is filled lazily, on the first query, and only with the columns queries name: row, line, source, time and
// level come from the index without reading the file, while logger, thread, message, text and the parsed fields need
// the entries read and parsed (on the scan pool's threads, through FileSource.scanEntries), and are added when a query
// first names them. Big files load the first `cap` entries unless told otherwise. Everything is inserted in one
// transaction with a prepared statement; a cancelled load rolls back to the previous table. When the file grows the new
// entries are appended; when it is re-parsed or rotated the table is rebuilt.
//
// `row` is not stored: it is a function of the stored base position and the document's current view, so a new filter
// changes `row` and `view` without touching the table.
//
// Queries are read-only: guardSql first; then the statement's bytecode (EXPLAIN) must not write a table, attach a
// database, vacuum into a file or call load_extension (node:sqlite has no authorizer); and it runs on a second,
// read-only connection to the same (shared-cache, in-memory) database, with extensions off. Same rules as LogSql.cs in
// the Avalonia app, which uses an authorizer.
//
// node:sqlite cannot interrupt a statement, and a recursive CTE or a cross join can run for ever. So queries run on a
// worker thread (sqlWorker.ts, over a third connection to the same database) while the engine thread stays free: Cancel,
// a newer query or the time limit gives up on that worker. Its connection may still be inside SQLite, holding the
// database, so the table moves to a new database and the old one is left alone (never closed: that would wait for it).

import { DatabaseSync, StatementSync } from 'node:sqlite';
import { Worker } from 'node:worker_threads';
import type { LogDocument } from './document';
import type { FileSource } from './fileSource';
import { buildSqlColumns, guardSql, mentionedColumns, SQL_CORE, SQL_DEFAULT_CAP, SQL_TIME_LIMIT_MS, SqlColumn, SqlResult, SqlValue, sqlTimeLimitText, tokenizeSql } from './sqlSchema';

export class SqlCancelled extends Error {
  constructor() {
    super('Cancelled');
  }
}

const LEVEL_LABELS = ['', 'TRACE', 'DEBUG', 'INFO', 'WARN', 'ERROR', 'FATAL'];
/** Opcodes of statements that change a database or its files. */
const WRITE_OPS = new Set(['OpenWrite', 'Vacuum', 'IncrVacuum', 'ParseSchema', 'CreateBtree', 'Destroy', 'Clear', 'DropTable', 'DropIndex', 'DropTrigger', 'VCreate', 'VDestroy', 'VUpdate', 'JournalMode', 'SqlExec', 'LoadAnalysis']);
let dbCounter = 0;

const quote = (name: string) => `"${name.replace(/"/g, '""')}"`;
const lit = (s: string) => `'${s.replace(/'/g, "''")}'`;
const yieldNow = () => new Promise<void>((r) => setImmediate(r));

export class LogSql {
  /** Load at most this many entries (the first ones); 0 loads everything. */
  cap = SQL_DEFAULT_CAP;
  /** Entries loaded per hold of the document's queue, so filters and tail updates can run in between. */
  slice = 1 << 18;
  batch = 8192;
  /** Tests: skip guardSql to check that the engine blocks on its own. */
  skipGuard = false;
  /** A query running longer than this (on the worker) is given up. */
  timeLimitMs = SQL_TIME_LIMIT_MS;

  private writer!: DatabaseSync;
  private reader!: DatabaseSync;
  private uri = '';
  private worker: Worker | null = null;
  private queryId = 0;
  /** Gives up the query running on the worker. */
  private abortQuery: (() => void) | null = null;
  private schema: SqlColumn[] | null = null;
  /** Optional columns in the table now (lower-case names). */
  private loadedCols = new Set<string>();
  private built = false;
  private builtCap = 0;
  private loadedPerSource: number[] = [];
  private loadedCount = 0;
  /** Index changes since the last load: null = none; first changed entry per source; dirtyAll = everything. */
  private dirty: number[] | null = null;
  private dirtyAll = false;
  private queue: Promise<unknown> = Promise.resolve();
  private gen = 0;
  private cancelGen = 0;
  private closed = false;
  private unlisten: () => void;

  /** `queryWorker`: the built sqlWorker.js (absent in unit tests: queries then run on this thread). */
  constructor(
    private doc: LogDocument,
    private queryWorker?: string,
  ) {
    this.openDb();
    this.unlisten = doc.onIndexChanged((first) => this.indexChanged(first));
  }

  /** A new, empty in-memory database (at the start, and after giving up a query that may still hold the old one). */
  private openDb() {
    this.uri = `file:ulvsql-${process.pid}-${++dbCounter}?mode=memory&cache=shared`;
    this.writer = new DatabaseSync(this.uri);
    this.reader = new DatabaseSync(this.uri, { readOnly: true });
    // Sorting temp data stays in memory, never in a temp file.
    for (const db of [this.writer, this.reader]) db.exec('PRAGMA temp_store = MEMORY');
    sqlFunctions(this.reader, (b) => this.doc.viewRowOfBase(b));
    this.built = false;
    this.loadedCols = new Set();
    this.loadedPerSource = [];
    this.loadedCount = 0;
  }

  get loaded() {
    return this.loadedCount;
  }
  get total() {
    return this.doc.baseCount();
  }
  get capped() {
    return this.built && this.builtCap > 0 && this.loadedCount < this.total && this.loadedCount >= this.builtCap;
  }
  /** The optional columns loaded so far. */
  get loadedColumns(): string[] {
    return [...this.loadedCols];
  }

  private indexChanged(first: number[] | null) {
    if (!first) this.dirtyAll = true;
    else if (!this.dirty) this.dirty = [...first];
    else first.forEach((f, i) => (this.dirty![i] = i < this.dirty!.length ? Math.min(this.dirty![i], f) : f));
  }

  /** Serialises loads and queries. */
  private serial<T>(fn: () => Promise<T>): Promise<T> {
    const run = this.queue.then(fn, fn);
    this.queue = run.catch(() => undefined);
    return run;
  }

  /** Stops a running load (it rolls back) or gives up a running query. */
  cancel() {
    this.cancelGen = this.gen;
    this.abortQuery?.();
  }

  // ------------------------------------------------------------------ schema

  /** The table's columns: built-in ones and one per parsed field (field keys sampled from the entries). */
  schemaColumns(): Promise<SqlColumn[]> {
    return this.serial(() => this.schemaNow());
  }

  private async schemaNow(): Promise<SqlColumn[]> {
    if (this.dirtyAll) this.schema = null;
    if (!this.schema) {
      const keys = await this.doc.inQueue(async () => fieldKeys(this.doc.sources));
      this.schema = buildSqlColumns(keys);
    }
    return this.schema;
  }

  // ------------------------------------------------------------------- query

  /**
   * Runs a query, loading (or bringing up to date) the table first. Returns at most `maxRows` rows. SQL errors come back
   * in `error`; a cancelled load in `cancelled`.
   */
  query(sql: string, onProgress?: (fraction: number) => void, maxRows = 5000): Promise<SqlResult> {
    const denied = this.skipGuard ? null : guardSql(sql);
    if (denied) return Promise.resolve(this.result({ error: denied }));
    const gen = ++this.gen;
    return this.serial(async () => {
      const t0 = performance.now();
      let loadMs = 0;
      try {
        if (this.closed) throw new SqlCancelled();
        const schema = await this.schemaNow();
        const { names, star } = mentionedColumns(sql);
        const need = new Set(schema.filter((c) => !SQL_CORE.includes(c.name) && (star || names.has(c.name.toLowerCase()))).map((c) => c.name.toLowerCase()));
        await this.ensureLoaded(schema, need, gen, onProgress);
        loadMs = performance.now() - t0;
        const t1 = performance.now();
        const r = await this.runSomewhere(sql, maxRows, gen);
        return this.result({ ...r, queryMs: performance.now() - t1, loadMs });
      } catch (e) {
        if (e instanceof SqlCancelled) return this.result({ cancelled: true, loadMs });
        if (e instanceof SqlTimeLimit) return this.result({ error: e.message, loadMs });
        throw e;
      }
    });
  }

  private result(r: Partial<SqlResult>): SqlResult {
    return { columns: [], rows: [], truncated: false, error: null, queryMs: 0, loadMs: 0, loaded: this.loadedCount, total: this.total, capped: this.capped, ...r };
  }

  /** The query on the worker when there is one, else here. Throws SqlCancelled when it was given up. */
  private async runSomewhere(sql: string, maxRows: number, gen: number): Promise<QueryRows> {
    try {
      if (this.writes(sql)) return { columns: [], rows: [], truncated: false, error: 'not authorized' };
    } catch (e) {
      return { columns: [], rows: [], truncated: false, error: cleanError(e) };
    }
    if (!this.queryWorker) return runQuery(this.reader, sql, maxRows);
    if (this.cancelGen >= gen) throw new SqlCancelled();
    const w = (this.worker ??= new Worker(this.queryWorker));
    w.unref(); // a query left running never keeps the process alive
    const id = ++this.queryId;
    const { view, base } = this.doc.viewSnapshot();
    return new Promise<QueryRows>((resolve, reject) => {
      const done = () => {
        clearTimeout(timer);
        w.off('message', onMessage);
        w.off('error', onError);
        this.abortQuery = null;
      };
      const giveUp = (err: Error) => {
        done();
        // The worker may be inside SQLite, holding the database: leave both behind and load into a new one next time.
        void w.terminate();
        if (this.worker === w) this.worker = null;
        abandoned.push(this.writer, this.reader);
        if (!this.closed) this.openDb();
        reject(err);
      };
      const onMessage = (m: { id: number; result: QueryRows }) => {
        if (m.id !== id) return;
        done();
        resolve(m.result);
      };
      const onError = (e: Error) => giveUp(e);
      const timer = setTimeout(() => giveUp(new SqlTimeLimit(sqlTimeLimitText(this.timeLimitMs))), this.timeLimitMs);
      this.abortQuery = () => giveUp(new SqlCancelled());
      w.on('message', onMessage);
      w.on('error', onError);
      w.postMessage({ id, uri: this.uri, sql, maxRows, view, base });
    });
  }

  // -------------------------------------------------------------------- load

  private async ensureLoaded(schema: SqlColumn[], need: Set<string>, gen: number, onProgress?: (f: number) => void) {
    for (let attempt = 0; ; attempt++) {
      await this.doc.sync();
      const dirty = this.dirty;
      const dirtyAll = this.dirtyAll;
      this.dirty = null;
      this.dirtyAll = false;
      const sources = this.doc.sources.length;
      const full =
        !this.built || dirtyAll || this.builtCap !== this.cap || [...need].some((n) => !this.loadedCols.has(n)) || this.loadedPerSource.length !== sources || (!!dirty && this.doc.mergedNow);
      if (dirtyAll) {
        // Re-parsed (format change) or rotated: the fields may have changed.
        this.schema = null;
        schema = await this.schemaNow();
      }
      try {
        if (full) {
          const cols = new Set([...need, ...this.loadedCols].filter((n) => schema.some((c) => c.name.toLowerCase() === n)));
          await this.load(schema, cols, null, gen, onProgress);
        } else if (dirty) await this.load(schema, this.loadedCols, dirty, gen, onProgress);
        else return;
      } catch (e) {
        // The load rolled back: the table is as it was, so the changes still need loading next time.
        if (dirtyAll) this.dirtyAll = true;
        if (dirty) this.indexChanged(dirty);
        throw e;
      }
      // The file changed while loading: catch up (a few times at most, a live file never stops growing).
      if ((!this.dirty && !this.dirtyAll) || attempt >= 2) return;
    }
  }

  /**
   * Fills the table: from scratch (`from` null) or from the first changed entry of a single source. One transaction;
   * cancelling rolls back to the table as it was.
   */
  private async load(schema: SqlColumn[], cols: Set<string>, from: number[] | null, gen: number, onProgress?: (f: number) => void) {
    const optional = schema.filter((c) => cols.has(c.name.toLowerCase()));
    const cap = this.cap;
    const sources = this.doc.sources;
    const total = this.doc.baseCount();
    const limit = cap > 0 ? Math.min(cap, total) : total;
    const counts = sources.map((_, s) => this.doc.entriesBelow(s, limit));
    const loadedBefore = this.loadedPerSource;
    const starts = counts.map((c, s) => (from ? Math.min(from[s] ?? 0, c, loadedBefore[s] ?? 0) : 0));
    const work = counts.reduce((n, c, s) => n + Math.max(0, c - starts[s]), 0);
    let done = 0;
    const check = () => {
      if (this.closed || this.cancelGen >= gen) throw new SqlCancelled();
    };
    const w = this.writer;
    w.exec('BEGIN');
    let committed = false;
    try {
      if (!from) {
        w.exec('DROP VIEW IF EXISTS view; DROP VIEW IF EXISTS logs; DROP TABLE IF EXISTS logs_data');
        const defs = ['base INTEGER PRIMARY KEY', 'src INTEGER', 'line INTEGER', 'time INTEGER', 'lvl INTEGER', ...optional.map((c) => quote(c.name) + (c.field === null ? ' TEXT' : ''))];
        w.exec(`CREATE TABLE logs_data (${defs.join(', ')})`);
        w.exec(viewSql(optional, sources));
        w.exec('CREATE VIEW view AS SELECT * FROM logs WHERE row IS NOT NULL');
      } else {
        sources.forEach((_, s) => {
          if (starts[s] < (loadedBefore[s] ?? 0)) w.exec(`DELETE FROM logs_data WHERE src = ${s} AND base >= ${this.doc.baseOfEntry(s, starts[s])}`);
        });
      }
      const insert = w.prepare(`INSERT INTO logs_data VALUES (${new Array(5 + optional.length).fill('?').join(', ')})`);
      const row = (s: number, e: number, src: FileSource, values: (string | number | bigint | null)[] | null, offset: number) => {
        const t = src.time(e);
        const l = src.level(e);
        const args: (string | number | bigint | null)[] = [this.doc.baseOfEntry(s, e), s, src.entryLines(e)[0] + 1, Number.isNaN(t) ? null : t, l || null];
        for (let c = 0; c < optional.length; c++) args.push(values![offset + c]);
        insert.run(...args);
      };
      for (let s = 0; s < sources.length; s++) {
        const src = sources[s];
        for (let a = starts[s]; a < counts[s]; a += this.slice) {
          const z = Math.min(counts[s], a + this.slice);
          check();
          await this.doc.inQueue(async () => {
            if (!optional.length) {
              // Everything comes from the index: no need to read the file.
              for (let e = a; e < z; e++) {
                row(s, e, src, null, 0);
                if (((e - a) & 0xffff) === 0xffff) {
                  done += 0x10000;
                  onProgress?.(work ? done / work : 1);
                  await yieldNow();
                  check();
                }
              }
              done += (z - a) & 0xffff;
              return;
            }
            let cancelled = false;
            await src.scanEntries(
              a,
              z,
              { kind: 'sql', columns: optional },
              null,
              (first, result) => {
                if (this.closed || this.cancelGen >= gen) {
                  cancelled = true;
                  return false;
                }
                const values = result as (string | number | bigint | null)[];
                const n = values.length / optional.length;
                for (let i = 0; i < n; i++) row(s, first + i, src, values, i * optional.length);
                done += n;
                onProgress?.(work ? done / work : 1);
              },
              this.batch,
            );
            if (cancelled) throw new SqlCancelled();
          });
        }
      }
      w.exec('COMMIT');
      committed = true;
    } finally {
      if (!committed) w.exec('ROLLBACK');
    }
    this.built = true;
    this.builtCap = cap;
    this.loadedCols = new Set(optional.map((c) => c.name.toLowerCase()));
    this.loadedPerSource = counts;
    this.loadedCount = Number((w.prepare('SELECT count(*) AS n FROM logs_data').get() as { n: number }).n);
  }

  // ----------------------------------------------------------------- helpers

  /**
   * Whether the statement would write a table, open a database file (ATTACH, VACUUM INTO) or load an extension, read
   * from its bytecode the way sqlite3_stmt_readonly does. Sorting and window functions write only ephemeral tables
   * (OpenEphemeral), which are fine. A statement that does not compile is left to prepare() to report. PRAGMA is refused
   * outright: some pragmas (temp_store = FILE) take effect while the statement is compiled.
   */
  private writes(sql: string): boolean {
    const first = tokenizeSql(sql)[0];
    if (first?.kind === 'word' && first.text.toLowerCase() === 'pragma') return true;
    let ops: { opcode: string; p2: number; p4: unknown }[];
    try {
      ops = this.reader.prepare('EXPLAIN ' + sql).all() as unknown as typeof ops;
    } catch {
      return false;
    }
    return ops.some(
      (o) =>
        WRITE_OPS.has(o.opcode) ||
        (o.opcode === 'Transaction' && o.p2 !== 0) ||
        (/^(Pure)?Func(tion)?0?$/.test(o.opcode) && /^(sqlite_attach|sqlite_detach|load_extension)\(/i.test(String(o.p4 ?? ''))),
    );
  }

  close() {
    if (this.closed) return;
    this.closed = true;
    this.unlisten();
    this.abortQuery?.();
    void this.worker?.terminate();
    this.worker = null;
    // After whatever runs has stopped (a load sees `closed` and rolls back).
    void this.serial(async () => {
      if (abandoned.includes(this.writer)) return; // a given-up query may still be inside it
      this.reader.close();
      this.writer.close();
    });
  }
}

class SqlTimeLimit extends Error {}

/** Connections of queries that were given up: never closed (that could wait for them for ever) nor collected. */
const abandoned: DatabaseSync[] = [];

type QueryRows = Pick<SqlResult, 'columns' | 'rows' | 'truncated' | 'error'>;

/** Runs a (checked) query on a connection: at most `maxRows` rows. Also used by the query worker (sqlWorker.ts). */
export function runQuery(db: DatabaseSync, sql: string, maxRows: number): QueryRows {
  let st: StatementSync;
  try {
    st = db.prepare(sql);
  } catch (e) {
    return { columns: [], rows: [], truncated: false, error: cleanError(e) };
  }
  const columns = st.columns().map((c) => c.name);
  const arrays = typeof (st as unknown as { setReturnArrays?: (on: boolean) => void }).setReturnArrays === 'function';
  if (arrays) (st as unknown as { setReturnArrays: (on: boolean) => void }).setReturnArrays(true);
  const rows: SqlValue[][] = [];
  let truncated = false;
  try {
    for (const row of st.iterate()) {
      if (rows.length >= maxRows) {
        truncated = true;
        break;
      }
      rows.push(arrays ? (row as unknown as SqlValue[]) : columns.map((c) => (row as Record<string, SqlValue>)[c]));
    }
  } catch (e) {
    return { columns: [], rows: [], truncated: false, error: cleanError(e) };
  }
  return { columns, rows, truncated, error: null };
}

/** The functions queries can call: ulv_row (the view row of a base position), regexp and regexp_extract. */
export function sqlFunctions(db: DatabaseSync, viewRowOfBase: (base: number) => number | null) {
  const cache = new Map<string, RegExp>();
  const re = (pattern: string) => {
    let r = cache.get(pattern);
    if (!r) {
      if (cache.size > 64) cache.clear();
      cache.set(pattern, (r = new RegExp(pattern)));
    }
    return r;
  };
  // regexp_extract(value, pattern[, group]): the group's text (group 1 when the pattern has groups, else the match), or null.
  const extract = (value: unknown, pattern: unknown, group: unknown): string | null => {
    if (value === null || pattern === null) return null;
    const m = re(String(pattern)).exec(String(value));
    if (!m) return null;
    const g = group === null ? (m.length > 1 ? 1 : 0) : Number(group);
    return m[g] ?? null;
  };
  db.function('ulv_row', { deterministic: false }, (b) => (typeof b === 'number' || typeof b === 'bigint' ? viewRowOfBase(Number(b)) : null));
  db.function('regexp', { deterministic: true }, (pattern, value) => (pattern === null || value === null ? null : re(String(pattern)).test(String(value)) ? 1 : 0));
  db.function('regexp_extract', { deterministic: true }, (value, pattern) => extract(value, pattern, null));
  db.function('regexp_extract', { deterministic: true }, (value, pattern, group) => extract(value, pattern, group));
}

/** ulv_row over a snapshot of a view (null: every base row), as LogDocument.viewRowOfBase. */
export function viewRowIn(view: Uint32Array | null, base: number, b: number): number | null {
  if (!view) return b >= 0 && b < base ? b : null;
  let lo = 0,
    hi = view.length;
  while (lo < hi) {
    const mid = (lo + hi) >>> 1;
    if (view[mid] < b) lo = mid + 1;
    else hi = mid;
  }
  return lo < view.length && view[lo] === b ? lo : null;
}

/** "no such column: x" from node:sqlite's error. */
function cleanError(e: unknown): string {
  return (e as Error).message ?? String(e);
}

/**
 * Field keys in order of first appearance: the format's columns, then the keys of parsed entries (every entry of a small
 * file; the first 1000 and 20 blocks of 50 spread over a big one).
 */
export function fieldKeys(sources: FileSource[]): string[] {
  const keys: string[] = [];
  const seen = new Set<string>();
  const add = (k: string) => {
    if (!seen.has(k)) {
      seen.add(k);
      keys.push(k);
    }
  };
  for (const src of sources) {
    for (const c of src.format.columns) add(c.key);
    const n = src.entryCount;
    const ranges: [number, number][] = [];
    if (n <= 2000) ranges.push([0, n]);
    else {
      ranges.push([0, 1000]);
      for (let b = 0; b < 20; b++) {
        const start = Math.floor(1000 + ((n - 1050) * b) / 19);
        ranges.push([start, start + 50]);
      }
    }
    for (const [a, z] of ranges) for (const text of src.readEntries(a, Math.min(n, z))) for (const k of Object.keys(src.format.parse(text))) add(k);
  }
  return keys;
}

/** The `logs` view: computed row, source name, ISO time and level name over the stored columns. */
function viewSql(optional: SqlColumn[], sources: FileSource[]): string {
  const source = sources.length === 1 ? lit(sources[0].info().displayName) : `CASE src ${sources.map((s, i) => `WHEN ${i} THEN ${lit(s.info().displayName)} `).join('')}END`;
  const level = `CASE lvl ${LEVEL_LABELS.slice(1).map((l, i) => `WHEN ${i + 1} THEN '${l}' `).join('')}END`;
  return (
    `CREATE VIEW logs AS SELECT ulv_row(base) AS row, line, ${source} AS source, time, ` +
    `CASE WHEN time IS NULL THEN NULL ELSE strftime('%Y-%m-%dT%H:%M:%fZ', time / 1000.0, 'unixepoch') END AS time_iso, ` +
    `${level} AS level${optional.map((c) => ', ' + quote(c.name)).join('')} FROM logs_data`
  );
}
