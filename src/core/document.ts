// A LogDocument is what a tab shows: one file, or several files merged into one timeline.
//
// Row model:
//   base rows  = every entry of every source (merged by timestamp when there are several sources)
//   view rows  = base rows that pass the current filter (plus context rows), ascending base order
// Filtering and search work per source in file order (sequential reads) and produce bitmaps,
// which are then walked in base order. That keeps merged views fast too.

import * as fs from 'node:fs';
import { FileSource, SourceOptions } from './fileSource';
import { GrowU32, GrowU8, lowerBound } from './grow';
import { compileMatcher, compileQuery, Matcher } from './query';
import { CustomFormat, DocInfo, FilterSpec, Histogram, Level, LEVEL_COUNT, RowData, rowKey, SearchResult, TextPattern } from './types';

export type Progress = (phase: string, fraction: number) => void;

const MAX_SEARCH_ROWS = 2_000_000;
/** Level names used when matching `level:` queries (WARNING also matches "warn"). */
const LEVEL_MATCH_NAMES = ['NONE', 'TRACE', 'DEBUG', 'INFO', 'WARNING', 'ERROR', 'FATAL'];

export class LogDocument {
  readonly sources: FileSource[] = [];
  private mergedSrc: GrowU8 | null = null;
  private mergedEnt: GrowU32 | null = null;
  /** For merged docs: entry -> base position, per source. */
  private basePos: Uint32Array[] = [];
  private filter: FilterSpec | null = null;
  private view: Uint32Array | null = null;
  private viewContext: Uint8Array | null = null;
  /** Per-source filter verdicts for already evaluated entries: 1 = match. */
  private verdict: GrowU8[] = [];
  private filterGen = 0;
  /** Serialises everything that mutates verdicts/view (filtering, tail refresh, re-parsing). */
  private queue: Promise<unknown> = Promise.resolve();
  indexing = false;
  progress = 0;

  constructor(readonly id: number, readonly title: string) {}

  async addSource(path: string, opts: SourceOptions, onProgress?: Progress): Promise<FileSource> {
    const src = new FileSource(this.sources.length, path, opts);
    this.sources.push(src);
    this.indexing = true;
    try {
      await src.open((f) => {
        this.progress = f;
        onProgress?.('index', f);
      });
    } catch (e) {
      this.sources.pop();
      throw e;
    } finally {
      this.indexing = false;
    }
    this.rebuildBase();
    if (this.filter) await this.applyFilter(this.filter, false, onProgress);
    return src;
  }

  private serial<T>(fn: () => Promise<T>): Promise<T> {
    const run = this.queue.then(fn, fn);
    this.queue = run.catch(() => undefined);
    return run;
  }

  get merged() {
    return this.sources.length > 1;
  }

  baseCount(): number {
    return this.mergedEnt ? this.mergedEnt.length : this.sources[0]?.entryCount ?? 0;
  }

  viewCount(): number {
    return this.view ? this.view.length : this.baseCount();
  }

  /** base position -> [source, entry] */
  private ref(base: number): [number, number] {
    return this.mergedEnt ? [this.mergedSrc!.get(base), this.mergedEnt.get(base)] : [0, base];
  }

  private baseOf(row: number): number {
    return this.view ? this.view[row] : row;
  }

  private baseForEntry(src: number, entry: number): number {
    return this.mergedEnt ? this.basePos[src][entry] : entry;
  }

  /** k-way merge of all sources by timestamp (stable: ties keep source order). */
  private rebuildBase() {
    if (this.sources.length < 2) {
      this.mergedSrc = this.mergedEnt = null;
      this.basePos = [];
      return;
    }
    const total = this.sources.reduce((n, s) => n + s.entryCount, 0);
    const ms = new GrowU8(total + 1);
    const me = new GrowU32(total + 1);
    const cur = this.sources.map(() => 0);
    this.basePos = this.sources.map((s) => new Uint32Array(s.entryCount));
    const k = this.sources.length;
    for (let n = 0; n < total; n++) {
      let best = -1;
      let bestT = Infinity;
      for (let s = 0; s < k; s++) {
        const src = this.sources[s];
        if (cur[s] >= src.entryCount) continue;
        let t = src.time(cur[s]);
        if (Number.isNaN(t)) t = -Infinity; // entries before the first timestamp go first
        if (best < 0 || t < bestT) {
          best = s;
          bestT = t;
        }
      }
      this.basePos[best][cur[best]] = n;
      ms.push(best);
      me.push(cur[best]++);
    }
    this.mergedSrc = ms;
    this.mergedEnt = me;
  }

  // ------------------------------------------------------------------ refresh

  /** Polls all sources for appended/rotated data. Returns true if anything changed. */
  refresh(): Promise<boolean> {
    return this.serial(() => this.refreshNow());
  }

  private async refreshNow(): Promise<boolean> {
    let changed = false;
    for (const [i, s] of this.sources.entries()) {
      const r = await s.refresh();
      if (!r.changed) continue;
      changed = true;
      const v = this.verdict[i];
      if (v) v.truncate(r.reset ? 0 : r.firstChangedEntry);
    }
    if (!changed) return false;
    this.rebuildBase();
    if (this.filter) await this.applyFilterNow(this.filter, true, this.filterGen);
    return true;
  }

  // ------------------------------------------------------------------- filter

  private static isTrivial(f: FilterSpec): boolean {
    return (
      f.levels.every(Boolean) &&
      !f.rules.some((r) => r.enabled && r.text) &&
      !(f.query && f.query.trim()) &&
      f.timeFrom == null &&
      f.timeTo == null &&
      !f.bookmarksOnly
    );
  }

  /**
   * Applies a filter. `incremental` re-uses verdicts of entries evaluated before (tail mode).
   * Returns false if superseded by a newer call.
   */
  applyFilter(spec: FilterSpec | null, incremental = false, onProgress?: Progress): Promise<boolean> {
    const gen = ++this.filterGen;
    return this.serial(() => this.applyFilterNow(spec, incremental, gen, onProgress));
  }

  private async applyFilterNow(spec: FilterSpec | null, incremental: boolean, gen: number, onProgress?: Progress): Promise<boolean> {
    if (gen !== this.filterGen) return false;
    if (!incremental) this.verdict = [];
    this.filter = spec;
    if (!spec || LogDocument.isTrivial(spec)) {
      this.view = this.viewContext = null;
      this.verdict = [];
      return true;
    }
    const includes = spec.rules.filter((r) => r.enabled && r.text && r.mode === 'include').map((r) => ({ m: compileMatcher(r), col: r.column }));
    const excludes = spec.rules.filter((r) => r.enabled && r.text && r.mode === 'exclude').map((r) => ({ m: compileMatcher(r), col: r.column }));
    const query = spec.query?.trim() ? compileQuery(spec.query, !!spec.queryCaseSensitive) : null;
    const needsText = includes.length > 0 || excludes.length > 0 || !!query;
    const bookmarks = spec.bookmarksOnly ? new Set(spec.bookmarks ?? []) : null;
    const from = spec.timeFrom ?? -Infinity;
    const to = spec.timeTo ?? Infinity;
    const hasTime = spec.timeFrom != null || spec.timeTo != null;

    const total = this.sources.reduce((n, s) => n + s.entryCount, 0);
    let done = 0;
    for (const [si, src] of this.sources.entries()) {
      const v = (this.verdict[si] ??= new GrowU8(Math.max(16, src.entryCount)));
      const start = v.length;
      done += start;
      const cheap = (e: number) => {
        if (bookmarks) return bookmarks.has(rowKey(si, e));
        if (!spec.levels[src.level(e)]) return false;
        if (hasTime) {
          const t = src.time(e);
          if (!(t >= from && t <= to)) return false;
        }
        return true;
      };
      if (!needsText || bookmarks) {
        for (let e = start; e < src.entryCount; e++) v.push(cheap(e) ? 1 : 0);
        done += src.entryCount - start;
        continue;
      }
      const fmt = src.format;
      const test = (m: Matcher, col: string | undefined, text: string, field: (k: string) => string) => (col ? m(field(col)) : m(text));
      await src.forEachEntryBatch(start, src.entryCount, (first, texts) => {
        if (gen !== this.filterGen) return false;
        for (let i = 0; i < texts.length; i++) {
          const e = first + i;
          let ok = cheap(e);
          if (ok) {
            const text = texts[i];
            let parsed: Record<string, string> | null = null;
            const field = (k: string): string => {
              // The level is already indexed: answer without parsing the entry.
              if (k === 'level') return LEVEL_MATCH_NAMES[src.level(e)];
              const f = (parsed ??= fmt.parse(text));
              if (k in f) return f[k];
              const lk = k.toLowerCase();
              const alias = lk === 'timestamp' ? 'time' : lk;
              return f[alias] ?? f[Object.keys(f).find((x) => x.toLowerCase() === lk) ?? ''] ?? '';
            };
            if (includes.length && !includes.some((r) => test(r.m, r.col, text, field))) ok = false;
            else if (excludes.some((r) => test(r.m, r.col, text, field))) ok = false;
            else if (query && !query(text, field)) ok = false;
          }
          v.push(ok ? 1 : 0);
        }
        done += texts.length;
        onProgress?.('filter', total ? done / total : 1);
      });
      if (gen !== this.filterGen) return false;
    }
    this.buildView(spec.context ?? 0);
    return true;
  }

  private buildView(context: number) {
    const n = this.baseCount();
    const hit = (b: number) => {
      const [s, e] = this.ref(b);
      const v = this.verdict[s];
      return !!v && e < v.length && v.get(e) === 1;
    };
    const out = new GrowU32(1024);
    const ctx = new GrowU8(1024);
    if (context <= 0) {
      for (let b = 0; b < n; b++) if (hit(b)) out.push(b);
    } else {
      let lastAdded = -1;
      for (let b = 0; b < n; b++) {
        if (!hit(b)) continue;
        const lo = Math.max(lastAdded + 1, b - context);
        for (let c = lo; c < b; c++) {
          out.push(c);
          ctx.push(1);
        }
        out.push(b);
        ctx.push(0);
        lastAdded = b;
        // trailing context
        let c = b + 1;
        for (; c <= Math.min(n - 1, b + context); c++) {
          if (hit(c)) break;
          out.push(c);
          ctx.push(1);
          lastAdded = c;
        }
      }
    }
    this.view = out.view().slice();
    this.viewContext = context > 0 ? ctx.view().slice() : null;
  }

  // --------------------------------------------------------------------- rows

  getRows(start: number, count: number): RowData[] {
    const end = Math.min(this.viewCount(), start + count);
    const rows: RowData[] = [];
    let r = start;
    while (r < end) {
      // Group consecutive rows from the same source with consecutive entries into one read.
      const [s, e0] = this.ref(this.baseOf(r));
      let len = 1;
      while (r + len < end) {
        const [s2, e2] = this.ref(this.baseOf(r + len));
        if (s2 !== s || e2 !== e0 + len) break;
        len++;
      }
      const src = this.sources[s];
      const texts = src.readEntries(e0, e0 + len);
      for (let i = 0; i < len; i++) {
        const e = e0 + i;
        const [l0, l1] = src.entryLines(e);
        const t = src.time(e);
        rows.push({
          row: r + i,
          key: rowKey(s, e),
          source: s,
          entry: e,
          line: l0 + 1,
          lineCount: l1 - l0 + 1,
          level: src.level(e),
          time: Number.isNaN(t) ? null : t,
          fields: src.format.parse(texts[i] ?? ''),
          text: texts[i] ?? '',
          context: this.viewContext ? this.viewContext[r + i] === 1 : undefined,
        });
      }
      r += len;
    }
    return rows;
  }

  // ------------------------------------------------------------------- search

  /** Finds all view rows whose entry text matches. */
  async search(p: TextPattern, onProgress?: Progress, cancelled: () => boolean = () => false): Promise<SearchResult> {
    if (!p.text) return { rows: [], total: 0, truncated: false };
    const m = compileMatcher(p);
    const hits = this.sources.map((s) => new Uint8Array(s.entryCount));
    const total = this.sources.reduce((n, s) => n + s.entryCount, 0);
    let done = 0;
    for (const [si, src] of this.sources.entries()) {
      const inView = this.verdict[si];
      const ctxAll = !!this.viewContext; // context rows may come from non-matching entries
      await src.forEachEntryBatch(0, src.entryCount, (first, texts) => {
        if (cancelled()) return false;
        for (let i = 0; i < texts.length; i++) {
          const e = first + i;
          if (this.view && !ctxAll && inView && (e >= inView.length || inView.get(e) !== 1)) continue;
          if (m(texts[i])) hits[si][e] = 1;
        }
        done += texts.length;
        onProgress?.('search', total ? done / total : 1);
      });
    }
    if (cancelled()) return { rows: [], total: 0, truncated: false, cancelled: true };
    const rows: number[] = [];
    const n = this.viewCount();
    let count = 0;
    for (let r = 0; r < n; r++) {
      const [s, e] = this.ref(this.baseOf(r));
      if (hits[s][e]) {
        count++;
        if (rows.length < MAX_SEARCH_ROWS) rows.push(r);
      }
    }
    return { rows, total: count, truncated: count > rows.length };
  }

  // -------------------------------------------------------------- navigation

  /** View row for a base position (nearest following row if filtered out). */
  private rowForBase(base: number): number {
    if (!this.view) return base;
    const v = this.view;
    return Math.min(v.length - 1, lowerBound(0, v.length, (i) => v[i] >= base));
  }

  rowForLine(source: number, line1: number): number {
    const src = this.sources[source];
    if (!src || !src.entryCount) return 0;
    const e = src.entryForLine(Math.max(0, line1 - 1));
    return this.rowForBase(this.baseForEntry(source, e));
  }

  rowForKey(key: string): number | null {
    const [s, e] = key.split(':').map(Number);
    if (!this.sources[s] || e >= this.sources[s].entryCount) return null;
    const base = this.baseForEntry(s, e);
    const row = this.rowForBase(base);
    return this.baseOf(row) === base ? row : null;
  }

  private rowTime(r: number): number {
    const [s, e] = this.ref(this.baseOf(r));
    return this.sources[s].time(e);
  }

  /** First view row at or after time t. */
  rowForTime(t: number): number {
    const n = this.viewCount();
    // Times are mostly ascending; binary search then fix up with a short scan.
    let r = lowerBound(0, n, (i) => {
      const x = this.rowTime(i);
      return !Number.isNaN(x) && x >= t;
    });
    if (r >= n) r = n - 1;
    return Math.max(0, r);
  }

  /** Next/previous view row (from `from`, exclusive) with level >= minLevel. */
  findLevel(from: number, dir: 1 | -1, minLevel: Level): number | null {
    const n = this.viewCount();
    for (let r = from + dir; r >= 0 && r < n; r += dir) {
      const [s, e] = this.ref(this.baseOf(r));
      const l = this.sources[s].level(e);
      if (l >= minLevel && (minLevel !== Level.Warn || l === Level.Warn)) return r;
    }
    return null;
  }

  // ---------------------------------------------------------------- overview

  levelCounts(): number[] {
    const counts = new Array(LEVEL_COUNT).fill(0);
    for (const s of this.sources) {
      const lv = s.entryLevel.view();
      for (let i = 0; i < lv.length; i++) counts[lv[i]]++;
    }
    return counts;
  }

  timeRange(): [number, number] | null {
    let lo = Infinity, hi = -Infinity;
    for (const s of this.sources) {
      const t = s.entryTime.view();
      for (let i = 0; i < t.length; i++) {
        const x = t[i];
        if (x < lo) lo = x;
        if (x > hi) hi = x;
      }
    }
    return lo <= hi ? [lo, hi] : null;
  }

  /**
   * Per-bucket flags over the view rows for the overview ruler next to the scrollbar:
   * bit 1 = has a warning, bit 2 = has an error or fatal entry.
   */
  overview(buckets: number): number[] {
    const n = this.viewCount();
    const out = new Array(Math.max(1, buckets)).fill(0);
    if (!n) return out;
    for (let r = 0; r < n; r++) {
      const [s, e] = this.ref(this.baseOf(r));
      const l = this.sources[s].level(e);
      if (l < Level.Warn) continue;
      out[Math.floor((r / n) * buckets)] |= l === Level.Warn ? 1 : 2;
    }
    return out;
  }

  histogram(buckets: number): Histogram | null {
    const n = this.viewCount();
    let lo = Infinity, hi = -Infinity;
    for (let r = 0; r < n; r++) {
      const t = this.rowTime(r);
      if (t < lo) lo = t;
      if (t > hi) hi = t;
    }
    if (!(lo <= hi)) return null;
    const span = Math.max(1, hi - lo);
    const nice = [1, 2, 5, 10, 20, 50, 100, 250, 500, 1e3, 2e3, 5e3, 1e4, 15e3, 3e4, 6e4, 12e4, 3e5, 6e5, 9e5, 18e5, 36e5, 72e5, 108e5, 216e5, 432e5, 864e5, 1728e5, 6048e5, 2592e6, 31536e6];
    const bucketMs = nice.find((x) => span / x <= buckets) ?? Math.ceil(span / buckets);
    const start = Math.floor(lo / bucketMs) * bucketMs;
    const count = Math.floor((hi - start) / bucketMs) + 1;
    const counts: [number, number, number][] = Array.from({ length: count }, () => [0, 0, 0]);
    for (let r = 0; r < n; r++) {
      const [s, e] = this.ref(this.baseOf(r));
      const t = this.sources[s].time(e);
      if (Number.isNaN(t)) continue;
      const l = this.sources[s].level(e);
      counts[Math.floor((t - start) / bucketMs)][l >= Level.Error ? 2 : l === Level.Warn ? 1 : 0]++;
    }
    return { start, bucketMs, counts };
  }

  info(): DocInfo {
    const range = this.timeRange();
    const first = this.sources[0];
    const columns = first?.format ? [...first.format.columns] : [];
    if (this.merged && columns.length) columns.unshift({ key: '__source', title: 'Source', width: 110 });
    return {
      id: this.id,
      title: this.title,
      sources: this.sources.map((s) => s.info()),
      columns,
      total: this.baseCount(),
      viewCount: this.viewCount(),
      levelCounts: this.levelCounts(),
      indexing: this.indexing,
      progress: this.progress,
      timeMin: range ? range[0] : null,
      timeMax: range ? range[1] : null,
      merged: this.merged,
    };
  }

  // ------------------------------------------------------------------- export

  async export(file: string, format: 'text' | 'csv' | 'jsonl', onProgress?: Progress): Promise<number> {
    const out = fs.createWriteStream(file, { encoding: 'utf-8' });
    const n = this.viewCount();
    const cols = this.info().columns.filter((c) => c.key !== '__source');
    const q = (s: string) => `"${s.replace(/"/g, '""')}"`;
    const write = (s: string) => (out.write(s) ? Promise.resolve() : new Promise<void>((r) => out.once('drain', () => r())));
    if (format === 'csv') await write((this.merged ? ['source'] : []).concat(['line', ...cols.map((c) => c.title)]).map(q).join(',') + '\n');
    for (let r = 0; r < n; r += 2000) {
      const rows = this.getRows(r, 2000);
      let chunk = '';
      for (const row of rows) {
        const srcName = this.sources[row.source].info().displayName;
        if (format === 'text') chunk += row.text + '\n';
        else if (format === 'csv') chunk += (this.merged ? [srcName] : []).concat([String(row.line), ...cols.map((c) => row.fields[c.key] ?? '')]).map(q).join(',') + '\n';
        else chunk += JSON.stringify({ ...(this.merged ? { source: srcName } : {}), line: row.line, ...row.fields }) + '\n';
      }
      await write(chunk);
      onProgress?.('export', Math.min(1, (r + 2000) / n));
    }
    await new Promise<void>((res, rej) => out.end((e?: Error | null) => (e ? rej(e) : res())));
    return n;
  }

  reformat(formatId: string | undefined, customFormats?: CustomFormat[]): Promise<void> {
    return this.serial(async () => {
      for (const s of this.sources) await s.setFormat(formatId, customFormats);
      this.verdict = [];
      this.rebuildBase();
      if (this.filter) await this.applyFilterNow(this.filter, false, this.filterGen);
    });
  }

  /** Stable key of the entry containing a 1-based line (used to restore bookmarks). */
  keyForLine(source: number, line1: number): string | null {
    const src = this.sources[source];
    if (!src || !src.entryCount || line1 < 1 || line1 > src.lineCount) return null;
    return rowKey(source, src.entryForLine(line1 - 1));
  }

  close() {
    this.filterGen++;
    for (const s of this.sources) s.close();
  }
}
