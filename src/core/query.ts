import { parseLevel } from './levels';
import { matchTimestamp, parseTimestamp } from './timestamp';
import { FilterRule, Level, TextPattern } from './types';

const escapeRe = (s: string) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

/** Builds a global RegExp for a pattern, or throws on an invalid regex. */
export function compilePattern(p: TextPattern): RegExp {
  let src = p.regex ? p.text : escapeRe(p.text);
  if (p.wholeWord) src = `\\b(?:${src})\\b`;
  return new RegExp(src, p.caseSensitive ? 'g' : 'gi');
}

export type Matcher = (text: string) => boolean;

/** A fast test function. Plain substring search avoids the regex engine where possible. */
export function compileMatcher(p: TextPattern): Matcher {
  if (!p.regex && !p.wholeWord) {
    if (p.caseSensitive) {
      const needle = p.text;
      return (t) => t.includes(needle);
    }
    // V8's case-insensitive regex on a literal beats lowercasing every line.
    const re = new RegExp(escapeRe(p.text), 'i');
    return (t) => re.test(t);
  }
  const re = compilePattern(p);
  const sticky = new RegExp(re.source, re.flags.replace('g', ''));
  return (t) => sticky.test(t);
}

export function validatePattern(p: TextPattern): string | null {
  try {
    compilePattern(p);
    return null;
  } catch (e) {
    return (e as Error).message;
  }
}

/** All [start, end) match ranges of a pattern in a string (used for highlighting). */
export function matchRanges(re: RegExp, text: string, limit = 1000): [number, number][] {
  const out: [number, number][] = [];
  re.lastIndex = 0;
  let m: RegExpExecArray | null;
  while ((m = re.exec(text)) && out.length < limit) {
    if (m[0].length === 0) {
      re.lastIndex++;
      continue;
    }
    out.push([m.index, m.index + m[0].length]);
  }
  return out;
}

const isSpace = (c: string) => /\s/.test(c);

/** The whitespace-delimited token that the caret is in or just after, as [start, length] (for autocomplete). */
export function tokenAt(text: string, caret: number): [number, number] {
  caret = Math.max(0, Math.min(text.length, caret));
  let start = caret;
  let quotes = 0;
  for (let i = 0; i < caret; i++) if (text[i] === '"') quotes++;
  // Inside an open quote, the token starts before the quote's field prefix.
  while (start > 0 && (!isSpace(text[start - 1]) || quotes % 2 === 1)) {
    start--;
    if (text[start] === '"') quotes--;
  }
  let end = caret;
  while (end < text.length && !isSpace(text[end])) end++;
  return [start, end - start];
}

/** Matcher for a filter rule (exact rules compare the trimmed value). */
export function ruleMatcher(r: FilterRule): Matcher {
  // Pattern rules are tested against the entry's message template (ordinal equality).
  if (r.patternMatch) {
    const tpl = r.text;
    return (t) => t === tpl;
  }
  if (!r.exact) return compileMatcher(r);
  const needle = r.text.trim();
  if (r.caseSensitive) return (t) => t.trim() === needle;
  const lower = needle.toLowerCase();
  return (t) => t.trim().toLowerCase() === lower;
}

// ------------------------------------------------------------ quick filter

/**
 * The quick-filter language. Old queries keep their meaning: words (all must appear), `a OR b`, `-word` / `NOT word`,
 * `"exact phrase"`, `field:value` / `field:"two words"` (contains, in a parsed field), `/regex/`.
 * New: `and`/`or`/`not` in either case with parentheses, comparisons `status>=500`, `duration>2s`, `status!=200`,
 * `user==bob`, `field?=x` (also matches entries without the field), `user in (alice,bob)`, `exists(traceId)`, and
 * `since:-3h` / `until:2024-05-01T10:00`. OR binds tighter than AND (as it always has: `a b OR c` is a AND (b OR c)).
 * Input that is still being typed (a trailing operator, an unclosed parenthesis, `logger:`) filters nothing rather
 * than failing.
 */
export type QueryNode =
  | { kind: 'and'; items: QueryNode[] }
  | { kind: 'or'; items: QueryNode[] }
  | { kind: 'not'; item: QueryNode }
  /** Text or regex inside the entry, or inside one parsed field (`field:value`). */
  | { kind: 'text'; field: string | null; pattern: TextPattern }
  /** `field op value` with op one of > >= < <= == != ?= */
  | { kind: 'compare'; field: string; op: string; value: string }
  | { kind: 'in'; field: string; values: string[] }
  | { kind: 'exists'; field: string }
  /** `since:` / `until:`, resolved to epoch ms and tested against the indexed entry time. */
  | { kind: 'time'; since: boolean; ms: number; text: string };

/** Thrown for a query that cannot be parsed; the message is meant for the user. */
export class QueryError extends Error {}

/** Pseudo-field answered from the index: the entry's time in epoch ms, or '' when it has none. */
export const TIME_FIELD = '@time';

type Tok = { kind: 'atom'; node: QueryNode } | { kind: 'lparen' | 'rparen' | 'and' | 'or' | 'not' };

const FIELD_START = '[A-Za-z_@]';
const FIELD_CHAR = '[\\w.-]';
const REGEX_TOK = /\/(?:\\.|[^/])+\//y;
const QUOTED_FIELD_TOK = new RegExp(`(${FIELD_START}${FIELD_CHAR}*?)(:|>=|<=|==|!=|\\?=|>|<)"([^"]*)"?`, 'y');
const FIELD_OP_RE = new RegExp(`^(${FIELD_START}${FIELD_CHAR}*?)(:|>=|<=|==|!=|\\?=|>|<)([\\s\\S]*)$`);
const EXISTS_RE = /^exists\(([^()]*)\)$/i;
const IN_RE = /\s+(?:in|IN)\s*\(/y;
const FIELD_NAME_RE = new RegExp(`^${FIELD_START}${FIELD_CHAR}*$`);
const DURATION_PART = /(\d+(?:\.\d+)?|\.\d+)\s*([a-zµ]+)/iy;
const NUMBER_RE = /^[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?$/;
const UNIT_LIKE = /^[+-]?(?:\d+(?:\.\d+)?|\.\d+)\s*[A-Za-zµ]+$/;
const ABS_TIME = /^(\d{4})-(\d{2})-(\d{2})(?:[T ](\d{2}):(\d{2})(?::(\d{2})(?:[.,](\d{1,9}))?)?)?\s*(Z|[+-]\d{2}:?\d{2})?$/;

const sticky = (re: RegExp, s: string, at: number) => {
  re.lastIndex = at;
  return re.exec(s);
};

/** Parses a quick-filter query; null means "everything matches". Throws QueryError with a readable message. */
export function parseQueryExpr(q: string, caseSensitive = false, now = Date.now()): QueryNode | null {
  const toks = lex(q, caseSensitive, now);
  const pos = { i: 0 };
  return parseAnd(toks, pos, false);
}

/** The error message for a query, or null when it is valid. */
export function validateQuery(q: string, caseSensitive = false): string | null {
  try {
    compileQuery(q, caseSensitive);
    return null;
  } catch (e) {
    return (e as Error).message;
  }
}

/**
 * `field(name)` returns a parsed field value ('' when missing). It must answer "level" and TIME_FIELD from the
 * index, because clauses on those run first and can reject an entry before anything has to parse it.
 */
export type EntryPredicate = (text: string, field: (name: string) => string) => boolean;

export function compileQuery(q: string, caseSensitive = false, now = Date.now()): EntryPredicate {
  const root = parseQueryExpr(q, caseSensitive, now);
  if (!root) return () => true;
  return build(root, caseSensitive).p;
}

/**
 * compileQuery with the `since:`/`until:` clauses that every match must pass taken out as a time range, so the caller
 * can test them with its time index like the time-range filter (no per-entry work at all for a query such as
 * `since:-1h`). `predicate` is null when nothing else is left to test.
 */
export function compileQueryWithTimeRange(q: string, caseSensitive = false, now = Date.now()): { predicate: EntryPredicate | null; from: number | null; to: number | null } {
  const root = parseQueryExpr(q, caseSensitive, now);
  let from: number | null = null;
  let to: number | null = null;
  const rest: QueryNode[] = [];
  for (const n of root?.kind === 'and' ? root.items : root ? [root] : []) {
    if (n.kind === 'time' && n.since) from = Math.max(from ?? -Infinity, n.ms);
    else if (n.kind === 'time') to = Math.min(to ?? Infinity, n.ms);
    else rest.push(n);
  }
  if (!rest.length) return { predicate: null, from, to };
  return { predicate: build(rest.length === 1 ? rest[0] : { kind: 'and', items: rest }, caseSensitive).p, from, to };
}

// ---- lexer

function lex(q: string, cs: boolean, now: number): Tok[] {
  const toks: Tok[] = [];
  const atom = (node: QueryNode | null) => node && toks.push({ kind: 'atom', node });
  const textPat = (text: string, regex = false): TextPattern => ({ text, regex, caseSensitive: cs, wholeWord: false });
  let depth = 0;
  let afterDash = false;
  let i = 0;
  while (i < q.length) {
    const c = q[i];
    if (isSpace(c)) {
      i++;
      afterDash = false;
      continue;
    }
    const dash = afterDash;
    afterDash = false;
    if (c === '(') {
      toks.push({ kind: 'lparen' });
      depth++;
      i++;
      continue;
    }
    if (c === ')' && (depth > 0 || i + 1 === q.length || isSpace(q[i + 1]))) {
      if (depth === 0) throw new QueryError('Unmatched ")"');
      toks.push({ kind: 'rparen' });
      depth--;
      i++;
      continue;
    }
    if (c === '-' && !dash && i + 1 < q.length && !isSpace(q[i + 1])) {
      // "-x" negates; "--x" negates "-x" (as before).
      toks.push({ kind: 'not' });
      afterDash = true;
      i++;
      continue;
    }
    if (c === '"') {
      const close = q.indexOf('"', i + 1); // an unclosed quote is fine: the user is still typing
      const text = close < 0 ? q.slice(i + 1) : q.slice(i + 1, close);
      i = close < 0 ? q.length : close + 1;
      if (text) atom({ kind: 'text', field: null, pattern: textPat(text) });
      continue;
    }
    let m: RegExpExecArray | null;
    if (c === '/' && (m = sticky(REGEX_TOK, q, i))) {
      i += m[0].length;
      atom({ kind: 'text', field: null, pattern: textPat(m[0].slice(1, -1), true) });
      continue;
    }
    if ((m = sticky(QUOTED_FIELD_TOK, q, i))) {
      i += m[0].length;
      atom(fieldNode(m[1], m[2], m[3], true, cs, now).node);
      continue;
    }
    let end = i;
    while (end < q.length && !isSpace(q[end])) end++;
    let word = q.slice(i, end);
    // ")" glued to a word closes a group ("(a OR b)"), unless the word opened it itself ("foo()").
    if (depth > 0) {
      const excess = (word.match(/\)/g)?.length ?? 0) - (word.match(/\(/g)?.length ?? 0);
      let strip = 0;
      while (strip < Math.min(excess, depth) && word.length - strip > 1 && word[word.length - 1 - strip] === ')') strip++;
      word = word.slice(0, word.length - strip);
      end -= strip;
    }
    i = end;
    if (word === 'OR' || word === 'or' || word === '|' || word === '||') {
      toks.push({ kind: 'or' });
      continue;
    }
    if (word === 'AND' || word === 'and' || word === '&&') {
      toks.push({ kind: 'and' });
      continue;
    }
    if (word === 'NOT' || word === 'not') {
      toks.push({ kind: 'not' });
      continue;
    }
    if ((m = EXISTS_RE.exec(word))) {
      const f = m[1].trim();
      if (!f) throw new QueryError('exists() needs a field name, e.g. exists(traceId)');
      atom({ kind: 'exists', field: f.toLowerCase() });
      continue;
    }
    if (/^exists\(/i.test(word) && !word.includes(')')) continue; // still typing
    if ((m = FIELD_OP_RE.exec(word))) {
      const r = fieldNode(m[1], m[2], m[3], false, cs, now);
      if (r.node || r.skip) {
        atom(r.node);
        continue;
      }
    }
    if (FIELD_NAME_RE.test(word) && (m = sticky(IN_RE, q, end))) {
      const [values, next] = readList(q, end + m[0].length);
      i = next;
      if (!values) continue; // "user in (ali" is still being typed
      if (!values.length) throw new QueryError(`${word} in (): add at least one value, e.g. ${word} in (alice,bob)`);
      atom({ kind: 'in', field: word.toLowerCase(), values });
      continue;
    }
    atom({ kind: 'text', field: null, pattern: textPat(word) });
  }
  return toks;
}

/** The node for `field op value`. node null with skip = nothing to filter yet; without skip = not a field token. */
function fieldNode(field: string, op: string, value: string, quoted: boolean, cs: boolean, now: number): { node: QueryNode | null; skip: boolean } {
  const f = field.toLowerCase();
  if (op === ':') {
    if (f === 'since' || f === 'until') {
      if (!value) return { node: null, skip: true };
      const t = parseWhen(value, now);
      if (t === null) throw new QueryError(`${f}:${value}: use a relative time such as -3h or -15m, or a date such as 2024-05-01T10:00`);
      return { node: { kind: 'time', since: f === 'since', ms: t, text: value }, skip: true };
    }
    if (!value) return { node: null, skip: true }; // "logger:" is still being typed
    const regex = !quoted && value.length > 1 && value.startsWith('/') && value.endsWith('/');
    if (!regex && !quoted && value.includes('*')) return { node: { kind: 'text', field: f, pattern: { text: globRegex(value), regex: true, caseSensitive: cs, wholeWord: false } }, skip: true };
    return { node: { kind: 'text', field: f, pattern: { text: regex ? value.slice(1, -1) : value, regex, caseSensitive: cs, wholeWord: false } }, skip: true };
  }
  // "a->b" and "x.>y" are text, not comparisons.
  if (f.endsWith('-') || f.endsWith('.')) return { node: null, skip: false };
  if (!value) return { node: null, skip: true }; // "status>=" is still being typed
  const node: QueryNode = { kind: 'compare', field: f, op, value };
  compileCompare(node, cs); // reports bad units and levels now
  return { node, skip: true };
}

/**
 * The regex of a `field:value` wildcard: "*" is any run of characters and the whole value must match. A trailing ".*"
 * is a package (logger hierarchy): `logger:com.acme.*` matches com.acme itself and everything below it.
 */
export function globRegex(glob: string): string {
  if (glob.length > 2 && glob.endsWith('.*') && !glob.slice(0, -2).includes('*')) return `^${escapeRe(glob.slice(0, -2))}(?:\\..*)?$`;
  return `^${glob.split('*').map(escapeRe).join('.*')}$`;
}

/** Reads `a, b, "c d")` after "in (". Values is null when the list is not closed yet. */
export function readList(q: string, i: number): [string[] | null, number] {
  const values: string[] = [];
  let cur = '';
  let quoted = false;
  const flush = () => {
    const v = cur.trim();
    if (v) values.push(v);
    cur = '';
  };
  for (; i < q.length; i++) {
    const c = q[i];
    if (c === '"') quoted = !quoted;
    else if (quoted) cur += c;
    else if (c === ',') flush();
    else if (c === ')') {
      flush();
      return [values, i + 1];
    } else cur += c;
  }
  return [null, q.length];
}

// ---- parser:  and := or ([AND] or)*    or := unary (OR unary)*    unary := (NOT | -) unary | "(" and ")" | atom

function parseAnd(t: Tok[], pos: { i: number }, inGroup: boolean): QueryNode | null {
  const items: QueryNode[] = [];
  while (pos.i < t.length) {
    const k = t[pos.i].kind;
    if (k === 'rparen' && inGroup) break;
    if (k === 'and' || k === 'or' || k === 'rparen') {
      pos.i++; // a stray operator (e.g. a leading OR) filters nothing, as before
      continue;
    }
    const x = parseOr(t, pos);
    if (x) items.push(x);
  }
  return items.length === 0 ? null : items.length === 1 ? items[0] : { kind: 'and', items };
}

function parseOr(t: Tok[], pos: { i: number }): QueryNode | null {
  const items: QueryNode[] = [];
  const first = parseUnary(t, pos);
  if (first) items.push(first);
  while (pos.i < t.length && t[pos.i].kind === 'or') {
    pos.i++;
    const x = parseUnary(t, pos);
    if (x) items.push(x);
  }
  return items.length === 0 ? null : items.length === 1 ? items[0] : { kind: 'or', items };
}

function parseUnary(t: Tok[], pos: { i: number }): QueryNode | null {
  if (pos.i >= t.length) return null;
  const tok = t[pos.i];
  switch (tok.kind) {
    case 'not': {
      pos.i++;
      const x = parseUnary(t, pos);
      return !x ? null : x.kind === 'not' ? x.item : { kind: 'not', item: x };
    }
    case 'lparen': {
      pos.i++;
      const e = parseAnd(t, pos, true);
      if (pos.i < t.length && t[pos.i].kind === 'rparen') {
        pos.i++;
        if (!e) throw new QueryError('Empty parentheses "()"');
      }
      return e; // an unclosed "(" closes at the end
    }
    case 'atom':
      pos.i++;
      return tok.node;
    default:
      return null;
  }
}

/** A canonical text form of a parsed query, for tests and diagnostics. */
export function describeQuery(n: QueryNode | null): string {
  if (!n) return '';
  switch (n.kind) {
    case 'and':
    case 'or':
      return `(${n.kind} ${n.items.map(describeQuery).join(' ')})`;
    case 'not':
      return `(not ${describeQuery(n.item)})`;
    case 'text':
      return (n.field === null ? '' : n.field + ':') + (n.pattern.regex ? `/${n.pattern.text}/` : `"${n.pattern.text}"`);
    case 'compare':
      return n.field + n.op + n.value;
    case 'in':
      return `${n.field} in (${n.values.join(',')})`;
    case 'exists':
      return `exists(${n.field})`;
    case 'time':
      return (n.since ? 'since:' : 'until:') + n.text;
  }
}

// ---- evaluator

/**
 * Cost classes: 0 = answered from the index (level, time), 1 = a scan of the raw text, 2 = needs the parsed entry.
 * AND and OR evaluate their children cheapest first, so a level or time clause rejects an entry before any clause
 * has to parse it.
 */
const fieldCost = (f: string) => (f === 'level' || f === TIME_FIELD ? 0 : 2);

function build(n: QueryNode, cs: boolean): { p: EntryPredicate; cost: number } {
  switch (n.kind) {
    case 'and':
    case 'or': {
      // Array.prototype.sort is stable, so equal-cost clauses keep the order they were typed in.
      const parts = n.items.map((x) => build(x, cs)).sort((a, b) => a.cost - b.cost);
      const ps = parts.map((x) => x.p);
      const cost = Math.max(...parts.map((x) => x.cost));
      if (n.kind === 'and')
        return {
          p: (text, f) => {
            for (const p of ps) if (!p(text, f)) return false;
            return true;
          },
          cost,
        };
      return {
        p: (text, f) => {
          for (const p of ps) if (p(text, f)) return true;
          return false;
        },
        cost,
      };
    }
    case 'not': {
      const { p, cost } = build(n.item, cs);
      return { p: (text, f) => !p(text, f), cost };
    }
    case 'text': {
      const m = compileMatcher(n.pattern);
      const key = n.field;
      if (key === null) return { p: (text) => m(text), cost: 1 };
      return { p: (_, f) => m(f(key)), cost: fieldCost(key) };
    }
    case 'compare': {
      const test = compileCompare(n, cs);
      const key = n.field;
      return { p: (_, f) => test(f(key)), cost: fieldCost(key) };
    }
    case 'in': {
      const tests = n.values.map((value) => compileCompare({ field: n.field, op: '==', value }, cs));
      const key = n.field;
      return {
        p: (_, f) => {
          const v = f(key);
          return tests.some((t) => t(v));
        },
        cost: fieldCost(key),
      };
    }
    case 'exists': {
      const key = n.field;
      return { p: (_, f) => f(key).length > 0, cost: fieldCost(key) };
    }
    case 'time': {
      const { since, ms } = n;
      return {
        p: (_, f) => {
          const s = f(TIME_FIELD);
          const t = s ? Number(s) : NaN;
          return since ? t >= ms : t <= ms; // NaN (no timestamp) fails both
        },
        cost: 0,
      };
    }
  }
}

const sign = (a: number | string, b: number | string) => (a < b ? -1 : a > b ? 1 : 0);

function ordered(op: string, cmp: number): boolean {
  switch (op) {
    case '>':
      return cmp > 0;
    case '>=':
      return cmp >= 0;
    case '<':
      return cmp < 0;
    case '<=':
      return cmp <= 0;
    default:
      return cmp === 0;
  }
}

/**
 * A test on one field value. Numbers compare numerically, durations by length (a bare number on one side is read in
 * the other side's unit, so `query_time>1s` matches "2.5"), dates as times, anything else as text.
 * Every comparison except `?=` needs the field to be present.
 */
function compileCompare(c: { field: string; op: string; value: string }, cs: boolean): (s: string) => boolean {
  const { op, value } = c;
  const equality = op === '==' || op === '!=' || op === '?=';
  const wrap = (compare: (s: string) => number | null): ((s: string) => boolean) => {
    if (op === '!=')
      return (s) => {
        const r = s.length > 0 ? compare(s) : null;
        return r !== null && r !== 0;
      };
    if (op === '?=') return (s) => s.length === 0 || compare(s) === 0;
    return (s) => {
      const r = s.length > 0 ? compare(s) : null;
      return r !== null && ordered(op, r);
    };
  };
  if (c.field === 'level') {
    const want = parseLevel(value);
    if (want === Level.None && value.toLowerCase() !== 'none') throw new QueryError(`level${op}${value}: "${value}" is not a level (trace, debug, info, warn, error, fatal)`);
    return wrap((s) => sign(parseLevel(s), want));
  }
  const fold = (s: string) => (cs ? s : s.toUpperCase());
  const strCmp = (s: string) => sign(fold(s.trim()), fold(value));
  const q = quantity(value);
  if (q) {
    return wrap((s) => {
      const f = quantity(s);
      if (!f) return equality ? (strCmp(s) === 0 ? 0 : 1) : null;
      // a bare number takes the other side's unit
      const fv = f.unit === 0 && q.unit > 0 ? f.value * q.unit : f.value;
      const qv = q.unit === 0 && f.unit > 0 ? q.value * f.unit : q.value;
      return sign(fv, qv);
    });
  }
  if (UNIT_LIKE.test(value) && !equality) throw new QueryError(`${c.field}${op}${value}: unknown unit (use ms, s, m or h, e.g. ${c.field}${op}2s)`);
  const qt = equality ? null : parseAbsolute(value);
  if (qt !== null) {
    return wrap((s) => {
      const t = parseTimestamp(s);
      return t === null ? null : sign(t, qt);
    });
  }
  return wrap(strCmp);
}

const UNIT_MS: Record<string, number> = Object.assign(Object.create(null), {
  ns: 1e-6, us: 1e-3, µs: 1e-3, ms: 1, s: 1000, sec: 1000, secs: 1000, m: 60_000, min: 60_000, mins: 60_000,
  h: 3_600_000, hr: 3_600_000, hrs: 3_600_000, d: 86_400_000, w: 604_800_000,
});

/**
 * A number, or a duration such as "250ms", "1.5s" or "2m30s" (value in ms). `unit` is the length of the last unit in
 * ms, or 0 for a bare number.
 */
export function quantity(s: string): { value: number; unit: number } | null {
  s = s.trim();
  if (!s) return null;
  if (NUMBER_RE.test(s)) return { value: Number(s), unit: 0 };
  const neg = s[0] === '-';
  let i = neg || s[0] === '+' ? 1 : 0;
  let total = 0;
  let unit = 0;
  while (i < s.length) {
    const m = sticky(DURATION_PART, s, i);
    if (!m) return null;
    const u = UNIT_MS[m[2].toLowerCase()];
    if (!u) return null;
    total += Number(m[1]) * u;
    unit = u;
    i += m[0].length;
  }
  if (!unit) return null;
  return { value: neg ? -total : total, unit };
}

/** "2024-05-01", "2024-05-01T10:00", "2024-05-01 10:00:05.5+02:00" (local time without an offset), or a full log timestamp. */
function parseAbsolute(s: string): number | null {
  s = s.trim();
  const m = ABS_TIME.exec(s);
  if (m) {
    const g = (i: number) => (m[i] ? Number(m[i]) : 0);
    const ms = m[7] ? Number((m[7] + '00').slice(0, 3)) : 0;
    const [y, mo, d, h, mi, sec] = [g(1), g(2), g(3), g(4), g(5), g(6)];
    if (mo < 1 || mo > 12 || d < 1 || d > new Date(y, mo, 0).getDate() || h > 23 || mi > 59 || sec > 60) return null;
    if (!m[8]) return new Date(y, mo - 1, d, h, mi, Math.min(sec, 59), ms).getTime();
    const z = m[8].replace(':', '');
    const off = z === 'Z' ? 0 : (z[0] === '-' ? -1 : 1) * (Number(z.slice(1, 3)) * 60 + Number(z.slice(3, 5)));
    return Date.UTC(y, mo - 1, d, h, mi, Math.min(sec, 59), ms) - off * 60_000;
  }
  // Other log timestamp shapes, when the whole value is one and it carries a date (not "10:00:00" or "5").
  const t = matchTimestamp(s, new Date().getFullYear());
  return t && t.length === s.length && t.ms > 366 * 86_400_000 ? t.ms : null;
}

/** The time a `since:`/`until:` value names: -3h, 15m (both "ago"), now, today, yesterday, or a date. */
export function parseWhen(s: string, now: number): number | null {
  s = s.trim();
  const lower = s.toLowerCase();
  if (lower === 'now') return now;
  if (lower === 'today' || lower === 'yesterday') {
    const d = new Date(now);
    d.setHours(0, 0, 0, 0);
    return lower === 'today' ? d.getTime() : d.getTime() - 86_400_000;
  }
  const rel = s.startsWith('-') ? s.slice(1) : s;
  const q = NUMBER_RE.test(rel) ? null : quantity(rel);
  if (q && q.unit > 0) return now - Math.round(q.value);
  return parseAbsolute(s);
}

// ---- autocomplete

/**
 * Splits an autocomplete token into a prefix ("-", "(" or "-("…), a field name (null if none yet), the operator after
 * it (":" or a comparison) and the partial value.
 */
export function splitToken(token: string): [string, string | null, string, string] {
  let p = 0;
  while (p < token.length && (token[p] === '-' || token[p] === '(')) p++;
  const prefix = token.slice(0, p);
  const t = token.slice(p);
  const m = FIELD_OP_RE.exec(t);
  if (!m || (m[2] !== ':' && (m[1].endsWith('-') || m[1].endsWith('.')))) return [prefix, null, '', t];
  return [prefix, m[1], m[2], m[3].replace(/^"+|"+$/g, '')];
}

/** A `field:value` (or `field>=value`…) token, quoted when the value contains spaces, quotes or parentheses. */
export function fieldToken(field: string, value: string, op = ':'): string {
  return value.length > 0 && !/[\s"()]/.test(value) ? `${field}${op}${value}` : `${field}${op}"${value.replace(/"/g, '')}"`;
}

/** Comparison operators offered by autocomplete after a complete field name. */
export const OPERATORS = [':', '>=', '<=', '>', '<', '==', '!=', '?='];

export function operatorHint(op: string): string {
  return op === ':' ? 'contains' : op === '==' ? 'equals' : op === '!=' ? 'differs' : op === '?=' ? 'equals or missing' : 'compare';
}

// ---- time shortcuts

/** The time-filter shortcuts: "Now" sets To, the others set From and clear To. */
export const TIME_SHORTCUTS: [string, number][] = [
  ['Now', 0],
  ['-15m', 15 * 60_000],
  ['-1h', 3_600_000],
  ['-24h', 86_400_000],
];

/**
 * The moment time shortcuts count back from: the wall clock while following a live file (new lines are arriving
 * now), otherwise the last entry, so "-1h" on yesterday's log shows its last hour rather than nothing.
 */
export function shortcutReference(lastEntry: number | null | undefined, live: boolean, now: number): number {
  return live || lastEntry == null ? now : lastEntry;
}

export const SHORTCUT_TIP =
  'Shortcuts count back from the last entry in the file, or from the clock while following a live file. Now sets To; the others set From and clear To.';
