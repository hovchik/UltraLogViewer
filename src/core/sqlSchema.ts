// The SQL side of the log table, shared by the engine, the scan workers and the renderer: column names, the read-only
// guard, which columns a query needs, the values stored per entry, and the example queries. The Avalonia app has the
// same rules in SqlSchema.cs.

import type { LogFormat } from './formats';
import { messageOf } from './patterns';

/** A column of the `logs` table: a built-in one (field null), or a parsed field (field is its key). */
export interface SqlColumn {
  name: string;
  field: string | null;
}

export interface SqlExample {
  title: string;
  sql: string;
}

export type SqlValue = number | string | null | Uint8Array;

export interface SqlResult {
  columns: string[];
  /** At most the requested number of rows. */
  rows: SqlValue[][];
  /** More rows followed. */
  truncated: boolean;
  error: string | null;
  queryMs: number;
  loadMs: number;
  /** Entries in the table, and in the document. */
  loaded: number;
  total: number;
  /** Only the first `cap` entries were loaded. */
  capped: boolean;
  cancelled?: boolean;
}

/** Always there; logger, thread, message and text (and the fields) are loaded only when a query names them. */
export const SQL_CORE = ['row', 'line', 'source', 'time', 'time_iso', 'level'];
export const SQL_OPTIONAL = ['logger', 'thread', 'message', 'text'];
/** Parsed fields shown as built-in columns instead (time and level come from the index). */
const PARSED_BUILTINS = new Set(['time', 'level', 'logger', 'thread', 'message']);
export const SQL_MAX_FIELDS = 100;
export const SQL_DEFAULT_CAP = 2_000_000;
/** A query (not the load) that runs longer than this is stopped: a recursive CTE or a cross join can run for ever. */
export const SQL_TIME_LIMIT_MS = 120_000;
export const sqlTimeLimitText = (ms: number) => `The query ran for more than ${Math.round(ms / 1000)} s and was stopped`;

const KEYWORDS = new Set(
  (
    'abort action add after all alter always analyze and as asc attach autoincrement before begin between by cascade case cast check collate column ' +
    'commit conflict constraint create cross current current_date current_time current_timestamp database default deferrable deferred delete desc ' +
    'detach distinct do drop each else end escape except exclude exclusive exists explain fail filter first following for foreign from full generated ' +
    'glob group groups having if ignore immediate in index indexed initially inner insert instead intersect into is isnull join key last left like ' +
    'limit match materialized natural no not nothing notnull null nulls of offset on or order others outer over partition plan pragma preceding ' +
    'primary query raise range recursive references regexp reindex release rename replace restrict returning right rollback row rows savepoint ' +
    'select set table temp temporary then ties to transaction trigger unbounded union unique update using vacuum values view virtual when where ' +
    'window with without'
  ).split(' '),
);

/**
 * A parsed field's key as a column name: runs of characters other than letters, digits and _ become one _
 * ("http.status" → http_status), a leading digit gets "f_", and an SQL keyword gets a trailing _.
 */
export function sanitizeColumn(key: string): string {
  let s = '';
  for (const c of key) {
    if (/[A-Za-z0-9_]/.test(c)) s += c;
    else if (s.length && !s.endsWith('_')) s += '_';
  }
  s = s.replace(/^_+|_+$/g, '');
  if (!s) return 'field';
  if (/^[0-9]/.test(s)) s = 'f_' + s;
  if (KEYWORDS.has(s.toLowerCase())) s += '_';
  return s;
}

/**
 * The table's columns: the built-in ones, then one per field key (keys the built-in columns already cover are left
 * out; a name taken twice gets _2, _3…), at most SQL_MAX_FIELDS fields.
 */
export function buildSqlColumns(fieldKeys: Iterable<string>): SqlColumn[] {
  const cols: SqlColumn[] = [...SQL_CORE, ...SQL_OPTIONAL].map((name) => ({ name, field: null }));
  const taken = new Set(cols.map((c) => c.name.toLowerCase()));
  const keys = new Set<string>();
  for (const k of fieldKeys) {
    if (cols.length - SQL_CORE.length - SQL_OPTIONAL.length >= SQL_MAX_FIELDS) break;
    if (!k || k.startsWith('__') || PARSED_BUILTINS.has(k.toLowerCase()) || keys.has(k)) continue;
    keys.add(k);
    const name = sanitizeColumn(k);
    let unique = name;
    for (let n = 2; taken.has(unique.toLowerCase()); n++) unique = `${name}_${n}`;
    taken.add(unique.toLowerCase());
    cols.push({ name: unique, field: k });
  }
  return cols;
}

// ------------------------------------------------------------------- tokens

type TokKind = 'word' | 'quoted' | 'string' | 'number' | 'punct';
interface Tok {
  kind: TokKind;
  text: string;
}

const isWordStart = (c: string) => /[A-Za-z_]/.test(c) || c.charCodeAt(0) >= 0x80;

/** Splits SQL into tokens, dropping comments and whitespace. Unterminated strings run to the end. */
export function tokenizeSql(sql: string): Tok[] {
  const out: Tok[] = [];
  let i = 0;
  while (i < sql.length) {
    const c = sql[i];
    if (/\s/.test(c)) {
      i++;
      continue;
    }
    if (c === '-' && sql[i + 1] === '-') {
      while (i < sql.length && sql[i] !== '\n') i++;
      continue;
    }
    if (c === '/' && sql[i + 1] === '*') {
      const end = sql.indexOf('*/', i + 2);
      i = end < 0 ? sql.length : end + 2;
      continue;
    }
    if (c === "'" || c === '"' || c === '`' || c === '[') {
      const close = c === '[' ? ']' : c;
      let text = '';
      let j = i + 1;
      while (j < sql.length) {
        if (sql[j] === close) {
          if (close !== ']' && sql[j + 1] === close) {
            text += close;
            j += 2;
            continue;
          }
          break;
        }
        text += sql[j++];
      }
      out.push({ kind: c === "'" ? 'string' : 'quoted', text });
      i = Math.min(sql.length, j + 1);
      continue;
    }
    if (isWordStart(c)) {
      let j = i + 1;
      while (j < sql.length && (isWordStart(sql[j]) || /[0-9$]/.test(sql[j]))) j++;
      out.push({ kind: 'word', text: sql.slice(i, j) });
      i = j;
      continue;
    }
    if (/[0-9]/.test(c)) {
      let j = i + 1;
      while (j < sql.length && /[A-Za-z0-9._]/.test(sql[j])) j++;
      out.push({ kind: 'number', text: sql.slice(i, j) });
      i = j;
      continue;
    }
    out.push({ kind: 'punct', text: c });
    i++;
  }
  return out;
}

const DENIED = new Set(['insert', 'update', 'delete', 'replace', 'create', 'drop', 'alter', 'pragma', 'vacuum', 'reindex', 'analyze', 'begin', 'commit', 'rollback', 'savepoint', 'release', 'attach', 'detach']);

/**
 * Why the query may not run, or null: it must be one SELECT (or WITH / VALUES) statement, without anything that
 * writes, opens other files (ATTACH) or loads extensions. The engine enforces this as well (a read-only connection,
 * extensions off); the check here gives a clear message first.
 */
export function guardSql(sql: string): string | null {
  let toks = tokenizeSql(sql);
  const semi = toks.findIndex((t) => t.kind === 'punct' && t.text === ';');
  if (semi >= 0) {
    if (toks.slice(semi + 1).some((t) => !(t.kind === 'punct' && t.text === ';'))) return 'Run one statement at a time.';
    toks = toks.slice(0, semi);
  }
  if (!toks.length) return 'Enter a query.';
  for (let i = 0; i < toks.length; i++) {
    if (toks[i].kind !== 'word') continue;
    const w = toks[i].text.toLowerCase();
    const call = toks[i + 1]?.kind === 'punct' && toks[i + 1].text === '(';
    if (w === 'load_extension') return 'load_extension is not allowed.';
    if (w === 'attach' || w === 'detach') return `${w.toUpperCase()} is not allowed: queries cannot open other files.`;
    if (DENIED.has(w) && !(w === 'replace' && call)) return `${w.toUpperCase()} is not allowed: the table is read-only.`;
  }
  const first = toks[0].kind === 'word' ? toks[0].text.toLowerCase() : '';
  if (first !== 'select' && first !== 'with' && first !== 'values') return 'Only SELECT queries can run here: the table is read-only.';
  return null;
}

/** The identifiers a query names (lower case), and whether it selects * (or t.*), which needs every column. */
export function mentionedColumns(sql: string): { names: Set<string>; star: boolean } {
  const toks = tokenizeSql(sql);
  const names = new Set<string>();
  let star = false;
  toks.forEach((t, i) => {
    if (t.kind === 'word' || t.kind === 'quoted') names.add(t.text.toLowerCase());
    else if (t.kind === 'punct' && t.text === '*' && i > 0) {
      const p = toks[i - 1];
      if ((p.kind === 'punct' && (p.text === ',' || p.text === '.')) || (p.kind === 'word' && ['select', 'distinct', 'all'].includes(p.text.toLowerCase()))) star = true;
    }
  });
  return { names, star };
}

// ------------------------------------------------------------------- values

const NUMBER = /^-?(0|[1-9][0-9]{0,14})(\.[0-9]{1,15})?$/;

/**
 * A field value as stored: an integer (as a bigint, so SQLite stores an INTEGER) or a decimal without leading zeros
 * (so ids like 007 stay text), else the text; empty is null.
 */
export function sqlFieldValue(s: string | undefined): string | number | bigint | null {
  if (!s) return null;
  if (!NUMBER.test(s)) return s;
  return s.includes('.') ? Number(s) : BigInt(s);
}

/**
 * The optional column values of a batch of entries, row after row (texts.length × columns.length). Runs on the scan
 * workers: parsing is the expensive part of loading the table.
 */
export function sqlRowValues(fmt: LogFormat, columns: SqlColumn[], texts: string[]): (string | number | bigint | null)[] {
  const needParse = columns.some((c) => c.name !== 'text' || c.field !== null);
  const out: (string | number | bigint | null)[] = new Array(texts.length * columns.length);
  let k = 0;
  for (const text of texts) {
    const parsed = needParse ? fmt.parse(text) : null;
    for (const c of columns) {
      if (c.field !== null) out[k++] = sqlFieldValue(parsed![c.field]);
      else if (c.name === 'text') out[k++] = text;
      else if (c.name === 'message') out[k++] = messageOf(text, parsed);
      else out[k++] = parsed![c.name] || null; // logger, thread
    }
  }
  return out;
}

/** A result the chart can draw: a label column, then a number in every row that has a value. */
export function sqlChartable(r: SqlResult): boolean {
  return !r.error && r.columns.length >= 2 && r.rows.length > 0 && r.rows.every((x) => x[1] === null || typeof x[1] === 'number') && r.rows.some((x) => typeof x[1] === 'number');
}

/** How a value shows in a cell: NULL, numbers as they are, the first line of a text, a short hex for blobs. */
export function sqlCell(v: SqlValue): string {
  if (v === null || v === undefined) return 'NULL';
  if (typeof v === 'number') return Number.isInteger(v) ? String(v) : String(Math.round(v * 1e6) / 1e6);
  if (v instanceof Uint8Array) {
    const hex = Array.from(v.subarray(0, 32), (b) => b.toString(16).padStart(2, '0').toUpperCase()).join('');
    return `x'${hex}${v.length > 32 ? '…' : ''}'`;
  }
  let s = String(v);
  const nl = s.indexOf('\n');
  if (nl >= 0) s = s.slice(0, nl).replace(/\r$/, '') + ' …';
  return s.length > 300 ? s.slice(0, 300) + '…' : s;
}

// ----------------------------------------------------------------- examples

export const SQL_DEFAULT_QUERY = 'SELECT level, count(*) AS entries\nFROM logs\nGROUP BY level\nORDER BY entries DESC';

const DURATION = /duration|elapsed|latency|took|response_?time|(^|_)ms$/i;

/** Example queries for the dropdown; "slowest" uses a parsed duration field when the format has one. */
export function sqlExamples(cols: SqlColumn[]): SqlExample[] {
  const dur = cols.find((c) => c.field !== null && DURATION.test(c.name))?.name;
  const slowest = dur
    ? `SELECT line, time_iso, ${dur} AS duration, message\nFROM logs\nWHERE ${dur} IS NOT NULL\nORDER BY CAST(${dur} AS REAL) DESC\nLIMIT 50`
    : "SELECT line, time_iso, CAST(regexp_extract(message, '(\\d+(?:\\.\\d+)?) ?ms\\b') AS REAL) AS ms, message\nFROM logs\nWHERE ms IS NOT NULL\nORDER BY ms DESC\nLIMIT 50";
  return [
    { title: 'Top loggers by errors', sql: "SELECT logger, count(*) AS errors\nFROM logs\nWHERE level IN ('ERROR', 'FATAL')\nGROUP BY logger\nORDER BY errors DESC\nLIMIT 20" },
    { title: 'Errors per minute', sql: "SELECT strftime('%Y-%m-%d %H:%M', time / 1000, 'unixepoch') AS minute, count(*) AS errors\nFROM logs\nWHERE level IN ('ERROR', 'FATAL') AND time IS NOT NULL\nGROUP BY minute\nORDER BY minute" },
    { title: 'Slowest requests', sql: slowest },
    { title: 'Longest gaps between entries (LAG)', sql: 'SELECT line, time_iso, time - LAG(time) OVER (ORDER BY time, line) AS gap_ms, message\nFROM logs\nWHERE time IS NOT NULL\nORDER BY gap_ms DESC\nLIMIT 50' },
    { title: 'Levels in the filtered view', sql: 'SELECT level, count(*) AS entries,\n  round(100.0 * count(*) / sum(count(*)) OVER (), 1) AS pct\nFROM view\nGROUP BY level\nORDER BY entries DESC' },
  ];
}
