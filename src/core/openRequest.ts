// "Open this file at this time with this filter": the one request model behind the command line, the single-instance
// handoff, ultralogviewer://open?… links, the local HTTP API and "Copy link to this view". It only describes what to
// show; nothing in it can run a program. No Node imports: the renderer builds links with it (see openRequestFs.ts).

import { CommandLine, OpenWindow, parseDuration, parsePoint } from './timeWindow';

/** Where a request came from. A URL can come from any web page, so it is never trusted. */
export type RequestSource = 'cli' | 'url' | 'api';

/**
 * "Go to" target: a time (epoch ms), or a 1-based line in a file. `file` names the file of a merged view: a 1-based
 * position in its file list, or a file name; absent is the first file.
 */
export interface GoToTarget {
  time?: number | null;
  line?: number;
  file?: string | null;
}

export interface OpenRequest {
  /** Files, "archive.zip::member" paths, globs (* and ? in the file name) and "-" for standard input. */
  paths: string[];
  /** A format id ("json", "custom:Name"…); absent detects it. */
  format?: string | null;
  window?: OpenWindow | null;
  /** Quick-filter query. */
  filter?: string | null;
  /** Text to find (plain text, highlighted and jumped to). */
  search?: string | null;
  goto?: GoToTarget | null;
  /** Levels to show, indexed by Level; absent leaves the level toggles alone. */
  levels?: boolean[] | null;
  /** Several files open as one merged timeline (otherwise one tab each). */
  merge?: boolean;
  /** Always open a new tab, even when one over the same files is open. */
  newTab?: boolean;
  source: RequestSource;
  errors: string[];
}

export const SCHEME = 'ultralogviewer';
/** More than this many files in one request is refused (a link or glob must not open thousands of tabs). */
export const MAX_FILES = 64;
const MAX_TEXT = 4000;
const LEVEL_KEYS = ['none', 'trace', 'debug', 'info', 'warn', 'error', 'fatal'];

export const newRequest = (source: RequestSource, paths: string[] = []): OpenRequest => ({ paths, source, errors: [] });
export const isValid = (r: OpenRequest) => r.errors.length === 0;
export const wantsStdin = (r: OpenRequest) => r.paths.includes('-');

// ------------------------------------------------------------------ parsing parts

/** "error,warn" → level toggles with only those on; null (and an error) when a name is unknown. */
export function parseLevels(s: string | null | undefined, errors?: string[]): boolean[] | null {
  if (!s?.trim()) return null;
  const on = LEVEL_KEYS.map(() => false);
  const names: Record<string, number> = { trace: 1, debug: 2, info: 3, warn: 4, warning: 4, error: 5, err: 5, fatal: 6, critical: 6, none: 0, other: 0 };
  for (const raw of s.split(/[,;| ]+/).filter(Boolean)) {
    const lv = names[raw.trim().toLowerCase()];
    if (lv === undefined) {
      errors?.push(`--levels: unknown level "${raw}"`);
      return null;
    }
    on[lv] = true;
  }
  return on;
}

/** Level toggles back to "warn,error" (null when all are on). */
export function levelsText(levels: boolean[] | null | undefined): string | null {
  if (!levels || levels.every(Boolean)) return null;
  return LEVEL_KEYS.filter((_, i) => levels[i]).join(',');
}

const iso = (ms: number) => new Date(ms).toISOString();

/** Text form of a target, read back by parseGoTo: an ISO time (UTC) or "line:[file:]N". */
export function goToText(g: GoToTarget): string {
  if (g.time != null) return iso(g.time);
  return g.file ? `line:${g.file}:${g.line}` : `line:${g.line}`;
}

/**
 * A "go to" target: "line:N" / "line:FILE:N" (always a line), a bare number (a line of the first file), a time
 * ("2024-05-01 10:00:05", "10:30", "-5m"), or "FILE:N" with FILE a 1-based file position or a file name.
 */
export function parseGoTo(s: string | null | undefined, now: number): GoToTarget | null {
  s = s?.trim();
  if (!s) return null;
  const fileLine = (v: string): GoToTarget | null => {
    // the last colon: a Windows path ("C:\logs\app.log:12") keeps its drive letter
    const i = v.lastIndexOf(':');
    const lineText = i < 0 ? v : v.slice(i + 1);
    if (!/^\d+$/.test(lineText) || Number(lineText) <= 0) return null;
    const file = i < 0 ? '' : v.slice(0, i).trim();
    return file ? { line: Number(lineText), file } : { line: Number(lineText) };
  };
  if (/^line:/i.test(s)) return fileLine(s.slice(5));
  if (/^\d+$/.test(s)) return Number(s) > 0 ? { line: Number(s) } : null;
  const t = parsePoint(s, now);
  if (t !== null) return { time: t };
  return fileLine(s);
}

/** Duration text for links: "15m", "2h", "90s" (exact, so it parses back to the same value). */
export function durationText(ms: number): string {
  for (const [unit, size] of [['d', 86_400_000], ['h', 3_600_000], ['m', 60_000], ['s', 1000]] as const) if (ms > 0 && ms % size === 0) return `${ms / size}${unit}`;
  return `${ms}ms`;
}

// eslint-disable-next-line no-control-regex
const hasControl = (s: string) => /[\u0000-\u001f\u007f-\u009f\u2028\u2029]/.test(s);

/** "/var/log/x", "C:\logs\x" or "C:/logs/x", on any OS (a link built on Windows parses the same on Linux). */
export const isAbsolutePath = (p: string) => p.startsWith('/') || /^[A-Za-z]:[\\/]/.test(p);

// --------------------------------------------------------------------- validation

/**
 * Checks the request; problems go to `errors`. Every source: no control characters, at most MAX_FILES paths, a plain
 * format id. From a URL or the API also: absolute local paths only (no "-", no relative paths), and from a URL no
 * network paths, which Windows would contact on its own.
 */
export function validate(r: OpenRequest): OpenRequest {
  if (r.paths.length > MAX_FILES) r.errors.push(`Too many files (${r.paths.length}; at most ${MAX_FILES})`);
  for (const p of r.paths) {
    if (!p) r.errors.push('Empty path');
    else if (hasControl(p)) r.errors.push('A path contains control characters');
    else if (r.source !== 'cli') {
      if (p === '-') r.errors.push('Standard input can only be read from the command line');
      else if (r.source === 'url' && (p.startsWith('\\\\') || p.startsWith('//'))) r.errors.push(`Network paths are not opened from links: ${p}`);
      else if (!isAbsolutePath(p)) r.errors.push(`A full path is needed: ${p}`);
    }
  }
  for (const [name, v] of [['filter', r.filter], ['search', r.search]] as const) {
    if (v == null) continue;
    if (v.length > MAX_TEXT) r.errors.push(`--${name} is too long`);
    else if (hasControl(v)) r.errors.push(`--${name} contains control characters`);
  }
  if (r.format != null && !/^(custom:)?[\w.\- ]{1,80}$/.test(r.format)) r.errors.push(`--format: not a format id "${r.format}"`);
  if (r.goto?.file && hasControl(r.goto.file)) r.errors.push('--goto contains control characters');
  return r;
}

const LOG_EXTENSIONS = ['.log', '.txt', '.json', '.jsonl', '.ndjson', '.out', '.err', '.csv', '.gz', '.bz2', '.zip', '.trace', '.evtx', '.syslog'];

/**
 * Whether a path looks like a log file: a log extension, a rotated log ("app.log.3", "app.log.2024-05-01"), or no
 * extension at all ("syslog", "messages"). Anything else (".exe", ".docx", ".ssh/id_rsa"…) is named in a warning.
 */
export function looksLikeLog(p: string): boolean {
  const member = p.indexOf('::') > 0 ? p.slice(p.indexOf('::') + 2) : p;
  const name = (member.split(/[\\/]/).pop() ?? member).toLowerCase();
  if (name.startsWith('.')) return false; // dotfiles: .bashrc, .env, .git-credentials
  if (LOG_EXTENSIONS.some((e) => name.endsWith(e))) return true;
  if (/\.(log|txt|out|err)\.[\w.-]+$/.test(name)) return true;
  return !name.includes('.');
}

// -------------------------------------------------------------------- command line

/** The request of a parsed command line (a deep link on the command line wins: Windows starts us with it). */
export function requestFromCommandLine(c: CommandLine, now: number): OpenRequest {
  if (c.url) return requestFromUrl(c.url, now);
  const r: OpenRequest = {
    source: 'cli',
    paths: [...c.files, ...(c.stdin ? ['-'] : [])],
    window: c.window,
    filter: c.filter ?? null,
    search: c.search ?? null,
    format: c.format ?? null,
    merge: !!c.merge,
    newTab: !!c.newTab,
    errors: [...c.errors],
  };
  if (c.goto != null) {
    r.goto = parseGoTo(c.goto, now);
    if (!r.goto) r.errors.push(`--goto: cannot read "${c.goto}"`);
  }
  r.levels = parseLevels(c.levels, r.errors);
  return validate(r);
}

// ----------------------------------------------------------------------------- URL

const flag = (v: string) => ['', '1', 'true', 'yes', 'on'].includes(v.toLowerCase());

/** Applies key/value pairs (from a URL query or JSON) to the request. */
function apply(r: OpenRequest, args: [string, string][], now: number) {
  let since: number | null = null, until: number | null = null, last: number | null = null;
  for (const [k, v] of args) {
    switch (k) {
      case 'path':
      case 'file':
        r.paths.push(v);
        break;
      case 'format':
        r.format = v || null;
        break;
      case 'filter':
      case 'q':
        r.filter = v;
        break;
      case 'search':
      case 'find':
        r.search = v;
        break;
      case 'levels':
      case 'level':
        r.levels = parseLevels(v, r.errors);
        break;
      case 'merge':
        r.merge = flag(v);
        break;
      case 'newtab':
        r.newTab = flag(v);
        break;
      case 'goto':
        r.goto = parseGoTo(v, now);
        if (!r.goto) r.errors.push(`goto: cannot read "${v}"`);
        break;
      case 'since':
        since = parsePoint(v, now);
        if (since === null) r.errors.push(`since: cannot read "${v}"`);
        break;
      case 'until':
        until = parsePoint(v, now);
        if (until === null) r.errors.push(`until: cannot read "${v}"`);
        break;
      case 'last':
        last = parseDuration(v);
        if (last === null) r.errors.push(`last: cannot read "${v}"`);
        break;
      // unknown keys are ignored, so newer links still open in older versions
    }
  }
  if (since !== null || until !== null || last !== null) r.window = { since, until, last: since === null ? last : null };
}

/**
 * `ultralogviewer://open?path=/var/log/app.log&since=-1h&filter=level:error&goto=…`. Keys: path (repeat it for several
 * files), format, since, until, last, filter, search, goto, levels, merge, newtab.
 */
export function requestFromUrl(url: string, now: number): OpenRequest {
  const r = newRequest('url');
  const m = /^ultralogviewer:(?:\/\/)?open\/?(?:\?([^#]*))?(?:#.*)?$/i.exec(url.trim());
  if (!m) {
    r.errors.push('Not an ultralogviewer://open link');
    return r;
  }
  const args: [string, string][] = [];
  const d = (s: string) => decodeURIComponent(s.replace(/\+/g, ' '));
  for (const part of (m[1] ?? '').split('&').filter(Boolean)) {
    const eq = part.indexOf('=');
    try {
      args.push(eq < 0 ? [d(part).toLowerCase(), ''] : [d(part.slice(0, eq)).toLowerCase(), d(part.slice(eq + 1))]);
    } catch {
      r.errors.push('The link is not encoded correctly');
    }
  }
  apply(r, args, now);
  return validate(r);
}

/** The request as a deep link (times as UTC ISO, so the link means the same moment everywhere). */
export function requestToUrl(r: OpenRequest): string {
  const q: string[] = [];
  const add = (k: string, v: string | null | undefined) => v != null && q.push(`${k}=${encodeURIComponent(v)}`);
  for (const p of r.paths) add('path', p);
  if (r.merge && r.paths.length > 1) add('merge', '1');
  add('format', r.format);
  const w = r.window;
  if (w) {
    if (w.since != null) add('since', iso(w.since));
    if (w.until != null) add('until', iso(w.until));
    if (w.since == null && w.last != null) add('last', durationText(w.last));
  }
  add('levels', levelsText(r.levels));
  if (r.filter) add('filter', r.filter);
  if (r.search) add('search', r.search);
  if (r.goto) add('goto', goToText(r.goto));
  if (r.newTab) add('newtab', '1');
  return `${SCHEME}://open?${q.join('&')}`;
}

// ---------------------------------------------------------------------------- JSON

/**
 * The request from JSON (the HTTP API's POST /open body): {"paths": [...] or "path": "...", "format", "since"/"until"
 * (a time spec or epoch ms), "last" (a duration or ms), "filter", "search", "goto" (text, or epoch ms for a time),
 * "levels" ("error,warn" or a list), "merge", "newTab"}.
 */
export function requestFromJson(json: string, now: number, source: RequestSource = 'api'): OpenRequest {
  const r = newRequest(source);
  let v: unknown;
  try {
    v = JSON.parse(json);
  } catch (e) {
    r.errors.push(`Bad JSON: ${(e as Error).message}`);
    return r;
  }
  if (!v || typeof v !== 'object' || Array.isArray(v)) {
    r.errors.push('Expected a JSON object');
    return r;
  }
  const args: [string, string][] = [];
  for (const [name, val] of Object.entries(v as Record<string, unknown>)) {
    const key = name.toLowerCase();
    if (Array.isArray(val) && (key === 'paths' || key === 'levels')) {
      const items = val.filter((x): x is string => typeof x === 'string');
      if (key === 'paths') for (const i of items) args.push(['path', i]);
      else args.push(['levels', items.join(',')]);
    } else if (typeof val === 'string') args.push([key === 'paths' ? 'path' : key, val]);
    else if (typeof val === 'boolean') args.push([key, val ? '1' : '0']);
    else if (typeof val === 'number' && Number.isSafeInteger(val)) {
      // epoch ms for times, ms for "last"
      args.push([key, key === 'since' || key === 'until' || key === 'goto' ? iso(val) : key === 'last' ? `${val}ms` : String(val)]);
    } else if (val !== null) r.errors.push(`"${name}": unexpected value`);
  }
  apply(r, args, now);
  return validate(r);
}

export function requestToJson(r: OpenRequest): string {
  const o: Record<string, unknown> = { paths: r.paths };
  if (r.format) o.format = r.format;
  if (r.window?.since != null) o.since = iso(r.window.since);
  if (r.window?.until != null) o.until = iso(r.window.until);
  if (r.window && r.window.since == null && r.window.last != null) o.last = durationText(r.window.last);
  if (levelsText(r.levels)) o.levels = levelsText(r.levels);
  if (r.filter) o.filter = r.filter;
  if (r.search) o.search = r.search;
  if (r.goto) o.goto = goToText(r.goto);
  if (r.merge) o.merge = true;
  if (r.newTab) o.newTab = true;
  return JSON.stringify(o);
}

/** The same request (what a link or JSON round trip must keep). */
export function sameRequest(a: OpenRequest, b: OpenRequest): boolean {
  const win = (w: OpenWindow | null | undefined) => JSON.stringify([w?.since ?? null, w?.until ?? null, w?.since == null ? w?.last ?? null : null]);
  const go = (g: GoToTarget | null | undefined) => (g ? goToText(g) : '');
  return (
    JSON.stringify(a.paths) === JSON.stringify(b.paths) &&
    (a.format ?? null) === (b.format ?? null) &&
    win(a.window) === win(b.window) &&
    (a.filter ?? '') === (b.filter ?? '') &&
    (a.search ?? '') === (b.search ?? '') &&
    go(a.goto) === go(b.goto) &&
    levelsText(a.levels) === levelsText(b.levels) &&
    (!!a.merge && a.paths.length > 1) === (!!b.merge && b.paths.length > 1) &&
    !!a.newTab === !!b.newTab
  );
}

/** A ready-to-run example for "Copy API example". */
export function curlExample(port: number, token: string, file: string | null, windows: boolean): string {
  const body = requestToJson({ ...newRequest('api', [file ?? (windows ? 'C:\\logs\\app.log' : '/var/log/app.log')]), filter: 'level:error', window: { last: 3_600_000 } });
  const quoted = windows ? `"${body.replace(/"/g, '\\"')}"` : `'${body.replace(/'/g, "'\\''")}'`;
  return `curl -H "Authorization: Bearer ${token}" -X POST http://127.0.0.1:${port}/open -d ${quoted}\ncurl -H "Authorization: Bearer ${token}" http://127.0.0.1:${port}/tabs`;
}
