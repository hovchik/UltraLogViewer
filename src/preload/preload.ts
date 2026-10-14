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
  startupFiles: (): Promise<string[]> => ipcRenderer.invoke('startup-files'),
  pathForFile: (f: File) => webUtils.getPathForFile(f),
  platform: process.platform,
};

contextBridge.exposeInMainWorld('ulv', api);
export type UlvApi = typeof api;
