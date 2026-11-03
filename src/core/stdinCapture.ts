// "ultralogviewer -": copies standard input (or any readable stream) into a growing temp file. The file is opened like
// any log, so the normal tail picks up new lines live; it is deleted when the app exits.

import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';

export interface StdinCapture {
  path: string;
  /** Resolves when the input has ended (or failed). */
  done: Promise<void>;
  dispose(): void;
}

const p2 = (n: number) => String(n).padStart(2, '0');

/**
 * Starts copying and resolves once there is enough to detect the format: 64 KB, the end of the input, half a second
 * after the first data, or `maxWaitMs` without any data.
 */
export async function captureStdin(input: NodeJS.ReadableStream, dir = path.join(os.tmpdir(), 'UltraLogViewer'), maxWaitMs = 3000): Promise<StdinCapture> {
  fs.mkdirSync(dir, { recursive: true });
  const d = new Date();
  const file = path.join(dir, `stdin-${d.getFullYear()}${p2(d.getMonth() + 1)}${p2(d.getDate())}-${p2(d.getHours())}${p2(d.getMinutes())}${p2(d.getSeconds())}-${process.pid}.log`);
  const fd = fs.openSync(file, 'w');
  let written = 0;
  let firstAt = 0;
  let ready!: () => void;
  const readyP = new Promise<void>((r) => (ready = r));
  const done = new Promise<void>((resolve) => {
    const finish = () => {
      try {
        fs.closeSync(fd);
      } catch {
        // already closed
      }
      ready();
      resolve();
    };
    input.on('data', (chunk: Buffer | string) => {
      const b = typeof chunk === 'string' ? Buffer.from(chunk) : chunk;
      try {
        fs.writeSync(fd, b); // every chunk reaches the file at once, for the live tail
      } catch {
        return;
      }
      written += b.length;
      if (!firstAt) firstAt = Date.now();
      if (written >= 64 * 1024) ready();
    });
    input.on('end', finish);
    input.on('error', finish);
  });
  const deadline = Date.now() + maxWaitMs;
  let settled = false;
  void readyP.then(() => (settled = true));
  while (!settled && Date.now() < deadline && !(firstAt && Date.now() - firstAt > 500)) await new Promise((r) => setTimeout(r, 20));
  return { path: file, done, dispose: () => fs.rmSync(file, { force: true }) };
}
