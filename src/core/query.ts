import { TextPattern } from './types';

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
 *   field:value     match inside a parsed column (e.g. logger:Db, level:error)
 *   /regex/
 */
export interface QueryAlt extends TextPattern {
  /** Restrict to a parsed column, e.g. "logger". */
  field: string | null;
}

export interface QueryTerm {
  negate: boolean;
  any: QueryAlt[]; // OR-ed alternatives
}

export function parseQuery(q: string, caseSensitive = false): QueryTerm[] {
  const tokens: string[] = [];
  const re = /"([^"]*)"|\/((?:\\.|[^/])+)\/|(\S+)/g;
  let m: RegExpExecArray | null;
  while ((m = re.exec(q))) tokens.push(m[1] !== undefined ? `"${m[1]}` : m[2] !== undefined ? `/${m[2]}` : m[3]);
  const terms: QueryTerm[] = [];
  let negateNext = false;
  let orNext = false;
  for (const raw of tokens) {
    if (raw === 'OR' || raw === '|') {
      orNext = terms.length > 0;
      continue;
    }
    if (raw === 'AND') continue;
    if (raw === 'NOT') {
      negateNext = true;
      continue;
    }
    let tok = raw;
    let negate = negateNext;
    negateNext = false;
    if (tok.startsWith('-') && tok.length > 1) {
      negate = true;
      tok = tok.slice(1);
    }
    let field: string | null = null;
    const fm = /^([A-Za-z_][\w-]*):(.+)$/.exec(tok);
    if (fm && !tok.startsWith('"') && !tok.startsWith('/')) {
      field = fm[1].toLowerCase();
      tok = fm[2];
    }
    let pat: QueryAlt;
    if (tok.startsWith('/')) pat = { field, text: tok.slice(1), regex: true, caseSensitive, wholeWord: false };
    else if (tok.startsWith('"')) pat = { field, text: tok.slice(1), regex: false, caseSensitive, wholeWord: false };
    else pat = { field, text: tok, regex: false, caseSensitive, wholeWord: false };
    if (orNext && !negate) {
      terms[terms.length - 1].any.push(pat);
    } else {
      terms.push({ negate, any: [pat] });
    }
    orNext = false;
  }
  return terms;
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
