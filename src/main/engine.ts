// The engine owns all open documents. It runs in a worker thread so indexing, filtering and
// searching gigabytes never block the UI.

import * as path from 'node:path';
import { LogDocument } from '../core/document';
import { Encoding } from '../core/fileSource';
import { CustomFormat, DocInfo, FieldStats, FilterSpec, Level, RowData, SearchResult, TextPattern } from '../core/types';

export interface OpenOptions {
  encoding?: Encoding;
  format?: string;
  customFormats?: CustomFormat[];
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
  private timer: ReturnType<typeof setInterval> | null = null;
  private polling = false;

  constructor(private emit: (e: EngineEvent) => void) {
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
    void (async () => {
      let last = 0;
      for (const p of paths) {
        try {
          await doc.addSource(p, opts, (phase, fraction) => {
            this.emit({ type: 'progress', docId: id, phase, fraction });
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
  }
}

export type EngineMethod = Exclude<keyof Engine, 'dispose'>;
