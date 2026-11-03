// Remote files over ssh: a remote file is copied into a local file and kept up to date with `tail -c +N -F`, so the
// normal indexing and live tail read it. The system OpenSSH client is used (spawned with an argument list, never a
// shell), so the user's ~/.ssh/config, keys, agent and known_hosts apply, as with lnav. When the connection drops it
// reconnects with backoff and goes on from the bytes it has (from 0 if the file got smaller); when the remote file is
// rotated or truncated it starts over from 0. All sessions are listed, so closing a tab or the app kills their ssh.

import { ChildProcess, spawn } from 'node:child_process';
import * as crypto from 'node:crypto';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { ensureFolder } from './captureFile';
import {
  explainSsh,
  globCommand,
  hasGlob,
  isRotation,
  listCommand,
  remoteDisplay,
  remoteUrl,
  RemoteState,
  RemoteStatus,
  RemoteTarget,
  sizeCommand,
  sshArgs,
  tailCommand,
  validateRemote,
} from './remoteTarget';

/** Environment variable with the ssh program to use (tests point it at a fake one). */
export const SSH_ENV = 'ULV_SSH';

/** The ssh program: the setting, else $ULV_SSH, else "ssh" from the PATH. */
export const sshProgram = (setting?: string | null) => (setting?.trim() ? setting.trim() : process.env[SSH_ENV] || 'ssh');

export interface RemoteOptions {
  /** The ssh program (see sshProgram). */
  ssh?: string | null;
  /** Folder of the local copies (per user, not the shared temp folder). */
  cacheDir: string;
  /** First wait before reconnecting; it doubles up to maxRetryMs. */
  retryMs?: number;
  maxRetryMs?: number;
}

const running = new Set<ChildProcess>();
let onPids: ((pids: number[]) => void) | null = null;

/** Tells the main process which ssh processes run, so it can kill them even if this worker does not get to. */
export function watchSshPids(cb: ((pids: number[]) => void) | null) {
  onPids = cb;
}
const pidsChanged = () => onPids?.([...running].map((p) => p.pid).filter((p): p is number => !!p));

function start(program: string, t: RemoteTarget, command: string): ChildProcess {
  const child = spawn(program, sshArgs(t, command), { stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true });
  running.add(child);
  pidsChanged();
  const forget = () => {
    if (running.delete(child)) pidsChanged();
  };
  child.once('exit', forget);
  child.once('error', forget);
  return child;
}

function kill(child: ChildProcess) {
  if (child.exitCode === null && child.signalCode === null) child.kill('SIGKILL');
}

/** Ssh processes still running (tests: none after a tab is closed). */
export const runningSsh = () => [...running].filter((c) => c.exitCode === null && c.signalCode === null).length;

export interface SshResult {
  code: number;
  out: string;
  err: string;
}

/** Runs a short command and collects its output (killed after `timeoutMs`). */
export function runSsh(program: string, t: RemoteTarget, command: string, timeoutMs = 30_000): Promise<SshResult> {
  return new Promise((resolve, reject) => {
    let child: ChildProcess;
    try {
      child = start(program, t, command);
    } catch (e) {
      return reject(new Error(`Could not start ${program}: ${(e as Error).message}`));
    }
    const out: Buffer[] = [];
    const err: Buffer[] = [];
    child.stdout!.on('data', (b: Buffer) => out.push(b));
    child.stderr!.on('data', (b: Buffer) => err.push(b));
    const timer = setTimeout(() => {
      kill(child);
      reject(new Error(`${t.host}: no answer in time`));
    }, timeoutMs);
    child.once('error', (e) => {
      clearTimeout(timer);
      reject(new Error(`Could not start ${program}: ${e.message}. Install the OpenSSH client or set its path in the settings.`));
    });
    child.once('close', (code) => {
      clearTimeout(timer);
      resolve({ code: code ?? 255, out: Buffer.concat(out).toString('utf-8'), err: Buffer.concat(err).toString('utf-8') });
    });
  });
}

const sessions = new Map<string, RemoteFile>();

/** The session whose local copy is `localPath`, if any. */
export const findRemote = (localPath: string) => sessions.get(localPath);
export const allRemote = () => [...sessions.values()];
/** "host:path" for a local copy, else undefined. */
export const remoteDisplayName = (localPath: string) => {
  const r = sessions.get(localPath);
  return r ? remoteDisplay(r.target) : undefined;
};

/** The same local name for the same remote file every time, so bookmarks and pins find it again. */
export function localPathFor(t: RemoteTarget, dir: string): string {
  const hash = crypto.createHash('sha256').update(remoteUrl(t)).digest('hex').slice(0, 12);
  const name = (t.path.split('/').pop() ?? '').replace(/[^A-Za-z0-9._-]/g, '_') || 'file';
  return path.join(dir, `${t.host.replace(/[^A-Za-z0-9.-]/g, '_')}-${hash}-${name}`);
}

const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

export class RemoteFile {
  readonly localPath: string;
  state: RemoteState = 'connecting';
  received = 0;
  expected = 0;
  attempt = 0;
  /** Ssh processes started so far (tests: a reconnect starts another). */
  starts = 0;
  private error = '';
  private fd = -1;
  private proc: ChildProcess | null = null;
  private closed = false;
  private rotated = false;
  private readonly program: string;
  /** Resolves when the background loop has stopped. */
  stopped: Promise<void> = Promise.resolve();

  private constructor(
    readonly target: RemoteTarget,
    private opts: RemoteOptions,
  ) {
    this.program = sshProgram(opts.ssh);
    this.localPath = localPathFor(target, opts.cacheDir);
  }

  status(): RemoteStatus {
    return { url: remoteUrl(this.target), display: remoteDisplay(this.target), state: this.state, error: this.error, received: this.received, expected: this.expected, attempt: this.attempt };
  }

  /**
   * Starts copying a remote file (or joins the session already copying it) and resolves once there is enough to detect
   * the format: the whole file when it is small, else its first 256 KB. Rejects when the file cannot be read.
   */
  static async open(target: RemoteTarget, opts: RemoteOptions): Promise<RemoteFile> {
    const existing = sessions.get(localPathFor(target, opts.cacheDir));
    if (existing && existing.state !== 'closed' && existing.state !== 'failed') return existing;
    const r = new RemoteFile(target, opts);
    await r.start();
    return r;
  }

  private async start() {
    this.expected = await this.probeSize();
    ensureFolder(this.opts.cacheDir); // owner-only: the copies hold the remote logs
    sessions.get(this.localPath)?.close(); // a failed session of the same file
    this.fd = fs.openSync(this.localPath, 'w', 0o600);
    sessions.set(this.localPath, this);
    this.state = this.expected === 0 ? 'live' : 'copying';
    this.stopped = this.loop();
    const want = Math.min(this.expected, 256 * 1024);
    const deadline = Date.now() + 20_000;
    while (this.received < want && ['copying', 'connecting'].includes(this.state as RemoteState) && Date.now() < deadline) await sleep(15);
  }

  private async probeSize(): Promise<number> {
    const r = await runSsh(this.program, this.target, sizeCommand(this.target.path));
    const n = Number(r.out.trim());
    if (r.code !== 0 || !/^\d+$/.test(r.out.trim())) throw new Error(explainSsh(this.target, r.code, r.err || r.out));
    return n;
  }

  private async loop() {
    while (!this.closed) {
      const began = Date.now();
      const { code, stderr } = await this.streamOnce();
      if (this.closed) break;
      if (this.rotated) {
        // Rotated or truncated: the local copy starts over, so the viewer re-reads it from the top.
        this.rotated = false;
        this.restart();
        continue;
      }
      if (Date.now() - began > 10_000) this.attempt = 0; // it ran for a while: a fresh drop
      this.attempt++;
      this.error = explainSsh(this.target, code, stderr);
      this.state = 'reconnecting';
      while (!this.closed) {
        await sleep(Math.min(this.opts.maxRetryMs ?? 30_000, (this.opts.retryMs ?? 1000) * 2 ** Math.min(this.attempt - 1, 16)));
        if (this.closed) break;
        try {
          const size = await this.probeSize();
          this.expected = size;
          if (size < this.received) this.restart(); // replaced by a smaller file while we were away
          break;
        } catch (e) {
          this.attempt++;
          this.error = (e as Error).message;
        }
      }
    }
  }

  private restart() {
    fs.ftruncateSync(this.fd, 0);
    this.received = 0;
    this.expected = 0; // the new file's size is not known: no copy progress
  }

  private streamOnce(): Promise<{ code: number; stderr: string }> {
    return new Promise((resolve) => {
      let child: ChildProcess;
      try {
        child = start(this.program, this.target, tailCommand(this.target.path, this.received));
      } catch (e) {
        return resolve({ code: 255, stderr: (e as Error).message });
      }
      this.starts++;
      this.proc = child;
      let stderr = '';
      let partial = '';
      // Still running after a moment: connected (a failed login or a missing host ends ssh sooner).
      const connected = setTimeout(() => {
        if (this.proc === child && !this.closed && this.state === 'reconnecting') this.state = this.received < this.expected ? 'copying' : 'live';
      }, 1500);
      child.stdout!.on('data', (b: Buffer) => {
        if (this.rotated || this.closed) return; // what follows belongs to the new file: it is fetched again from 0
        fs.writeSync(this.fd, b, 0, b.length, this.received); // at once, so the live tail of the local copy sees it
        this.received += b.length;
        this.state = this.received < this.expected ? 'copying' : 'live';
      });
      child.stderr!.on('data', (b: Buffer) => {
        const text = partial + b.toString('utf-8');
        const lines = text.split('\n');
        partial = lines.pop() ?? '';
        for (const line of lines) {
          stderr += line + '\n';
          if (isRotation(line)) {
            this.rotated = true;
            kill(child);
          }
        }
      });
      const done = (code: number | null) => {
        clearTimeout(connected);
        resolve({ code: code ?? 255, stderr: stderr + partial });
      };
      child.once('error', (e) => {
        stderr += e.message;
        done(255);
      });
      child.once('close', done);
    });
  }

  close() {
    if (this.state === 'closed') return;
    this.state = 'closed';
    this.closed = true;
    if (sessions.get(this.localPath) === this) sessions.delete(this.localPath);
    if (this.proc) kill(this.proc);
    if (this.fd >= 0) fs.closeSync(this.fd);
    this.fd = -1;
    fs.rmSync(this.localPath, { force: true });
  }
}

/** Closes the sessions whose local copies no open document uses any more. */
export function pruneRemote(inUse: Iterable<string>) {
  const used = new Set(inUse);
  for (const r of [...sessions.values()]) if (!used.has(r.localPath)) r.close();
}

/** The app is closing: every ssh process goes. */
export function closeAllRemote() {
  for (const r of [...sessions.values()]) r.close();
  for (const c of running) kill(c);
}

/** The files a path with * or ? matches on its host (expanded there by ls); a plain path as it is. */
export async function expandRemote(pattern: RemoteTarget, opts: { ssh?: string | null }): Promise<RemoteTarget[]> {
  validateRemote(pattern);
  if (!hasGlob(pattern.path)) return [pattern];
  const r = await runSsh(sshProgram(opts.ssh), pattern, globCommand(pattern.path));
  if (r.code !== 0) throw new Error(explainSsh(pattern, r.code, r.err));
  const out: RemoteTarget[] = [];
  for (const line of r.out.split('\n')) {
    const t = { ...pattern, path: line.replace(/\r$/, '') };
    if (!t.path) continue;
    try {
      validateRemote(t);
      out.push(t);
    } catch {
      // a name that cannot be passed on safely: left out
    }
  }
  if (!out.length) throw new Error(`${pattern.host}: no file matches ${pattern.path}`);
  return out;
}

/** The entries of a remote folder ("name", or "name/" for a folder), for the Browse button. */
export async function listRemote(dir: RemoteTarget, opts: { ssh?: string | null }): Promise<string[]> {
  const r = await runSsh(sshProgram(opts.ssh), dir, listCommand(dir.path));
  if (r.code !== 0) throw new Error(explainSsh(dir, r.code, r.err));
  return r.out
    .split('\n')
    .map((l) => l.replace(/\r$/, ''))
    .filter((l) => l && !/[\x00-\x1f\x7f]/.test(l));
}
