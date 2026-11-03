// Values picked for one field in the sidebar (Ctrl+click several loggers, or a package node), kept as one clause of the
// quick-filter query so the query box always says what is shown: `logger in (a,b)`, `logger:com.acme.*`, or
// `(logger in (a,b) or logger:com.acme.*)` when both kinds are picked. The clause is found again by its shape, so the
// rest of the query is left exactly as the user typed it.

import { readList } from './query';

const escapeRe = (s: string) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

/** One alternative of a pick clause: `field in (…)` (group 1) or `field:prefix*` (group 2). */
const alt = (f: string) => `${f}\\s+in\\s*\\(((?:"[^"]*"|[^()"])*)\\)|${f}:([^\\s()"*]+)\\*`;

function findPickClause(query: string, field: string): { index: number; length: number; text: string } | null {
  const a = alt(escapeRe(field));
  const re = new RegExp(`(^|\\s)(\\((?:${a})(?:\\s+or\\s+(?:${a}))*\\)|(?:${a}))(?=$|\\s)`, 'i');
  const m = re.exec(query);
  if (!m) return null;
  const index = m.index + m[1].length;
  return { index, length: m[2].length, text: m[2] };
}

/** The values and package prefixes (without the "*", e.g. "com.acme.") picked for a field, read back from the query. */
export function fieldPicks(query: string, field: string): { values: string[]; prefixes: string[] } {
  const values: string[] = [];
  const prefixes: string[] = [];
  const c = findPickClause(query, field);
  if (!c) return { values, prefixes };
  const re = new RegExp(alt(escapeRe(field)), 'gi');
  for (let m = re.exec(c.text); m; m = re.exec(c.text)) {
    if (m[1] !== undefined) {
      const [list] = readList(m[1] + ')', 0);
      if (list) values.push(...list);
    } else prefixes.push(m[2]);
  }
  return { values, prefixes };
}

/** `field in (a,b)`, quoting values that contain spaces, commas, quotes or parentheses. */
export function inClause(field: string, values: string[]): string {
  return `${field} in (${values.map((v) => (/[\s,()"]/.test(v) ? `"${v.replace(/"/g, '')}"` : v)).join(',')})`;
}

/** The clause for picked values and prefixes ('' when nothing is picked). */
export function pickClause(field: string, values: string[], prefixes: string[]): string {
  const parts: string[] = [];
  if (values.length) parts.push(inClause(field, values));
  for (const p of prefixes) parts.push(`${field}:${p}*`);
  return parts.length === 0 ? '' : parts.length === 1 ? parts[0] : `(${parts.join(' or ')})`;
}

/** The query with the field's pick clause replaced (or added, or removed when nothing is picked). */
export function setFieldPicks(query: string, field: string, values: string[], prefixes: string[]): string {
  const clause = pickClause(field, values, prefixes);
  const c = findPickClause(query, field);
  if (!c) return clause ? `${query.trimEnd()} ${clause}`.trim() : query;
  const before = query.slice(0, c.index).trimEnd();
  const after = query.slice(c.index + c.length).trimStart();
  return [before, clause, after].filter(Boolean).join(' ');
}

/** Package prefixes of a dotted name, outermost first: "com.acme.web.Api" gives "com.", "com.acme." and "com.acme.web.". */
export function packagePrefixes(name: string): string[] {
  const out: string[] = [];
  for (let i = name.indexOf('.'); i > 0 && i < name.length - 1; i = name.indexOf('.', i + 1)) out.push(name.slice(0, i + 1));
  return out;
}

/** A hierarchical name such as a logger or class (com.acme.Db), as opposed to a version or an IP address. */
export const isDottedName = (v: string) => /^[A-Za-z_$][\w$-]*(?:\.[\w$-]+)+$/.test(v);

/**
 * The package nodes for a field's top values: prefixes shared by at least two dotted values, without a parent that
 * covers exactly the same values as its only child, at most `max`. Each is [prefix, depth, count].
 */
export function packageTree(top: [string, number][], max = 8): [string, number, number][] {
  const dotted = top.filter(([v]) => isDottedName(v));
  const members = new Map<string, { values: number; count: number }>();
  for (const [v, n] of dotted)
    for (const p of packagePrefixes(v)) {
      const m = members.get(p) ?? { values: 0, count: 0 };
      m.values++;
      m.count += n;
      members.set(p, m);
    }
  const shared = [...members].filter(([, m]) => m.values >= 2).map(([p]) => p);
  // a parent that holds exactly the values of a single child adds nothing ("com." over "com.acme.")
  const keep = shared.filter((p) => !shared.some((c) => c !== p && c.startsWith(p) && members.get(c)!.values === members.get(p)!.values));
  keep.sort();
  const out: [string, number, number][] = [];
  for (const p of keep) {
    const depth = keep.filter((q) => q !== p && p.startsWith(q)).length;
    out.push([p, depth, members.get(p)!.count]);
  }
  return out.slice(0, max);
}
