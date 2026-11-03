// A small pool of worker threads that scan file chunks in parallel (see scanChunk.ts). Indexing is CPU bound on the
// per-line format scan, so spreading chunks over the cores is what makes multi-gigabyte files open quickly. The same
// workers read and test entry batches for search, filtering, grouping, patterns, field stats and export (entryScan.ts).

import * as os from 'node:os';
import { Worker } from 'node:worker_threads';
import { EntryJob, jobBuffers, RangeResult, RowsResult } from './entryScan';
import { ChunkScan, FormatSpec } from './scanChunk';

interface Job {
  resolve: (r: unknown) => void;
  reject: (e: Error) => void;
}

interface Slot {
  worker: Worker;
  pending: number;
  specs: Set<number>;
}

let nextSpecId = 1;
const specIds = new WeakMap<FormatSpec, number>();

export class ScanPool {
  private slots: Slot[] = [];
  private jobs = new Map<number, Job>();
  private nextJob = 1;
  private broken = false;

  constructor(private script: string, readonly size = Math.max(1, Math.min(8, os.availableParallelism?.() ?? os.cpus().length))) {}

  /** False once a worker failed to start or crashed; callers then scan on their own thread. */
  get usable() {
    return !this.broken;
  }

  /** Scans `buf` (which is transferred to the worker and unusable afterwards). */
  scan(spec: FormatSpec, encoding: string, buf: Uint8Array, pos: number): Promise<ChunkScan> {
    const ab = buf.buffer as ArrayBuffer;
    return this.post(spec, { encoding, buf: ab, offset: buf.byteOffset, length: buf.byteLength, pos }, [ab]);
  }

  /** Reads and evaluates a batch of entries (see entryScan.ts); the job's arrays are transferred to the worker. */
  entries(spec: FormatSpec, job: EntryJob): Promise<RangeResult | RowsResult> {
    return this.post(spec, { job }, jobBuffers(job));
  }

  private post<T>(spec: FormatSpec, msg: object, transfer: ArrayBuffer[]): Promise<T> {
    if (this.slots.length < this.size) this.spawn();
    const slot = this.slots.reduce((a, b) => (b.pending < a.pending ? b : a));
    let specId = specIds.get(spec);
    if (specId === undefined) specIds.set(spec, (specId = nextSpecId++));
    const sendSpec = !slot.specs.has(specId);
    slot.specs.add(specId);
    const id = this.nextJob++;
    slot.pending++;
    return new Promise<T>((resolve, reject) => {
      this.jobs.set(id, {
        resolve: (r) => {
          slot.pending--;
          resolve(r as T);
        },
        reject: (e) => {
          slot.pending--;
          reject(e);
        },
      });
      slot.worker.postMessage({ id, specId, spec: sendSpec ? spec : undefined, ...msg }, transfer);
    });
  }

  private spawn() {
    const worker = new Worker(this.script);
    worker.unref(); // an idle pool never keeps the process alive
    const slot: Slot = { worker, pending: 0, specs: new Set() };
    worker.on('message', (m: { id: number; result?: unknown; error?: string }) => {
      const job = this.jobs.get(m.id);
      if (!job) return;
      this.jobs.delete(m.id);
      if (m.result) job.resolve(m.result);
      else job.reject(new Error(m.error ?? 'scan failed'));
    });
    const fail = (e: Error) => {
      this.broken = true;
      for (const [id, job] of this.jobs) {
        job.reject(e);
        this.jobs.delete(id);
      }
    };
    worker.on('error', fail);
    worker.on('exit', (code) => {
      this.slots = this.slots.filter((s) => s !== slot);
      if (code !== 0) fail(new Error(`scan worker exited with code ${code}`));
    });
    this.slots.push(slot);
  }

  close() {
    for (const s of this.slots) void s.worker.terminate();
    this.slots = [];
  }
}
