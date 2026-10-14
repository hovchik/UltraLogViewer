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
  filesExist: (paths: string[]): Promise<boolean[]> => ipcRenderer.invoke('files:exist', paths),
  startupFiles: (): Promise<string[]> => ipcRenderer.invoke('startup-files'),
  pathForFile: (f: File) => webUtils.getPathForFile(f),
  platform: process.platform,
};

contextBridge.exposeInMainWorld('ulv', api);
export type UlvApi = typeof api;
