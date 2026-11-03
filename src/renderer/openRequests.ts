// Open requests from outside, on the renderer side: the command line (and later launches handing theirs over), deep
// links (confirmed by the main process before they get here) and the local HTTP API; plus "Copy link to this view" and
// "Copy API example". The main process resolves the files; this applies the request to tabs.

import type { ApiResult, ApiTab } from '../core/localApi';
import { curlExample, OpenRequest, requestToUrl } from '../core/openRequest';
import type { OpenWindow } from '../core/timeWindow';
import type { RowData } from '../core/types';
import { engine, ulv } from './api';
import type { Tab } from './state';
import { toast } from './util';

/** What the app lends this module. */
export interface OpenRequestHost {
  tabs: () => Tab[];
  active: () => Tab | null;
  /** All tabs including split panes. */
  allTabs: () => Tab[];
  openTab: (paths: string[], opts: { format?: string; window?: OpenWindow | null }) => Promise<Tab>;
  activate: (t: Tab) => void;
  addRecent: (paths: string[]) => void;
  title: (t: Tab) => string;
  /** Sets the quick filter (validated, toasts on error) and runs the filter; resolves when done. */
  applyFilter: (t: Tab) => Promise<void>;
  validateQuery: (t: Tab, q: string) => string | null;
  search: (t: Tab, text: string, jump: boolean) => Promise<void>;
  select: (row: number) => void;
  /** Formats the format picker knows (built-in and custom ids). */
  knownFormat: (id: string) => boolean;
  focusedRow: (t: Tab) => Promise<RowData | null>;
  copy: (text: string, what: string) => void;
  /** The tab that reads standard input, if any (it cannot be linked). */
  stdinPath: () => string | null;
}

const sameWindow = (a: OpenWindow | null | undefined, b: OpenWindow | null | undefined) =>
  (a?.since ?? null) === (b?.since ?? null) && (a?.until ?? null) === (b?.until ?? null) && (a?.last ?? null) === (b?.last ?? null);

const indexed = async (h: OpenRequestHost, t: Tab) => {
  while (t.info.indexing && !t.error && h.allTabs().includes(t)) await new Promise((r) => setTimeout(r, 30));
  return h.allTabs().includes(t) && !t.error;
};

/**
 * Opens (or reuses) the tabs of a request whose paths main already resolved, and applies its filter, levels, search and
 * "go to". Problems go to `req.errors` (and a toast). Returns the last tab it showed.
 */
export async function applyOpenRequest(h: OpenRequestHost, req: OpenRequest): Promise<Tab | null> {
  if (req.errors.length) toast(req.errors.slice(0, 5).join('\n'), true);
  const files = req.paths.filter((p) => p !== '-');
  if (!files.length) return null;
  let format = req.format ?? undefined;
  if (format && !h.knownFormat(format)) {
    toast(`Unknown format "${format}": detecting it instead`, true);
    req.errors.push(`Unknown format "${format}"`);
    format = undefined;
  }
  const window = req.window && (req.window.since != null || req.window.until != null || req.window.last != null) ? req.window : null;
  const groups = req.merge && files.length > 1 ? [files] : files.map((f) => [f]);
  let last: Tab | null = null;
  for (const g of groups) {
    let tab = req.newTab ? undefined : h.tabs().find((t) => t.paths.length === g.length && t.paths.every((p, i) => p === g[i]) && sameWindow(t.window, window) && (!format || t.formatId === format));
    if (tab) h.activate(tab);
    else {
      tab = await h.openTab(g, { format, window });
      h.activate(tab);
    }
    await applyToTab(h, tab, req);
    last = tab;
  }
  h.addRecent(files);
  return last;
}

/** Applies the filter, levels, search and "go to" of a request to an open tab (waiting until it is indexed). */
export async function applyToTab(h: OpenRequestHost, tab: Tab, req: OpenRequest) {
  if (!req.levels && req.filter == null && req.search == null && !req.goto) return; // just open: no need to wait
  if (!(await indexed(h, tab))) return;
  if (h.active() !== tab) h.activate(tab);
  let refilter = false;
  if (req.levels) {
    tab.filter.levels = [...req.levels];
    refilter = true;
  }
  if (req.filter != null) {
    const err = h.validateQuery(tab, req.filter);
    if (err) {
      toast(`Quick filter: ${err}`, true);
      req.errors.push(`filter: ${err}`);
    } else {
      tab.filter.query = req.filter;
      refilter = true;
    }
  }
  if (refilter) await h.applyFilter(tab);
  if (req.search != null && h.active() === tab) await h.search(tab, req.search, !req.goto);
  if (req.goto) {
    const g = req.goto;
    let row: number;
    if (g.time != null) row = await engine.rowForTime(tab.id, g.time);
    else {
      const src = sourceOf(tab, g.file ?? null);
      if (src === null) {
        toast(`No file "${g.file}" in this tab`, true);
        req.errors.push(`goto: no file "${g.file}"`);
        return;
      }
      row = await engine.rowForLine(tab.id, src, g.line ?? 1);
    }
    if (h.active() === tab && tab.info.viewCount > 0) h.select(Math.max(0, Math.min(row, tab.info.viewCount - 1)));
  }
}

/** A "go to" file: null is the first, "2" the second, or a path or file name. */
function sourceOf(tab: Tab, file: string | null): number | null {
  const src = tab.info.sources;
  if (file == null) return 0;
  if (/^\d+$/.test(file)) return Number(file) >= 1 && Number(file) <= src.length ? Number(file) - 1 : null;
  const i = src.findIndex((s) => s.path === file || s.displayName.toLowerCase() === file.toLowerCase());
  return i < 0 ? null : i;
}

// ---------------------------------------------------------------- copy link

/** The request that reopens a tab as it is: its files, window, filter, levels, search and the selected entry. */
export function viewRequest(tab: Tab, row: RowData | null): OpenRequest {
  const p = tab.search.pattern;
  const req: OpenRequest = {
    source: 'cli',
    errors: [],
    paths: [...tab.paths],
    merge: tab.paths.length > 1,
    format: tab.formatId || null,
    window: tab.window ?? null,
    filter: tab.filter.query?.trim() ? tab.filter.query : null,
    levels: tab.filter.levels.every(Boolean) ? null : [...tab.filter.levels],
    search: p.text && !p.regex ? p.text : null,
  };
  if (row) req.goto = row.time != null ? { time: row.time } : tab.info.sources.length > 1 ? { line: row.line, file: String(row.source + 1) } : { line: row.line };
  return req;
}

export async function copyViewLink(h: OpenRequestHost, row?: RowData | null) {
  const tab = h.active();
  if (!tab) return toast('Open a file first', true);
  if (tab.paths.includes(h.stdinPath() ?? '\0')) return toast('Standard input cannot be linked', true);
  const url = requestToUrl(viewRequest(tab, row ?? (await h.focusedRow(tab))));
  h.copy(url, tab.filter.rules.some((r) => r.enabled) ? "Link (without the sidebar's filter rules)" : 'Link to this view');
}

export async function copyApiExample(h: OpenRequestHost) {
  const st = await ulv().apiStatus();
  if (!st.port || !st.token) return toast('Turn on File › Local API › Enable local HTTP API first', true);
  h.copy(curlExample(st.port, st.token, h.active()?.paths[0] ?? null, ulv().platform === 'win32'), 'API example (it contains your token)');
}

// -------------------------------------------------------------- API calls

/** Answers the main process's API and open-request calls. */
export function serveApiCalls(h: OpenRequestHost): Promise<unknown> {
  const byId = (id: number) => h.allTabs().find((t) => t.id === id);
  return ulv().onApiCall(async (id, method, args) => {
    let result: unknown;
    try {
      result = await call(method, args);
    } catch (e) {
      result = { status: 500, error: (e as Error).message } satisfies ApiResult;
    }
    ulv().apiReply(id, result);
  });
  async function call(method: string, args: unknown[]): Promise<unknown> {
    switch (method) {
      case 'open': {
        const req = args[0] as OpenRequest;
        const tab = await applyOpenRequest(h, req);
        return tab ? ({ status: 200, tab: tab.id } satisfies ApiResult) : ({ status: 404, error: req.errors.join('; ') || 'Nothing was opened' } satisfies ApiResult);
      }
      case 'tabs': {
        const active = h.active();
        return h.allTabs().map((t): ApiTab => ({ id: t.id, title: t.splitOf ? `${h.title(t)} (split)` : h.title(t), paths: t.paths, total: t.info.total, shown: t.info.viewCount, filter: t.filter.query ?? '', ready: !t.info.indexing, active: t === active }));
      }
      case 'update': {
        const t = byId(args[0] as number);
        const req = args[1] as OpenRequest;
        if (!t) return { status: 404, error: `No tab ${args[0]}` } satisfies ApiResult;
        await applyToTab(h, t, req);
        return req.errors.length ? ({ status: 422, error: req.errors.join('; '), tab: t.id } satisfies ApiResult) : ({ status: 200, tab: t.id } satisfies ApiResult);
      }
      case 'rows': {
        const t = byId(args[0] as number);
        if (!t || !(await indexed(h, t))) return null;
        const [from, count] = [args[1] as number, args[2] as number];
        const n = Math.min(count, t.info.viewCount - from);
        return n > 0 ? engine.rows(t.id, from, n) : [];
      }
    }
    throw new Error(`Unknown call ${method}`);
  }
}
