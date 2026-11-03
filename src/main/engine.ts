// The engine owns all open documents. It runs in a worker thread so indexing, filtering and
// searching gigabytes never block the UI.

import * as fs from 'node:fs';
import * as path from 'node:path';
import { displayName, expandArchives } from '../core/archives';
import { LogDocument } from '../core/document';
import { clearCache } from '../core/indexCache';
import { OpenWindow } from '../core/timeWindow';
import { ScanPool } from '../core/scanPool';
import { Encoding } from '../core/fileSource';
import { CustomFormat, DocInfo, FieldStats, FilterSpec, Level, PatternsResult, RowData, SearchResult, TextPattern } from '../core/types';

export interface OpenOptions {
  encoding?: Encoding;
  format?: string;
  customFormats?: CustomFormat[];
  /** Use the index cache (when the engine has a cache folder). */
  cache?: boolean;
  cacheMaxBytes?: number;
  /** Open only this time window of the files. */
  window?: OpenWindow | null;
}

export type EngineEvent =
  | { type: 'doc-updated'; docId: number; info: DocInfo; reset: boolean }
  | { type: 'doc-error'; docId: number; message: string }
  | { type: 'progress'; docId: number; phase: string; fraction: number };

const POLL_MS = 500;

export class Engine {
  private docs = new Map<number, LogDocument>();
  private nextId = 1;
  private searchGen = new Map<number, number>();
  private statsGen = new Map<number, number>();
  private patternsGen = new Map<number, number>();
  private timer: ReturnType<typeof setInterval> | null = null;
  private polling = false;
  /** Scans big files on all cores; absent when the worker script is not built next to this file (unit tests). */
  private scanPool = fs.existsSync(path.join(__dirname, 'scanWorker.js')) ? new ScanPool(path.join(__dirname, 'scanWorker.js')) : undefined;

  /** `cacheDir`: where the index cache lives (the app's user-data folder); none turns the cache off. */
  constructor(private emit: (e: EngineEvent) => void, private cacheDir?: string) {
    this.timer = setInterval(() => void this.poll(), POLL_MS);
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
    const { cache, cacheMaxBytes, ...rest } = opts;
    const sourceOpts = { ...rest, scanPool: this.scanPool, cacheDir: cache && this.cacheDir ? this.cacheDir : undefined, cacheMaxBytes };
    void (async () => {
      let last = 0;
      try {
        // A zip opens as its text members, merged; all files of a merge are indexed in parallel.
        const failed = await doc.addSources(expandArchives(paths), () => sourceOpts, (phase, fraction) => {
          this.emit({ type: 'progress', docId: id, phase, fraction });
          const now = Date.now();
          if (now - last > 250 && phase === 'index') {
            last = now;
            this.emit({ type: 'doc-updated', docId: id, info: doc.info(), reset: false });
          }
        });
        for (const f of failed.slice(0, 3)) this.emit({ type: 'doc-error', docId: id, message: `${displayName(f.path)}: ${f.error.message}` });
      } catch (e) {
        this.emit({ type: 'doc-error', docId: id, message: `${path.basename(paths[0])}: ${(e as Error).message}` });
      }
      if (!this.docs.has(id)) return;
      this.emit({ type: 'doc-updated', docId: id, info: doc.info(), reset: true });
    })();
    return id;
  }

  /** Deletes the saved indexes; returns the bytes freed. */
  clearIndexCache(): number {
    return this.cacheDir ? clearCache(this.cacheDir) : 0;
  }

  close(docId: number) {
    this.docs.get(docId)?.close();
    this.docs.delete(docId);
  }

  info(docId: number): DocInfo {
    return this.doc(docId).info();
  }

  rows(docId: number, start: number, count: number): RowData[] {
    return this.doc(docId).getRows(start, count);
  }

  async setFilter(docId: number, spec: FilterSpec | null): Promise<DocInfo | null> {
    const d = this.doc(docId);
    const ok = await d.applyFilter(spec, false, (phase, fraction) => this.emit({ type: 'progress', docId, phase, fraction }));
    return ok ? d.info() : null;
  }

  async search(docId: number, pattern: TextPattern): Promise<SearchResult> {
    const gen = (this.searchGen.get(docId) ?? 0) + 1;
    this.searchGen.set(docId, gen);
    return this.doc(docId).search(
      pattern,
      (phase, fraction) => this.emit({ type: 'progress', docId, phase, fraction }),
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
    return this.doc(docId).export(file, format, (phase, fraction) => this.emit({ type: 'progress', docId, phase, fraction }));
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
