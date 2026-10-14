import { app, BrowserWindow, dialog, ipcMain, Menu, nativeTheme, shell } from 'electron';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { Worker } from 'node:worker_threads';

let win: BrowserWindow | null = null;
let worker: Worker;
let nextCall = 1;
const pending = new Map<number, { resolve: (v: unknown) => void; reject: (e: Error) => void }>();
const pendingOpen: string[] = [];

// ------------------------------------------------------------------ settings

const settingsFile = () => path.join(app.getPath('userData'), 'settings.json');

function readSettings(): Record<string, unknown> {
  try {
    return JSON.parse(fs.readFileSync(settingsFile(), 'utf-8'));
  } catch {
    return {};
  }
}

function writeSettings(s: Record<string, unknown>) {
  fs.mkdirSync(path.dirname(settingsFile()), { recursive: true });
  const tmp = settingsFile() + '.tmp';
  fs.writeFileSync(tmp, JSON.stringify(s, null, 2));
  fs.renameSync(tmp, settingsFile());
}

// -------------------------------------------------------------------- engine

function startWorker() {
  worker = new Worker(path.join(__dirname, 'engineWorker.js'));
  worker.on('message', (msg: { id?: number; result?: unknown; error?: string; event?: unknown }) => {
    if (msg.event) {
      win?.webContents.send('engine-event', msg.event);
      return;
    }
    const p = pending.get(msg.id!);
    if (!p) return;
    pending.delete(msg.id!);
    if (msg.error !== undefined) p.reject(new Error(msg.error));
    else p.resolve(msg.result);
  });
  worker.on('error', (e) => console.error('engine worker error', e));
  worker.on('exit', (code) => {
    for (const p of pending.values()) p.reject(new Error('engine stopped'));
    pending.clear();
    if (code !== 0 && !quitting) startWorker();
  });
}

function callEngine(method: string, args: unknown[]): Promise<unknown> {
  const id = nextCall++;
  return new Promise((resolve, reject) => {
    pending.set(id, { resolve, reject });
    worker.postMessage({ id, method, args });
  });
}

// ---------------------------------------------------------------------- menu

const send = (cmd: string, ...args: unknown[]) => win?.webContents.send('menu', cmd, ...args);

/** The settings the View menu shows as check / radio items. */
function viewMenuState(s: Record<string, unknown>) {
  return {
    tintRows: s.tintRows === true,
    tokenColors: s.tokenColors !== false,
    comfortable: s.density === 'comfortable',
    wrap: s.wrap === true,
    dockRight: s.detailDock === 'right',
    timeMode: typeof s.timeMode === 'string' ? s.timeMode : 'original',
    timeUtc: s.timeUtc === true,
    gaps: (typeof s.gapMs === 'number' ? s.gapMs : 10000) > 0,
  };
}
let menuState = '';

function buildMenu() {
  const settings = readSettings();
  const recent = ((settings.recent as string[]) ?? []).slice(0, 10);
  const v = viewMenuState(settings);
  menuState = JSON.stringify(v);
  const timeModes: [string, string][] = [
    ['original', 'Time as written'],
    ['full', 'Full date and time'],
    ['time', 'Time of day'],
    ['relative', 'Relative to selected row'],
    ['delta', 'Delta from previous row'],
  ];
  const isMac = process.platform === 'darwin';
  const template: Electron.MenuItemConstructorOptions[] = [
    ...(isMac ? [{ role: 'appMenu' as const }] : []),
    {
      label: 'File',
      submenu: [
        { label: 'Open…', accelerator: 'CmdOrCtrl+O', click: () => send('open') },
        { label: 'Open and merge files…', accelerator: 'CmdOrCtrl+Shift+O', click: () => send('open-merge') },
        {
          label: 'Open recent',
          submenu: recent.length ? recent.map((p) => ({ label: p, click: () => send('open-path', p) })) : [{ label: 'No recent files', enabled: false }],
        },
        { type: 'separator' },
        { label: 'Export view…', accelerator: 'CmdOrCtrl+E', click: () => send('export') },
        { label: 'Close tab', accelerator: 'CmdOrCtrl+W', click: () => send('close-tab') },
        { type: 'separator' },
        isMac ? { role: 'close' } : { role: 'quit' },
      ],
    },
    {
      label: 'Edit',
      submenu: [
        { role: 'copy' },
        { role: 'selectAll' },
        { type: 'separator' },
        { label: 'Find…', accelerator: 'CmdOrCtrl+F', click: () => send('find') },
        { label: 'Quick filter', accelerator: 'CmdOrCtrl+L', click: () => send('quick-filter') },
        { label: 'Go to line…', accelerator: 'CmdOrCtrl+G', click: () => send('goto-line') },
        { label: 'Toggle bookmark', accelerator: 'CmdOrCtrl+B', click: () => send('toggle-bookmark') },
      ],
    },
    {
      label: 'View',
      submenu: [
        { label: 'Follow (tail)', accelerator: 'CmdOrCtrl+T', click: () => send('toggle-follow') },
        { label: 'Timeline', accelerator: 'CmdOrCtrl+Shift+H', click: () => send('toggle-histogram') },
        { label: 'Details pane', accelerator: 'CmdOrCtrl+Shift+D', click: () => send('toggle-detail') },
        { label: 'Sidebar', accelerator: 'CmdOrCtrl+Shift+B', click: () => send('toggle-sidebar') },
        { label: 'Details on the right', type: 'checkbox', checked: v.dockRight, click: () => send('toggle-dock') },
        { type: 'separator' },
        { label: 'Tint whole rows by level', type: 'checkbox', checked: v.tintRows, click: () => send('toggle-tint') },
        { label: 'Colour tokens in messages', type: 'checkbox', checked: v.tokenColors, click: () => send('toggle-tokens') },
        { label: 'Comfortable rows', type: 'checkbox', checked: v.comfortable, click: () => send('toggle-density') },
        // The renderer handles Alt+Z itself (so it also works with the menu bar hidden).
        { label: 'Wrap long messages', type: 'checkbox', checked: v.wrap, accelerator: 'Alt+Z', registerAccelerator: false, click: () => send('toggle-wrap') },
        {
          label: 'Time column',
          submenu: [
            ...timeModes.map(([mode, label]): Electron.MenuItemConstructorOptions => ({ label, type: 'radio', checked: v.timeMode === mode, click: () => send('time-mode', mode) })),
            { type: 'separator' },
            { label: 'Show in UTC', type: 'checkbox', checked: v.timeUtc, click: () => send('toggle-utc') },
            { label: 'Mark gaps over 10 s', type: 'checkbox', checked: v.gaps, click: () => send('toggle-gaps') },
          ],
        },
        { type: 'separator' },
        { label: 'Columns…', click: () => send('columns') },
        { label: 'Reopen with encoding…', click: () => send('encoding') },
        { label: 'Toggle dark mode', click: () => send('toggle-theme') },
        { type: 'separator' },
        { role: 'zoomIn' },
        { role: 'zoomOut' },
        { role: 'resetZoom' },
        { type: 'separator' },
        { role: 'toggleDevTools' },
      ],
    },
    {
      label: 'Help',
      submenu: [
        { label: 'Keyboard shortcuts', accelerator: 'F1', click: () => send('help') },
        { label: 'Project on GitHub', click: () => shell.openExternal('https://github.com/hovchik/UltraLogViewer') },
      ],
    },
  ];
  Menu.setApplicationMenu(Menu.buildFromTemplate(template));
}

function addRecent(paths: string[]) {
  const s = readSettings();
  const recent = ((s.recent as string[]) ?? []).filter((p) => !paths.includes(p));
  s.recent = [...paths, ...recent].slice(0, 15);
  writeSettings(s);
  buildMenu();
}

// -------------------------------------------------------------------- window

function createWindow() {
  win = new BrowserWindow({
    width: 1400,
    height: 900,
    minWidth: 720,
    minHeight: 420,
    title: 'UltraLogViewer',
    backgroundColor: nativeTheme.shouldUseDarkColors ? '#14161a' : '#ffffff',
    webPreferences: {
      preload: path.join(__dirname, '../preload/preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
    },
  });
  win.loadFile(path.join(__dirname, '../renderer/index.html'));
  win.webContents.on('will-navigate', (e) => e.preventDefault());
  win.webContents.setWindowOpenHandler(({ url }) => {
    if (/^https?:/.test(url)) void shell.openExternal(url);
    return { action: 'deny' };
  });
  win.on('closed', () => (win = null));
}

const filesFromArgv = (argv: string[]) =>
  argv.slice(app.isPackaged ? 1 : 2).filter((a) => !a.startsWith('-') && fs.existsSync(a) && fs.statSync(a).isFile()).map((a) => path.resolve(a));

let quitting = false;

if (!app.requestSingleInstanceLock()) {
  app.quit();
} else {
  app.on('second-instance', (_e, argv) => {
    for (const f of filesFromArgv(argv)) send('open-path', f);
    if (win) {
      if (win.isMinimized()) win.restore();
      win.focus();
    }
  });
  app.on('open-file', (e, file) => {
    e.preventDefault();
    if (win) send('open-path', file);
    else pendingOpen.push(file);
  });

  app.whenReady().then(() => {
    startWorker();
    ipcMain.handle('engine', (_e, method: string, args: unknown[]) => callEngine(method, args));
    ipcMain.handle('settings:get', () => readSettings());
    ipcMain.handle('settings:set', (_e, s: Record<string, unknown>) => {
      writeSettings(s);
      // keep the View menu's check marks in sync with the renderer
      if (JSON.stringify(viewMenuState(s)) !== menuState) buildMenu();
    });
    ipcMain.handle('recent:add', (_e, paths: string[]) => addRecent(paths));
    ipcMain.handle('dialog:open', async (_e, multi: boolean) => {
      const r = await dialog.showOpenDialog(win!, {
        properties: multi ? ['openFile', 'multiSelections'] : ['openFile', 'multiSelections'],
        filters: [{ name: 'Logs', extensions: ['log', 'txt', 'json', 'jsonl', 'gz', 'out', 'err', 'csv'] }, { name: 'All files', extensions: ['*'] }],
      });
      return r.canceled ? [] : r.filePaths;
    });
    ipcMain.handle('dialog:save', async (_e, defaultName: string) => {
      const r = await dialog.showSaveDialog(win!, {
        defaultPath: defaultName,
        filters: [{ name: 'Text', extensions: ['log', 'txt'] }, { name: 'CSV', extensions: ['csv'] }, { name: 'JSON lines', extensions: ['jsonl'] }],
      });
      return r.canceled ? null : r.filePath;
    });
    ipcMain.handle('startup-files', () => [...filesFromArgv(process.argv), ...pendingOpen.splice(0)]);
    buildMenu();
    createWindow();
    app.on('activate', () => {
      if (BrowserWindow.getAllWindows().length === 0) createWindow();
    });
  });

  app.on('before-quit', () => {
    quitting = true;
    void worker?.terminate();
  });
  app.on('window-all-closed', () => {
    if (process.platform !== 'darwin') app.quit();
  });
}
