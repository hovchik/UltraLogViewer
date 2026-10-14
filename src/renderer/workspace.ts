// Saved workspaces (.ulvw, JSON): the open tabs with their files, format, filters, search, bookmarks and
// column settings, plus the highlight rules. Pure functions (no DOM, no Node) so they can be unit tested;
// app.ts does the file IO and opens the tabs.

import type { FilterSpec, TextPattern } from '../core/types';

export interface WorkspaceBookmark {
  path: string;
  line: number;
  note: string;
}

export interface WorkspaceTab {
  paths: string[];
  /** The same files relative to the workspace file's folder (used when the absolute path is gone). */
  relPaths: string[];
  /** Format id ("" = auto). */
  format: string;
  encoding: string;
  filter: FilterSpec;
  search: TextPattern;
  bookmarks: WorkspaceBookmark[];
  extraColumns: string[];
  columnOrder: string[];
  hiddenColumns: string[];
}

export interface Workspace<H = unknown> {
  version: 1;
  active: number;
  highlights: H[];
  tabs: WorkspaceTab[];
}

/** What app.ts knows about an open tab. */
export interface WorkspaceTabInput {
  paths: string[];
  formatId: string;
  encoding?: string;
  filter: FilterSpec;
  search: TextPattern;
  bookmarks: WorkspaceBookmark[];
  extraColumns?: string[];
  columnOrder?: string[];
  hiddenColumns?: string[];
}

// ------------------------------------------------------------------ paths

const isWinPath = (p: string) => /^[A-Za-z]:[\\/]/.test(p) || p.startsWith('\\\\');
export const isAbsolutePath = (p: string) => p.startsWith('/') || isWinPath(p);
const sepOf = (p: string) => (isWinPath(p) ? '\\' : '/');
/** [root, segments]: root is '' for POSIX paths, 'C:' for drive paths, '\\\\server' style for UNC. */
function split(p: string): [string, string[]] {
  const segs = p.split(/[\\/]+/);
  const root = p.startsWith('\\\\') ? '\\\\' + segs[1] : segs[0];
  return [root, segs.slice(p.startsWith('\\\\') ? 2 : 1).filter((x) => x !== '' && x !== '.')];
}

export function dirName(p: string): string {
  const i = Math.max(p.lastIndexOf('/'), p.lastIndexOf('\\'));
  if (i < 0) return '.';
  if (i === 0) return p[0];
  return /^[A-Za-z]:$/.test(p.slice(0, i)) ? p.slice(0, i + 1) : p.slice(0, i);
}

export function baseName(p: string): string {
  return p.split(/[\\/]/).pop() ?? p;
}

/** `to` relative to the absolute folder `fromDir` ('/'-separated; `to` itself when they share no root). */
export function relativePath(fromDir: string, to: string): string {
  const win = isWinPath(fromDir) || isWinPath(to);
  const norm = (s: string) => (win ? s.toLowerCase() : s);
  const [ra, a] = split(fromDir);
  const [rb, b] = split(to);
  if (norm(ra) !== norm(rb)) return to;
  let i = 0;
  while (i < a.length && i < b.length && norm(a[i]) === norm(b[i])) i++;
  return [...a.slice(i).map(() => '..'), ...b.slice(i)].join('/') || '.';
}

/** Resolves a relative path against an absolute folder (absolute paths are returned unchanged). */
export function resolvePath(dir: string, rel: string): string {
  if (isAbsolutePath(rel)) return rel;
  const sep = sepOf(dir);
  const [root, stack] = split(dir);
  for (const x of rel.split(/[\\/]+/)) {
    if (x === '.' || x === '') continue;
    if (x === '..') stack.pop();
    else stack.push(x);
  }
  return root + sep + stack.join(sep);
}

// ------------------------------------------------------------------ serialise

/** The workspace JSON for the open tabs (bookmark keys and the live-tail "clear" are left out of the filter). */
export function serializeWorkspace<H>(tabs: WorkspaceTabInput[], active: number, highlights: H[], workspaceFile: string): Workspace<H> {
  const dir = dirName(workspaceFile);
  return {
    version: 1,
    active: Math.max(0, Math.min(tabs.length - 1, active)),
    highlights: highlights.map((h) => ({ ...h })),
    tabs: tabs.map((t) => {
      const filter: FilterSpec = { ...t.filter, levels: [...t.filter.levels], rules: t.filter.rules.map((r) => ({ ...r })) };
      delete filter.bookmarks;
      delete filter.clearedBefore;
      return {
        paths: [...t.paths],
        relPaths: t.paths.map((p) => relativePath(dir, p)),
        format: t.formatId ?? '',
        encoding: t.encoding ?? 'auto',
        filter,
        search: { text: t.search.text, regex: t.search.regex, caseSensitive: t.search.caseSensitive, wholeWord: t.search.wholeWord },
        bookmarks: t.bookmarks.map((b) => ({ path: b.path, line: b.line, note: b.note ?? '' })),
        extraColumns: [...(t.extraColumns ?? [])],
        columnOrder: [...(t.columnOrder ?? [])],
        hiddenColumns: [...(t.hiddenColumns ?? [])],
      };
    }),
  };
}

const strs = (v: unknown): string[] => (Array.isArray(v) ? v.filter((x): x is string => typeof x === 'string') : []);

/** Parses and normalises workspace JSON; throws on anything that is not a workspace. */
export function parseWorkspace<H = unknown>(text: string): Workspace<H> {
  const raw = JSON.parse(text) as Record<string, unknown>;
  if (!raw || typeof raw !== 'object' || raw.version !== 1 || !Array.isArray(raw.tabs)) throw new Error('Not an UltraLogViewer workspace');
  const tabs: WorkspaceTab[] = (raw.tabs as Record<string, unknown>[]).map((t) => {
    const f = (t.filter ?? {}) as Partial<FilterSpec>;
    const s = (t.search ?? {}) as Partial<TextPattern>;
    return {
      paths: strs(t.paths),
      relPaths: strs(t.relPaths),
      format: typeof t.format === 'string' ? t.format : '',
      encoding: typeof t.encoding === 'string' ? t.encoding : 'auto',
      filter: {
        ...f,
        levels: Array.isArray(f.levels) && f.levels.length === 7 ? f.levels.map(Boolean) : [true, true, true, true, true, true, true],
        rules: Array.isArray(f.rules) ? f.rules : [],
        bookmarks: undefined,
        clearedBefore: undefined,
      },
      search: { text: String(s.text ?? ''), regex: !!s.regex, caseSensitive: !!s.caseSensitive, wholeWord: !!s.wholeWord },
      bookmarks: (Array.isArray(t.bookmarks) ? t.bookmarks : [])
        .filter((b): b is WorkspaceBookmark => !!b && typeof b.path === 'string' && typeof b.line === 'number')
        .map((b) => ({ path: b.path, line: b.line, note: typeof b.note === 'string' ? b.note : '' })),
      extraColumns: strs(t.extraColumns),
      columnOrder: strs(t.columnOrder),
      hiddenColumns: strs(t.hiddenColumns),
    };
  });
  return { version: 1, active: typeof raw.active === 'number' ? raw.active : 0, highlights: Array.isArray(raw.highlights) ? (raw.highlights as H[]) : [], tabs };
}

export interface ResolvedWorkspace<H> {
  /** Tabs to open, `paths` replaced by the files that exist. */
  tabs: WorkspaceTab[];
  /** Index into `tabs` to activate. */
  active: number;
  /** Files that exist under neither their absolute nor their relative path. */
  missing: number;
  highlights: H[];
}

/** Picks each file's absolute path if it exists, else its relative path resolved against the workspace folder. */
export function resolveWorkspace<H>(ws: Workspace<H>, workspaceFile: string, exists: (p: string) => boolean): ResolvedWorkspace<H> {
  const dir = dirName(workspaceFile);
  const tabs: WorkspaceTab[] = [];
  let missing = 0;
  let active = 0;
  ws.tabs.forEach((t, i) => {
    const paths: string[] = [];
    // bookmarks follow their file to its new location
    const moved = new Map<string, string>();
    t.paths.forEach((p, k) => {
      const rel = t.relPaths[k];
      const found = exists(p) ? p : rel && exists(resolvePath(dir, rel)) ? resolvePath(dir, rel) : null;
      if (found === null) return missing++;
      paths.push(found);
      moved.set(p, found);
    });
    if (i === ws.active) active = tabs.length;
    if (!paths.length) return;
    tabs.push({ ...t, paths, bookmarks: t.bookmarks.map((b) => ({ ...b, path: moved.get(b.path) ?? b.path })) });
  });
  return { tabs, active: Math.max(0, Math.min(tabs.length - 1, active)), missing, highlights: ws.highlights };
}
