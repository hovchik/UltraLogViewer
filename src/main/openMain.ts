// Open requests in the main process: the startup command line, later launches (single instance), ultralogviewer://
// links and the local HTTP API. Main resolves the files and asks before a link opens anything; the renderer applies
// the request to tabs (see renderer/openRequests.ts) and answers API calls over IPC.

import { app, BrowserWindow, dialog, ipcMain } from 'electron';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { DEFAULT_API_PORT, LocalApi, newApiToken, startLocalApi } from '../core/localApi';
import { looksLikeLog, OpenRequest, requestFromCommandLine, SCHEME } from '../core/openRequest';
import { expandPaths } from '../core/openRequestFs';
import { parseCommandLine } from '../core/timeWindow';

export interface LocalApiSettings {
  enabled?: boolean;
  port?: number;
  token?: string;
}

interface Deps {
  win: () => BrowserWindow | null;
  readSettings: () => Record<string, unknown>;
  writeSettings: (s: Record<string, unknown>) => void;
  buildMenu: () => void;
  isFile: (p: string) => boolean;
}

let deps: Deps;
let api: LocalApi | null = null;
let apiError = '';
let nextCall = 1;
const calls = new Map<number, (v: unknown) => void>();
let rendererReady: () => void;
let isReady = false;
let ready = new Promise<void>((r) => (rendererReady = r));

/** Calls the renderer (once it is listening) and waits for its answer. */
function callRenderer(method: string, ...args: unknown[]): Promise<unknown> {
  return ready.then(
    () =>
      new Promise((resolve, reject) => {
        const id = nextCall++;
        const timer = setTimeout(() => {
          calls.delete(id);
          reject(new Error('The window did not answer'));
        }, 120_000);
        calls.set(id, (v) => {
          clearTimeout(timer);
          resolve(v);
        });
        const w = deps.win();
        if (!w) {
          calls.delete(id);
          clearTimeout(timer);
          return reject(new Error('No window'));
        }
        w.webContents.send('api-call', id, method, args);
      }),
  );
}

/** The switch that carries a launch's raw arguments to the running instance. */
export const HANDOFF_SWITCH = 'ulv-handoff';

export const encodeHandoff = (argv: string[], cwd: string) => Buffer.from(JSON.stringify({ argv, cwd }), 'utf-8').toString('base64url');

/** A later launch's raw arguments and folder, from the switch it added; null when it has none. */
export function decodeHandoff(argv: string[]): { argv: string[]; cwd: string } | null {
  const prefix = `--${HANDOFF_SWITCH}=`;
  const a = argv.find((x) => x.startsWith(prefix));
  if (!a) return null;
  try {
    const v = JSON.parse(Buffer.from(a.slice(prefix.length), 'base64url').toString('utf-8'));
    if (Array.isArray(v?.argv) && v.argv.every((x: unknown) => typeof x === 'string') && typeof v.cwd === 'string') return { argv: v.argv, cwd: v.cwd };
  } catch {
    // not ours
  }
  return null;
}

const isDir = (p: string) => {
  try {
    return fs.statSync(p).isDirectory();
  } catch {
    return false;
  }
};

/**
 * Command-line arguments as a request with resolved files. Folders (Electron's own "." in development) are dropped. An
 * invalid request keeps only its errors, so nothing opens.
 */
export function requestFromArgv(args: string[], cwd: string): OpenRequest {
  const now = Date.now();
  const c = parseCommandLine(args, now, (a) => deps.isFile(a));
  const req = requestFromCommandLine(c, now);
  if (req.source === 'cli') req.paths = req.paths.filter((p) => p === '-' || !isDir(path.resolve(cwd, p)));
  if (req.errors.length) {
    req.paths = [];
    return req;
  }
  req.paths = expandPaths(req, cwd);
  return req;
}

/** A link can come from any web page: it opens files only after the user saw which ones. */
async function confirmLink(files: string[]): Promise<boolean> {
  const log = process.env.ULV_LINK_CONFIRM_LOG;
  if (log) {
    // UI tests: record what would be asked, and answer yes
    fs.appendFileSync(log, files.join('\n') + '\n');
    return true;
  }
  const odd = files.filter((f) => !looksLikeLog(f));
  const opts = {
    type: 'warning' as const,
    buttons: ['Open', 'Cancel'],
    defaultId: 1,
    cancelId: 1,
    title: 'Open files from a link?',
    message: 'A link asks to open:',
    detail:
      files.slice(0, 12).join('\n') +
      (files.length > 12 ? `\n… and ${files.length - 12} more` : '') +
      (odd.length ? `\n\nThese do not look like log files:\n${odd.slice(0, 6).join('\n')}` : '') +
      '\n\nOpen them only if you trust where the link came from.',
  };
  const w = deps.win();
  const { response } = w ? await dialog.showMessageBox(w, opts) : await dialog.showMessageBox(opts);
  return response === 0;
}

/** Confirms a link's files, then hands the request to the renderer. */
async function deliver(req: OpenRequest): Promise<void> {
  const files = req.paths.filter((p) => p !== '-');
  if (req.source === 'url' && files.length && !(await confirmLink(files))) return;
  if (!files.length && !req.errors.length) return;
  await callRenderer('open', req).catch(() => {});
}

/** A later launch's arguments (single instance) or a clicked link. */
export function handleArgv(args: string[], cwd: string) {
  void deliver(requestFromArgv(args, cwd));
}

export function handleUrl(url: string) {
  void deliver(requestFromArgv([url], process.cwd()));
}

// ------------------------------------------------------------------ HTTP API

const apiSettings = (): LocalApiSettings => (deps.readSettings().localApi as LocalApiSettings) ?? {};

async function configureApi(): Promise<{ port: number | null; error: string }> {
  await api?.close();
  api = null;
  apiError = '';
  const s = apiSettings();
  if (s.enabled && s.token && s.token.length >= 32) {
    try {
      api = await startLocalApi(
        {
          open: async (r) => {
            r.paths = expandPaths(r, '/');
            if (!r.paths.length) return { status: 404, error: r.errors.join('; ') || 'No files' };
            const w = deps.win();
            if (w) {
              if (w.isMinimized()) w.restore();
              w.focus();
            }
            return (await callRenderer('open', r)) as { status: number; error?: string; tab?: number };
          },
          tabs: async () => (await callRenderer('tabs')) as never,
          update: async (id, r) => (await callRenderer('update', id, r)) as never,
          rows: async (id, from, count) => (await callRenderer('rows', id, from, count)) as never,
        },
        s.token,
        typeof s.port === 'number' && s.port >= 0 && s.port < 65536 ? s.port : DEFAULT_API_PORT,
      );
    } catch (e) {
      apiError = (e as Error).message;
    }
  }
  return { port: api?.port ?? null, error: apiError };
}

/** Changes the API settings (main writes them, so the token never has to come from the page). */
function updateApiSettings(change: (s: LocalApiSettings) => void): LocalApiSettings {
  const all = deps.readSettings();
  const s: LocalApiSettings = { port: DEFAULT_API_PORT, ...((all.localApi as LocalApiSettings) ?? {}) };
  change(s);
  if (!s.token || s.token.length < 32) s.token = newApiToken();
  all.localApi = s;
  deps.writeSettings(all);
  return s;
}

export function registerOpenRequests(d: Deps) {
  deps = d;
  ipcMain.handle('api:ready', () => {
    isReady = true;
    rendererReady();
  });
  ipcMain.on('api:reply', (_e, id: number, result: unknown) => {
    calls.get(id)?.(result);
    calls.delete(id);
  });
  ipcMain.handle('api:configure', async (_e, enabled: boolean) => {
    const settings = updateApiSettings((s) => (s.enabled = !!enabled));
    const r = await configureApi();
    deps.buildMenu();
    return { settings, ...r };
  });
  ipcMain.handle('api:new-token', async () => {
    const settings = updateApiSettings((s) => (s.token = newApiToken()));
    const r = await configureApi();
    return { settings, ...r };
  });
  ipcMain.handle('api:status', () => ({ port: api?.port ?? null, token: api ? apiSettings().token : null, error: apiError }));
  // macOS hands clicked links over as an event (Windows and Linux on the command line)
  app.on('open-url', (e, url) => {
    e.preventDefault();
    handleUrl(url);
  });
  // A packaged build registers the scheme (the Windows installer of the .NET app does it for that app).
  if (app.isPackaged) app.setAsDefaultProtocolClient(SCHEME);
}

/** Starts the API when it was left on, once the app is ready. */
export function startApiFromSettings() {
  if (apiSettings().enabled) void configureApi();
}

/** The window was reloaded or recreated: wait for its renderer again. */
export function rendererGone() {
  if (!isReady) return; // still loading: calls already wait for it
  isReady = false;
  ready = new Promise<void>((r) => (rendererReady = r));
}

export async function stopApi() {
  await api?.close();
  api = null;
}

/** The startup request (files already resolved); a link on the command line is confirmed first. */
export async function startupRequest(args: string[], stdinPath: string | null, opened: string[]): Promise<OpenRequest | null> {
  const req = requestFromArgv(args, process.cwd());
  req.paths = [...req.paths.map((p) => (p === '-' ? stdinPath : p)).filter((p): p is string => !!p), ...opened];
  if (req.source === 'url' && req.paths.length && !(await confirmLink(req.paths))) return null;
  return req.paths.length || req.errors.length ? req : null;
}
