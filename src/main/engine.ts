// The engine owns all open documents. It runs in a worker thread so indexing, filtering and
// searching gigabytes never block the UI.

import * as fs from 'node:fs';
import * as path from 'node:path';
import { displayName, expandArchives } from '../core/archives';
import { LogDocument } from '../core/document';
import { SourceSet } from '../core/sourceSet';
import { clearCache } from '../core/indexCache';
import { OpenWindow } from '../core/timeWindow';
import { ScanPool } from '../core/scanPool';
import { Encoding } from '../core/fileSource';
import { closeAllRemote, findRemote, pruneRemote } from '../core/remote';
import { listRemoteFolder, localCopies, remoteCacheDir } from './engineRemote';
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
  private groupsGen = new Map<number, number>();
  private timer: ReturnType<typeof setInterval> | null = null;
  private polling = false;
  private progressAt = new Map<string, number>();
  /** Scans big files on all cores; absent when the worker script is not built next to this file (unit tests). */
  private scanPool = fs.existsSync(path.join(__dirname, 'scanWorker.js')) ? new ScanPool(path.join(__dirname, 'scanWorker.js')) : undefined;

  /** `cacheDir`: where the index cache lives (the app's user-data folder); none turns the cache off. */
  constructor(private emit: (e: EngineEvent) => void, private cacheDir?: string) {
    this.timer = setInterval(() => void this.poll(), POLL_MS);
  }

  // ------------------------------------------------- remote files (engineRemote.ts)

  /** Local copies being opened by `openRemote` and not yet passed to `open`: never pruned. */
  private pendingRemote = new Set<string>();
  /** The paths each document was opened with (its sources appear only once indexing has started). */
  private docPaths = new Map<number, string[]>();
  /** `openRemote` calls running: their new copies are in no document yet, so pruning waits for them. */
  private remoteOpening = 0;
  /** The connection state last sent per document, so a change is pushed even without new data. */
  private remoteSent = new Map<number, string>();

  /**
   * Remote files (ssh:// URLs, user@host:/path, patterns with * or ?) replaced by local copies that are kept up to
   * date, all hosts at once; local paths pass through. Pass the result to `open`.
   */
  async openRemote(paths: string[], opts: { ssh?: string | null } = {}): Promise<{ paths: string[]; errors: string[] }> {
    this.remoteOpening++;
    try {
      const r = await localCopies(paths, { ssh: opts.ssh, cacheDir: remoteCacheDir(this.cacheDir) });
      for (const p of r.paths) this.pendingRemote.add(p);
      return r;
    } finally {
      this.remoteOpening--;
    }
  }

  /** The entries of a remote folder (for the Browse button of "Open remote file…"). */
  listRemote(url: string, ssh?: string | null): Promise<string[]> {
    return listRemoteFolder(url, ssh);
  }

  /** The app quits: every ssh process goes. */
  closeRemote() {
    closeAllRemote();
  }

  /** Ends the ssh sessions that no open document shows any more. */
  private pruneRemote() {
    if (this.remoteOpening > 0) return; // (open() prunes once they are in a document)
    pruneRemote([...this.pendingRemote, ...[...this.docPaths.values()].flat(), ...[...this.docs.values()].flatMap((d) => d.sources.map((s) => s.path))]);
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
    const wasPending = paths.filter((p) => this.pendingRemote.delete(p)).length > 0;
    const id = this.nextId++;
    // (displayName: "host:path" for the local copy of a remote file)
    const title = paths.length === 1 ? displayName(paths[0]) : `${displayName(paths[0])} + ${paths.length - 1} more`;
    const doc = new LogDocument(id, title);
    this.docs.set(id, doc);
    this.docPaths.set(id, paths);
    if (wasPending) this.pruneRemote(); // one that openRemote put off
    const { cache, cacheMaxBytes, ...rest } = opts;
    const sourceOpts = { ...rest, scanPool: this.scanPool, cacheDir: cache && this.cacheDir ? this.cacheDir : undefined, cacheMaxBytes };
    void (async () => {
      let last = 0;
      try {
        // A zip opens as its text members, merged; all files of a merge are indexed in parallel.
        const failed = await doc.addSources(expandArchives(paths), () => sourceOpts, (phase, fraction) => {
          this.progress(id, phase, fraction);
          const now = Date.now();
          if (now - last > 250 && phase === 'index') {
            last = now;
            this.emit({ type: 'doc-updated', docId: id, info: doc.info(), reset: false });
          }
        });
        // A document closed while loading: its files "failed" because they were closed.
        if (this.docs.has(id)) for (const f of failed.slice(0, 3)) this.emit({ type: 'doc-error', docId: id, message: `${displayName(f.path)}: ${f.error.message}` });
      } catch (e) {
        this.emit({ type: 'doc-error', docId: id, message: `${path.basename(paths[0])}: ${(e as Error).message}` });
      }
      if (!this.docs.has(id)) return;
      this.emit({ type: 'doc-updated', docId: id, info: doc.info(), reset: true });
    })();
    return id;
  }

  /**
   * Opens another document over the same, already indexed files (a duplicated tab or a split pane). It needs no
   * indexing, starts unfiltered and shares tailing with the original; the files close when the last one is closed.
   */
  duplicate(docId: number): number {
    const src = this.doc(docId);
    if (src.indexing) throw new Error('The file is still being indexed');
    const id = this.nextId++;
    this.docs.set(id, src.duplicate(id));
    this.docPaths.set(id, this.docPaths.get(docId) ?? []);
    return id;
  }

  /** Deletes the saved indexes; returns the bytes freed. */
  clearIndexCache(): number {
    return this.cacheDir ? clearCache(this.cacheDir) : 0;
  }

  close(docId: number) {
    this.docs.get(docId)?.close();
    this.docs.delete(docId);
    this.remoteSent.delete(docId);
    this.docPaths.delete(docId);
    for (const k of this.progressAt.keys()) if (k.startsWith(`${docId}:`)) this.progressAt.delete(k);
    this.pruneRemote();
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
  /** The timeline stacked by a field; a newer call for the same document cancels the previous one. */
  async histogramBy(docId: number, field: string, buckets: number) {
    const gen = (this.groupsGen.get(docId) ?? 0) + 1;
    this.groupsGen.set(docId, gen);
    return this.doc(docId).histogramBy(field, buckets, () => this.groupsGen.get(docId) !== gen || !this.docs.has(docId));
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
    // The other documents over these files were re-parsed too.
    for (const [id, other] of this.docs) {
      if (other === d || other.set !== d.set) continue;
      await other.sync();
      this.emit({ type: 'doc-updated', docId: id, info: other.info(), reset: true });
    }
    return info;
  }

  /**
   * Tail: picks up appended or rotated data for every open document. Documents over the same files (duplicated tabs,
   * split panes) share one set, which is polled once; each document then syncs with what the poll found.
   */
  private async poll() {
    if (this.polling) return;
    this.polling = true;
    try {
      const failed = new Map<SourceSet, Error>();
      for (const set of new Set([...this.docs.values()].map((d) => d.set))) {
        try {
          await set.refresh();
        } catch (e) {
          failed.set(set, e as Error);
        }
      }
      for (const [id, d] of this.docs) {
        if (d.indexing) continue;
        const err = failed.get(d.set);
        try {
          if (err) throw err;
          if (await d.sync()) this.emit({ type: 'doc-updated', docId: id, info: d.info(), reset: false });
          else this.pushRemoteState(id, d);
        } catch (e) {
          this.emit({ type: 'doc-error', docId: id, message: (e as Error).message });
        }
      }
    } finally {
      this.polling = false;
    }
  }

  /** A remote file's connection state changes without new data: the Sources list shows it. */
  private pushRemoteState(id: number, d: LogDocument) {
    if (!d.sources.some((s) => findRemote(s.path))) return;
    const info = d.info();
    const key = info.sources.map((s) => (s.remote ? `${s.remote.state}:${s.remote.attempt}:${s.remote.state === 'copying' ? s.remote.received : ''}` : '')).join('|');
    if (this.remoteSent.get(id) === key) return;
    this.remoteSent.set(id, key);
    this.emit({ type: 'doc-updated', docId: id, info, reset: false });
  }

  dispose() {
    if (this.timer) clearInterval(this.timer);
    for (const id of [...this.docs.keys()]) this.close(id);
    this.scanPool?.close();
    closeAllRemote();
  }
}

export type EngineMethod = Exclude<keyof Engine, 'dispose'>;
