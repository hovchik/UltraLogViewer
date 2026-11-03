// Network receivers and Windows event log exports live in the main process: the sockets and the wevtutil process stay
// out of the renderer, which only gets a capture file path to open as a live tab and the status lines to show.

import { spawn } from 'node:child_process';
import * as path from 'node:path';
import { ipcMain } from 'electron';
import { LogReceiver, ReceiverOptions, ReceiverProtocol } from '../core/receivers';
import { Wevtutil, WinEventLog } from '../core/winEvent';

type Capture = LogReceiver | WinEventLog;

const captures = new Map<number, Capture>();
let nextId = 1;

/** wevtutil.exe with an argument array: no shell, so nothing in a channel name or query is interpreted. */
const wevtutil: Wevtutil = {
  run(args, output, signal) {
    return new Promise((resolve, reject) => {
      const p = spawn('wevtutil.exe', args, { windowsHide: true, signal });
      let err = '';
      p.stdout.setEncoding('utf8');
      p.stdout.on('data', (t: string) => output(t));
      p.stderr.setEncoding('utf8');
      p.stderr.on('data', (t: string) => (err = (err + t).slice(-4000)));
      p.on('error', reject);
      p.on('close', (code) => resolve({ code: code ?? -1, error: err.trim() }));
    });
  },
};

const PROTOCOLS: ReceiverProtocol[] = ['syslog-udp', 'syslog-tcp', 'tcp-lines', 'log4j-xml'];
const capBytes = (mb: unknown) => Math.max(1, Math.min(1024 * 1024, Number(mb) || 1024)) * 1024 * 1024;

export function registerReceivers(userData: () => string) {
  const dir = () => path.join(userData(), 'receivers');
  ipcMain.handle('receivers:dir', () => dir());
  ipcMain.handle('receiver:start', async (_e, o: { protocol?: unknown; bind?: unknown; port?: unknown; maxMessage?: unknown; capMb?: unknown }) => {
    if (!PROTOCOLS.includes(o?.protocol as ReceiverProtocol)) throw new Error('Unknown protocol');
    const opts: ReceiverOptions = {
      protocol: o.protocol as ReceiverProtocol,
      bind: String(o.bind ?? '127.0.0.1'),
      port: Number(o.port),
      maxMessage: Number(o.maxMessage ?? 64 * 1024),
      capBytes: capBytes(o.capMb),
    };
    try {
      const r = await LogReceiver.start(opts, dir());
      const id = nextId++;
      captures.set(id, r);
      return { id, path: r.path, endpoint: r.endpoint, port: r.port };
    } catch (e) {
      const err = e as NodeJS.ErrnoException;
      // the message is what the user sees: "the port is already in use" rather than a raw errno
      return { error: err.code === 'EADDRINUSE' ? 'the port is already in use' : err.code === 'EACCES' ? 'permission denied (ports below 1024 need administrator rights)' : err.message };
    }
  });
  ipcMain.handle('eventlog:start', (_e, channel: unknown, from: unknown, until: unknown, follow: unknown, capMb: unknown) => {
    if (process.platform !== 'win32') return { error: 'The Windows event log is only available on Windows' };
    const num = (v: unknown) => (typeof v === 'number' && Number.isFinite(v) ? v : null);
    try {
      const log = new WinEventLog(wevtutil, String(channel), num(from), num(until), !!follow, dir(), capBytes(capMb));
      const id = nextId++;
      captures.set(id, log);
      return { id, path: log.path };
    } catch (e) {
      return { error: (e as Error).message };
    }
  });
  ipcMain.handle('eventlog:first', (_e, id: number) => {
    const c = captures.get(Number(id));
    return c instanceof WinEventLog ? c.firstExport : null;
  });
  ipcMain.handle('capture:status', (_e, ids: unknown) => {
    const out: Record<number, { status: string; running: boolean }> = {};
    for (const id of Array.isArray(ids) ? ids : []) {
      const c = captures.get(Number(id));
      if (c) out[Number(id)] = { status: c.status, running: c.running };
    }
    return out;
  });
  ipcMain.handle('capture:stop', async (_e, id: unknown, forget: unknown) => {
    const c = captures.get(Number(id));
    if (!c) return;
    await c.stop();
    if (forget) captures.delete(Number(id)); // the tab closed
  });
}

/** When the app quits: stop listening and close every capture. */
export async function stopAllReceivers() {
  await Promise.all([...captures.values()].map((c) => c.stop()));
  captures.clear();
}
