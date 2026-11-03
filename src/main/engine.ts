// The engine owns all open documents. It runs in a worker thread so indexing, filtering and
// searching gigabytes never block the UI.

import * as fs from 'node:fs';
import * as path from 'node:path';
import { LogDocument } from '../core/document';
import { ScanPool } from '../core/scanPool';
import { Encoding } from '../core/fileSource';
import { CustomFormat, DocInfo, FieldStats, FilterSpec, Level, PatternsResult, RowData, SearchResult, TextPattern } from '../core/types';

export interface OpenOptions {
  encoding?: Encoding;
  format?: string;
  customFormats?: CustomFormat[];
}

export type EngineEvent =
  | { type: 'doc-updated'; docId: number; info: DocInfo; reset: boolean }
  | { type: 'doc-error'; docId: number; message: string }
  | { type: 'progress'; docId: number; phase: string; fraction: number }
  /** Rows a running filter has found so far (`seq` as passed to setFilter); see LogDocument.viewEpoch. */
  | { type: 'view-partial'; docId: number; info: DocInfo; epoch: number; seq: number };

const POLL_MS = 500;
const PROGRESS_MS = 50;

export class Engine {
  private docs = new Map<number, LogDocument>();
  private nextId = 1;
  private searchGen = new Map<number, number>();
  private statsGen = new Map<number, number>();
  private patternsGen = new Map<number, number>();
  private timer: ReturnType<typeof setInterval> | null = null;
  private polling = false;
  private progressAt = new Map<string, number>();
  /** Scans big files on all cores; absent when the worker script is not built next to this file (unit tests). */
  private scanPool = fs.existsSync(path.join(__dirname, 'scanWorker.js')) ? new ScanPool(path.join(__dirname, 'scanWorker.js')) : undefined;

  constructor(private emit: (e: EngineEvent) => void) {
    this.timer = setInterval(() => void this.poll(), POLL_MS);
  }

  /**
   * Progress events, at most every 50 ms per document and phase (and always the last one): a scan on all cores
   * reports a thousand batches a second, and redrawing the status bar for each would take CPU from the scan.
   */
  private progress(docId: number, phase: string, fraction: number) {
    const key = `${docId}:${phase}`;
    const now = Date.now();
    if (fraction < 1 && now - (this.progressAt.get(key) ?? 0) < PROGRESS_MS) return;
    this.progressAt.set(key, now);
    this.emit({ type: 'progress', docId, phase, fraction });
  }

  private doc(id: number): LogDocument {
    const d = this.docs.get(id);
    if (!d) throw new Error(`Document ${id} is not open`);
    return d;
  }

  /** Opens one document over the given files (several files = merged timeline). Indexing continues in the background. */
  open(paths: string[], opts: OpenOptions = {}): number {
    const id = this.nextId++;
    const title = paths.length === 1 ? path.basename(paths[0]) : `${path.basename(paths[0])} + ${paths.length - 1} more`;
    const doc = new LogDocument(id, title);
    this.docs.set(id, doc);
    void (async () => {
      let last = 0;
      for (const p of paths) {
        try {
          await doc.addSource(p, { ...opts, scanPool: this.scanPool }, (phase, fraction) => {
            this.progress(id, phase, fraction);
            const now = Date.now();
            if (now - last > 250 && phase === 'index') {
              last = now;
              this.emit({ type: 'doc-updated', docId: id, info: doc.info(), reset: false });
            }
          });
        } catch (e) {
          this.emit({ type: 'doc-error', docId: id, message: `${path.basename(p)}: ${(e as Error).message}` });
        }
        if (!this.docs.has(id)) return;
      }
      this.emit({ type: 'doc-updated', docId: id, info: doc.info(), reset: true });
    })();
    return id;
  }

  close(docId: number) {
    this.docs.get(docId)?.close();
    this.docs.delete(docId);
    for (const k of this.progressAt.keys()) if (k.startsWith(`${docId}:`)) this.progressAt.delete(k);
  }

  info(docId: number): DocInfo {
    return this.doc(docId).info();
  }

  rows(docId: number, start: number, count: number): RowData[] {
    return this.doc(docId).getRows(start, count);
  }

  /** Applies a filter; while it runs, the rows found so far are pushed as `view-partial` events tagged with `seq`. */
  async setFilter(docId: number, spec: FilterSpec | null, seq = 0): Promise<DocInfo | null> {
    const d = this.doc(docId);
    let epoch = d.viewEpoch;
    let count = d.viewCount();
    const ok = await d.applyFilter(spec, false, (phase, fraction) => {
      this.progress(docId, phase, fraction);
      if (d.viewEpoch === epoch && d.viewCount() === count) return;
      epoch = d.viewEpoch;
      count = d.viewCount();
      this.emit({ type: 'view-partial', docId, info: d.info(), epoch, seq });
    });
    return ok ? d.info() : null;
  }

  async search(docId: number, pattern: TextPattern): Promise<SearchResult> {
    const gen = (this.searchGen.get(docId) ?? 0) + 1;
    this.searchGen.set(docId, gen);
    return this.doc(docId).search(
      pattern,
      (phase, fraction) => this.progress(docId, phase, fraction),
      () => this.searchGen.get(docId) !== gen,
    );
  }

  rowForLine(docId: number, source: number, line: number) {
    return this.doc(docId).rowForLine(source, line);
  }
  rowForTime(docId: number, t: number) {
    return this.doc(docId).rowForTime(t);
  }
  rowForKey(docId: number, key: string, nearest = false) {
    return this.doc(docId).rowForKey(key, nearest);
  }
  /** "Show in context": show every entry, dimming the ones the filter hides. */
  setIgnoreFilter(docId: number, on: boolean): DocInfo {
    const d = this.doc(docId);
    d.ignoreFilter = on;
    return d.info();
  }
  /** Top values per parsed field over the view; a newer call for the same document cancels the previous one. */
  async fieldStats(docId: number, maxRows?: number, top?: number): Promise<FieldStats> {
    const gen = (this.statsGen.get(docId) ?? 0) + 1;
    this.statsGen.set(docId, gen);
    return this.doc(docId).fieldStats(maxRows, top, () => this.statsGen.get(docId) !== gen || !this.docs.has(docId));
  }
  /** Message templates over the view; a newer call for the same document cancels the previous one. */
  async patterns(docId: number, maxRows?: number, top?: number): Promise<PatternsResult> {
    const gen = (this.patternsGen.get(docId) ?? 0) + 1;
    this.patternsGen.set(docId, gen);
    return this.doc(docId).patterns(maxRows, top, () => this.patternsGen.get(docId) !== gen || !this.docs.has(docId));
  }
  keyForLine(docId: number, source: number, line: number) {
    return this.doc(docId).keyForLine(source, line);
  }
  findLevel(docId: number, from: number, dir: 1 | -1, minLevel: Level) {
    return this.doc(docId).findLevel(from, dir, minLevel);
  }
  histogram(docId: number, buckets: number) {
    return this.doc(docId).histogram(buckets);
  }
  overview(docId: number, buckets: number) {
    return this.doc(docId).overview(buckets);
  }
  async exportView(docId: number, file: string, format: 'text' | 'csv' | 'jsonl') {
    return this.doc(docId).export(file, format, (phase, fraction) => this.progress(docId, phase, fraction));
  }
  async reformat(docId: number, formatId: string | undefined, customFormats?: CustomFormat[]) {
    const d = this.doc(docId);
    await d.reformat(formatId, customFormats);
    const info = d.info();
    this.emit({ type: 'doc-updated', docId, info, reset: true });
    return info;
  }

  /** Tail: picks up appended or rotated data for every open document. */
  private async poll() {
    if (this.polling) return;
    this.polling = true;
    try {
      for (const [id, d] of this.docs) {
        if (d.indexing) continue;
        try {
          if (await d.refresh()) this.emit({ type: 'doc-updated', docId: id, info: d.info(), reset: false });
        } catch (e) {
          this.emit({ type: 'doc-error', docId: id, message: (e as Error).message });
        }
      }
    } finally {
      this.polling = false;
    }
  }

  dispose() {
    if (this.timer) clearInterval(this.timer);
    for (const id of [...this.docs.keys()]) this.close(id);
    this.scanPool?.close();
  }
}

export type EngineMethod = Exclude<keyof Engine, 'dispose'>;
