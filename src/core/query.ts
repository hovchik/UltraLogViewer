import { FilterRule, TextPattern } from './types';

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

/**
 * Quick-filter mini language:
 *   words           all must appear (AND)
 *   a OR b          either
 *   -word / NOT w   must not appear
 *   "exact phrase"
 *   field:value     match inside a parsed column (e.g. logger:Db, level:error, http.status:500)
 *   field:"a b"     a value with spaces (also -field:"a b")
 *   /regex/         also -/regex/ and field:/regex/
 */
export interface QueryAlt extends TextPattern {
  /** Restrict to a parsed column, e.g. "logger". */
  field: string | null;
}

export interface QueryTerm {
  negate: boolean;
  any: QueryAlt[]; // OR-ed alternatives
}

// An unclosed quote is allowed (the user is still typing).
const TOKEN_RE = /(?<neg>-?)(?<field>[A-Za-z_][\w.-]*):"(?<fq>[^"]*)"?|"(?<q>[^"]*)"?|\/(?<re>(?:\\.|[^/])+)\/|\S+/g;
const FIELD_RE = /^([A-Za-z_][\w.-]*):(.+)$/;
const BARE_FIELD_RE = /^[A-Za-z_][\w.-]*:$/;

export function parseQuery(q: string, caseSensitive = false): QueryTerm[] {
  const terms: QueryTerm[] = [];
  let negateNext = false;
  let orNext = false;
  for (const m of q.matchAll(TOKEN_RE)) {
    const raw = m[0];
    const g = m.groups ?? {};
    if (raw === 'OR' || raw === '|') {
      orNext = terms.length > 0;
      continue;
    }
    if (raw === 'AND') continue;
    if (raw === 'NOT') {
      negateNext = true;
      continue;
    }
    let negate = negateNext;
    negateNext = false;
    let field: string | null = null;
    let text: string;
    let regex = false;
    if (g.fq !== undefined) {
      negate ||= g.neg === '-';
      field = g.field.toLowerCase();
      text = g.fq;
    } else if (g.q !== undefined) {
      text = g.q;
    } else if (g.re !== undefined) {
      text = g.re;
      regex = true;
    } else {
      let tok = raw;
      if (tok.length > 1 && tok.startsWith('-')) {
        negate = true;
        tok = tok.slice(1);
      }
      const fm = FIELD_RE.exec(tok);
      if (fm) {
        field = fm[1].toLowerCase();
        tok = fm[2];
      } else if (BARE_FIELD_RE.test(tok)) {
        continue; // a field name still being typed ("logger:") filters nothing yet
      }
      if (tok.length > 1 && tok.startsWith('/') && tok.endsWith('/')) {
        text = tok.slice(1, -1);
        regex = true;
      } else text = tok;
    }
    if (!text) continue;
    const pat: QueryAlt = { field, text, regex, caseSensitive, wholeWord: false };
    if (orNext && !negate) {
      terms[terms.length - 1].any.push(pat);
    } else {
      terms.push({ negate, any: [pat] });
    }
    orNext = false;
  }
  return terms;
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

/** Splits an autocomplete token into an optional "-" prefix, a field name (null if none yet) and the partial value. */
export function splitToken(token: string): [string, string | null, string] {
  const prefix = token.startsWith('-') ? '-' : '';
  const t = token.slice(prefix.length);
  const colon = t.indexOf(':');
  if (colon <= 0 || !FIELD_RE.test(t.slice(0, colon) + ':x')) return [prefix, null, t];
  return [prefix, t.slice(0, colon), t.slice(colon + 1).replace(/^"+|"+$/g, '')];
}

/** A `field:value` token, quoted when the value contains spaces or quotes. */
export function fieldToken(field: string, value: string): string {
  return value.length > 0 && !/[\s"]/.test(value) ? `${field}:${value}` : `${field}:"${value.replace(/"/g, '')}"`;
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

/** `field(name)` returns a parsed column value (lower-case name). */
export type EntryPredicate = (text: string, field: (name: string) => string) => boolean;

export function compileQuery(q: string, caseSensitive = false): EntryPredicate {
  const terms = parseQuery(q, caseSensitive).map((t) => ({ negate: t.negate, alts: t.any.map((a) => ({ field: a.field, m: compileMatcher(a) })) }));
  if (!terms.length) return () => true;
  return (text, getField) => {
    for (const t of terms) {
      const hit = t.alts.some(({ field, m }) => (field ? m(getField(field)) : m(text)));
      if (hit === t.negate) return false;
    }
    return true;
  };
}
