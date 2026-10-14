// Message templates and repeat keys: "Payment 42 failed after 30000 ms" and "Payment 7 failed after 12 ms" share
// the template "Payment <num> failed after <num> ms". Used by the patterns view, pattern filter rules and
// "group repeats".

import { matchTimestamp } from './timestamp';
import { tokenize, TokenKind } from './tokenizer';
import type { Level } from './types';

export type RepeatMode = 'off' | 'exact' | 'pattern';

/** Placeholder per token kind; kinds not listed (keys, paths, exceptions, methods, keywords) are kept as written. */
const PLACEHOLDERS: Partial<Record<TokenKind, string>> = {
  number: '<num>',
  duration: '<num>',
  guid: '<guid>',
  ip: '<ip>',
  date: '<time>',
  string: '<str>',
  url: '<url>',
};

const HEX_RE = /\b(?=[0-9a-fA-F]*\d)[0-9a-fA-F]{8,}\b/g;
const LEAD_SEP = /^[ \t\-:|]+/;
const MAX_TEMPLATE_INPUT = 500;

const firstLineOf = (s: string) => {
  const i = s.indexOf('\n');
  return i < 0 ? s : s.slice(0, i);
};

/** The message of an entry: the parsed `message` field, else the text without its leading timestamp. */
export function messageOf(text: string, fields: Record<string, string> | null | undefined): string {
  const m = fields?.message;
  if (m) return m;
  const ts = matchTimestamp(text);
  if (!ts) return text;
  return text.slice(ts.length).replace(LEAD_SEP, '');
}

/** The template of a message: first line, variable parts (numbers, ids, times, strings, urls, hex ids) replaced. */
export function template(message: string): string {
  let s = firstLineOf(message);
  if (s.length > MAX_TEMPLATE_INPUT) s = s.slice(0, MAX_TEMPLATE_INPUT);
  let out = '';
  let at = 0;
  for (const tk of tokenize(s)) {
    const ph = PLACEHOLDERS[tk.kind];
    if (!ph) continue;
    out += s.slice(at, tk.start) + ph;
    at = tk.start + tk.length;
  }
  out += s.slice(at);
  return out.replace(HEX_RE, '<hex>').replace(/\s+/g, ' ').trim();
}

/** The key consecutive entries must share to be grouped as repeats. */
export function repeatKey(level: Level, fields: Record<string, string>, text: string, mode: 'exact' | 'pattern'): string {
  const msg = messageOf(text, fields);
  if (mode === 'exact') return `${level}|${fields.logger ?? ''}|${msg}`;
  return `${level}|${template(msg)}`;
}

/** 64-bit FNV-1a over the UTF-16 code units of a string, as [high, low] 32-bit halves. */
export function fnv1a64(s: string): [number, number] {
  let hi = 0xcbf29ce4;
  let lo = 0x84222325;
  for (let i = 0; i < s.length; i++) {
    lo = (lo ^ s.charCodeAt(i)) >>> 0;
    // (hi, lo) *= 0x100000001b3 = 2^40 + 0x1b3
    const l = lo * 0x1b3;
    const carry = Math.floor(l / 0x100000000);
    hi = (hi * 0x1b3 + carry + ((lo << 8) >>> 0)) >>> 0;
    lo = l >>> 0;
  }
  return [hi, lo];
}
