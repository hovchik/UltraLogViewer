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

import { DatabaseSync, StatementSync } from 'node:sqlite';
import type { LogDocument } from './document';
import type { FileSource } from './fileSource';
import { buildSqlColumns, guardSql, mentionedColumns, SQL_CORE, SQL_DEFAULT_CAP, SqlColumn, SqlResult, SqlValue, tokenizeSql } from './sqlSchema';

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

  private writer: DatabaseSync;
  private reader: DatabaseSync;
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
  private regexCache = new Map<string, RegExp>();

  constructor(private doc: LogDocument) {
    const uri = `file:ulvsql-${process.pid}-${++dbCounter}?mode=memory&cache=shared`;
    this.writer = new DatabaseSync(uri);
    this.reader = new DatabaseSync(uri, { readOnly: true });
    // Sorting temp data stays in memory, never in a temp file.
    for (const db of [this.writer, this.reader]) db.exec('PRAGMA temp_store = MEMORY');
    this.reader.function('ulv_row', { deterministic: false }, (b) => (typeof b === 'number' || typeof b === 'bigint' ? this.doc.viewRowOfBase(Number(b)) : null));
    this.reader.function('regexp', { deterministic: true }, (pattern, value) =>
      pattern === null || value === null ? null : this.re(String(pattern)).test(String(value)) ? 1 : 0,
    );
    this.reader.function('regexp_extract', { deterministic: true }, (value, pattern) => this.extract(value, pattern, null));
    this.reader.function('regexp_extract', { deterministic: true }, (value, pattern, group) => this.extract(value, pattern, group));
    this.unlisten = doc.onIndexChanged((first) => this.indexChanged(first));
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

  /** Stops a running load (it rolls back). */
  cancel() {
    this.cancelGen = this.gen;
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
        const r = this.run(sql, maxRows);
        return this.result({ ...r, queryMs: performance.now() - t1, loadMs });
      } catch (e) {
        if (e instanceof SqlCancelled) return this.result({ cancelled: true, loadMs });
        throw e;
      }
    });
  }

  private result(r: Partial<SqlResult>): SqlResult {
    return { columns: [], rows: [], truncated: false, error: null, queryMs: 0, loadMs: 0, loaded: this.loadedCount, total: this.total, capped: this.capped, ...r };
  }

  private run(sql: string, maxRows: number): Pick<SqlResult, 'columns' | 'rows' | 'truncated' | 'error'> {
    let st: StatementSync;
    try {
      if (this.writes(sql)) return { columns: [], rows: [], truncated: false, error: 'not authorized' };
      st = this.reader.prepare(sql);
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

  private re(pattern: string): RegExp {
    let r = this.regexCache.get(pattern);
    if (!r) {
      if (this.regexCache.size > 64) this.regexCache.clear();
      this.regexCache.set(pattern, (r = new RegExp(pattern)));
    }
    return r;
  }

  /** regexp_extract(value, pattern[, group]): the group's text (group 1 when the pattern has groups, else the match), or null. */
  private extract(value: unknown, pattern: unknown, group: unknown): string | null {
    if (value === null || pattern === null) return null;
    const m = this.re(String(pattern)).exec(String(value));
    if (!m) return null;
    const g = group === null ? (m.length > 1 ? 1 : 0) : Number(group);
    return m[g] ?? null;
  }

  close() {
    if (this.closed) return;
    this.closed = true;
    this.unlisten();
    // After whatever runs has stopped (a load sees `closed` and rolls back).
    void this.serial(async () => {
      this.reader.close();
      this.writer.close();
    });
  }
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
