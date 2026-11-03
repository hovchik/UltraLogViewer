import { contextBridge, ipcRenderer, webUtils } from 'electron';

const api = {
  call: (method: string, ...args: unknown[]) => ipcRenderer.invoke('engine', method, args),
  onEvent: (cb: (e: unknown) => void) => ipcRenderer.on('engine-event', (_e, ev) => cb(ev)),
  onMenu: (cb: (cmd: string, ...args: unknown[]) => void) => ipcRenderer.on('menu', (_e, cmd, ...args) => cb(cmd, ...args)),
  openDialog: (multi: boolean): Promise<string[]> => ipcRenderer.invoke('dialog:open', multi),
  saveDialog: (name: string): Promise<string | null> => ipcRenderer.invoke('dialog:save', name),
  getSettings: (): Promise<Record<string, unknown>> => ipcRenderer.invoke('settings:get'),
  setSettings: (s: Record<string, unknown>) => ipcRenderer.invoke('settings:set', s),
  addRecent: (paths: string[]) => ipcRenderer.invoke('recent:add', paths),
  menuItems: (): Promise<{ id: string; label: string; accelerator?: string; checked?: boolean; enabled: boolean }[]> => ipcRenderer.invoke('menu:items'),
  runMenuItem: (id: string): Promise<boolean> => ipcRenderer.invoke('menu:run', id),
  setMenuState: (s: { groupRepeats: string; patterns: boolean }) => ipcRenderer.invoke('menu:state', s),
  workspaceOpenDialog: (): Promise<string | null> => ipcRenderer.invoke('workspace:open-dialog'),
  workspaceSaveDialog: (name: string): Promise<string | null> => ipcRenderer.invoke('workspace:save-dialog', name),
  readWorkspace: (file: string): Promise<string> => ipcRenderer.invoke('workspace:read', file),
  writeWorkspace: (file: string, text: string) => ipcRenderer.invoke('workspace:write', file, text),
  addRecentWorkspace: (file: string) => ipcRenderer.invoke('workspace:recent', file),
  folderDialog: (): Promise<string | null> => ipcRenderer.invoke('dialog:folder'),
  /** Opens a source location in the configured editor; ok false without `error` when no source root has the file. */
  openSource: (
    ref: { start: number; length: number; path: string; line: number; col: number; pkg?: string },
    opts: { roots: string[]; editor: string; command: string },
  ): Promise<{ ok: boolean; file?: string; target?: string; error?: string }> => ipcRenderer.invoke('source:open', ref, opts),
  filesExist: (paths: string[]): Promise<boolean[]> => ipcRenderer.invoke('files:exist', paths),
  startupFiles: (): Promise<string[]> => ipcRenderer.invoke('startup-files'),
  /** Command-line window (--since/--until/--last), its errors, and whether stdin is being read. */
  startupOptions: (): Promise<{ window: { since?: number | null; until?: number | null; last?: number | null } | null; errors: string[]; stdin: boolean }> =>
    ipcRenderer.invoke('startup-options'),
  /** The startup command line as an open request (files resolved, a link already confirmed), and whether stdin is read. */
  startupRequest: (): Promise<{ request: import('../core/openRequest').OpenRequest | null; stdin: boolean }> => ipcRenderer.invoke('startup-request'),
  /** Open requests and local API calls from the main process; answer each with apiReply. */
  onApiCall: (cb: (id: number, method: string, args: unknown[]) => void) => {
    ipcRenderer.on('api-call', (_e, id, method, args) => cb(id, method, args));
    return ipcRenderer.invoke('api:ready');
  },
  apiReply: (id: number, result: unknown) => ipcRenderer.send('api:reply', id, result),
  /** Turns the local HTTP API on or off (main keeps the token); returns the saved settings and the port. */
  apiConfigure: (enabled: boolean): Promise<{ settings: { enabled?: boolean; port?: number; token?: string }; port: number | null; error: string }> =>
    ipcRenderer.invoke('api:configure', enabled),
  apiNewToken: (): Promise<{ settings: { enabled?: boolean; port?: number; token?: string }; port: number | null; error: string }> => ipcRenderer.invoke('api:new-token'),
  apiStatus: (): Promise<{ port: number | null; token: string | null; error: string }> => ipcRenderer.invoke('api:status'),
  pathForFile: (f: File) => webUtils.getPathForFile(f),
  /** Which AI keys are saved (never the keys themselves) and how they are protected. */
  aiKeys: (): Promise<{ anthropic: boolean; openai: boolean; protection: string }> => ipcRenderer.invoke('ai:keys'),
  /** Saves a key in the main process; an empty key deletes it. */
  aiSetKey: (provider: string, key: string) => ipcRenderer.invoke('ai:set-key', provider, key),
  /** Sends a prompt the user approved; the main process adds the key and refuses while AI is off. */
  aiSend: (id: number, prompt: { system: string; user: string }): Promise<{ ok: boolean; text?: string; error?: string; cancelled?: boolean }> =>
    ipcRenderer.invoke('ai:send', id, prompt),
  aiAbort: (id: number) => ipcRenderer.invoke('ai:abort', id),
  platform: process.platform,
};

contextBridge.exposeInMainWorld('ulv', api);
export type UlvApi = typeof api;
