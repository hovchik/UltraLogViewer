// The local file a receiver writes what it receives to, one line per message. The file is opened as a normal live tab,
// so indexing, filters and tailing work unchanged (as for standard input). Writes are asynchronous behind a queue, so
// bursts and a slow disk never hold up the sockets or the UI; when more than MAX_PENDING bytes wait, new lines are
// dropped and counted. At `capBytes` the file is rotated: it becomes "name.1.log" (replacing the previous one) and a new
// file starts under the same name.

import * as crypto from 'node:crypto';
import * as fs from 'node:fs';
import * as path from 'node:path';

export const MAX_PENDING = 64 * 1024 * 1024;

/** Creates the folder with owner-only permissions (and tightens them if it already exists). */
export function ensureFolder(dir: string): string {
  fs.mkdirSync(dir, { recursive: true, mode: 0o700 });
  if (process.platform !== 'win32') fs.chmodSync(dir, 0o700); // on Windows the folder is under the user's private profile
  return dir;
}

const p2 = (n: number) => String(n).padStart(2, '0');

// Created new (never through a planted link) and readable by this user only: received logs can hold anything.
const openNew = (file: string) => fs.promises.open(file, 'wx', 0o600);

export class CaptureFile {
  lines = 0;
  written = 0;
  dropped = 0;
  rotations = 0;
  /** The last write error (disk full, …), or null. */
  error: string | null = null;
  private queue: string[] = [];
  private pending = 0;
  private size = 0;
  private fh: Promise<fs.promises.FileHandle>;
  private pumping: Promise<void> | null = null;
  private closed = false;

  private constructor(readonly path: string, private cap: number) {
    this.cap = Math.max(64 * 1024, cap);
    // the file exists as soon as create() returns, so it can be opened as a tab at once
    fs.closeSync(fs.openSync(path, 'wx', 0o600));
    this.fh = fs.promises.open(path, 'a');
  }

  get rotatedPath() {
    return this.path.replace(/\.log$/, '') + '.1.log';
  }

  /** Starts a new capture "prefix-yyyyMMdd-HHmmss-random.log" in `dir`. */
  static create(dir: string, prefix: string, capBytes: number): CaptureFile {
    ensureFolder(dir);
    const d = new Date();
    const name = `${prefix}-${d.getFullYear()}${p2(d.getMonth() + 1)}${p2(d.getDate())}-${p2(d.getHours())}${p2(d.getMinutes())}${p2(d.getSeconds())}-${crypto.randomBytes(3).toString('hex')}.log`;
    return new CaptureFile(path.join(dir, name), capBytes);
  }

  /** Queues one line (a line break inside it must already be removed). Never blocks; false when dropped. */
  writeLine(line: string): boolean {
    const bytes = line.length * 2 + 1;
    if (this.closed || this.pending + bytes > MAX_PENDING) {
      this.dropped++;
      return false;
    }
    this.pending += bytes;
    this.queue.push(line);
    this.lines++;
    this.pumping ??= this.pump();
    return true;
  }

  private async pump() {
    try {
      while (this.queue.length) {
        const batch = this.queue;
        this.queue = [];
        let parts: Buffer[] = [];
        let partBytes = 0;
        const flush = async () => {
          if (!parts.length) return;
          try {
            await (await this.fh).write(Buffer.concat(parts));
            this.size += partBytes;
            this.written += parts.length;
          } catch (e) {
            this.error = (e as Error).message; // disk full or the folder went away: keep receiving, count it as dropped
            this.dropped += parts.length;
          }
          parts = [];
          partBytes = 0;
        };
        for (const line of batch) {
          this.pending -= line.length * 2 + 1;
          const b = Buffer.from(line + '\n', 'utf-8');
          if (this.size + partBytes > 0 && this.size + partBytes + b.length > this.cap) {
            await flush();
            await this.rotate();
          }
          parts.push(b);
          partBytes += b.length;
        }
        await flush(); // after each burst, so the live tail sees it at once
      }
    } finally {
      this.pumping = null;
      if (this.queue.length) this.pumping = this.pump();
    }
  }

  private async rotate() {
    try {
      await (await this.fh).close();
    } catch {
      // already closed
    }
    try {
      await fs.promises.rename(this.path, this.rotatedPath);
      this.fh = openNew(this.path);
      await this.fh;
    } catch {
      // could not keep the old part (locked, or a file appeared under the new name): start the file over
      this.fh = fs.promises.open(this.path, 'w');
    }
    this.size = 0;
    this.rotations++;
  }

  /** Waits until everything queued so far is on disk. */
  async flush(): Promise<void> {
    while (this.pumping) await this.pumping;
  }

  /** Stops taking lines and closes the file after writing what is queued. */
  async close(): Promise<void> {
    if (this.closed) return;
    this.closed = true;
    await this.flush();
    try {
      await (await this.fh).close();
    } catch {
      // already closed
    }
  }
}
