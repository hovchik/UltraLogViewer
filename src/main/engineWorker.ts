// Worker-thread host for the Engine: request/response over postMessage, plus pushed events.
import { parentPort, workerData } from 'node:worker_threads';
import { Engine } from './engine';
import { watchSshPids } from '../core/remote';

const port = parentPort!;
const engine = new Engine((event) => port.postMessage({ event }), (workerData as { cacheDir?: string } | null)?.cacheDir);
// The ssh processes of remote files, so the main process can kill them when the app quits, even if this thread cannot.
watchSshPids((pids) => port.postMessage({ sshPids: pids }));

port.on('message', async (msg: { id: number; method: string; args: unknown[] }) => {
  try {
    const fn = (engine as unknown as Record<string, (...a: unknown[]) => unknown>)[msg.method];
    if (typeof fn !== 'function' || msg.method === 'dispose' || msg.method.startsWith('_')) throw new Error(`Unknown method ${msg.method}`);
    const result = await fn.apply(engine, msg.args);
    port.postMessage({ id: msg.id, result });
  } catch (e) {
    port.postMessage({ id: msg.id, error: (e as Error).message ?? String(e) });
  }
});
