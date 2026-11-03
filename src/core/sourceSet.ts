// The indexed files behind one or more documents. A tab and its duplicates or split panes are documents over the same
// set: each has its own filter, view and search, while the files are indexed, merged and tailed once. The set is
// reference counted by the documents attached to it and closes its files when the last one is closed.
//
// Anything that changes the index (tail refresh, re-parsing, adding files) runs through exclusive(): it takes the set's
// gate and then every attached document's queue, in attach order, so it never runs while a document is reading entries
// for a filter, a group or a search (those await the scan pool, so they do interleave with other work). The documents
// are told what changed and bring their verdicts up to date in their own queue.

import { FileSource } from './fileSource';
import { GrowU32, GrowU8 } from './grow';

/** The base-row order of a merged view: base position -> (source, entry), and entry -> base position per source. */
export interface Merge {
  src: GrowU8;
  ent: GrowU32;
  basePos: Uint32Array[];
}

/** What a set needs from a document attached to it. */
export interface SetMember {
  /** Holds the document's queue until the returned function is called. */
  lock(): Promise<() => void>;
  /** The index changed: first changed entry per source, or null when everything must be filtered again. */
  sourcesChanged(firstChanged: number[] | null): void;
}

export class SourceSet {
  readonly sources: FileSource[] = [];
  merge: Merge | null = null;
  indexing = false;
  progress = 0;
  closed = false;
  /**
   * Counts the changes to the index. A document remembers the version it last synced to, so one attached while a
   * change was running (and so not told about it) still notices on its next sync.
   */
  version = 0;
  private members: SetMember[] = [];
  private gate: Promise<unknown> = Promise.resolve();

  /** Documents (tabs, split panes) reading these files. */
  get refCount() {
    return this.members.length;
  }

  attach(m: SetMember) {
    if (this.closed) throw new Error('These files are closed');
    this.members.push(m);
  }

  /** Detaches a document; the last one out closes the files. */
  detach(m: SetMember) {
    const i = this.members.indexOf(m);
    if (i < 0) return;
    this.members.splice(i, 1);
    if (this.members.length) return;
    this.closed = true;
    for (const s of this.sources) s.close();
  }

  /**
   * Runs `change` with the index to itself: no attached document is scanning entries meanwhile. Then each document gets
   * `apply` (still under the lock), which tells it what changed.
   */
  exclusive<T>(change: () => Promise<T>, apply: (m: SetMember, r: T) => void): Promise<T> {
    const run = this.gate.then(async () => {
      const members = [...this.members];
      const releases: (() => void)[] = [];
      try {
        for (const m of members) releases.push(await m.lock());
        const r = await change();
        this.version++;
        for (const m of members) apply(m, r);
        return r;
      } finally {
        for (const release of releases) release();
      }
    });
    this.gate = run.catch(() => undefined);
    return run;
  }

  /** k-way merge of all sources by timestamp (stable: ties keep source order). */
  rebuildMerge() {
    const sources = this.sources;
    if (sources.length < 2) {
      this.merge = null;
      return;
    }
    const total = sources.reduce((n, s) => n + s.entryCount, 0);
    const ms = new GrowU8(total + 1);
    const me = new GrowU32(total + 1);
    const cur = sources.map(() => 0);
    const basePos = sources.map((s) => new Uint32Array(s.entryCount));
    const k = sources.length;
    for (let n = 0; n < total; n++) {
      let best = -1;
      let bestT = Infinity;
      for (let s = 0; s < k; s++) {
        const src = sources[s];
        if (cur[s] >= src.entryCount) continue;
        let t = src.time(cur[s]);
        if (Number.isNaN(t)) t = -Infinity; // entries before the first timestamp go first
        if (best < 0 || t < bestT) {
          best = s;
          bestT = t;
        }
      }
      basePos[best][cur[best]] = n;
      ms.push(best);
      me.push(cur[best]++);
    }
    this.merge = { src: ms, ent: me, basePos };
  }

  /**
   * Polls every file once for appended or rotated data, for all documents over them. Returns the first changed entry per
   * source (0 after a rotation, the entry count when unchanged), or null when nothing changed. Each document picks the
   * change up with LogDocument.sync().
   */
  refresh(): Promise<number[] | null> {
    if (this.indexing || this.closed) return Promise.resolve(null);
    return this.exclusive(
      async () => {
        if (this.closed) return null;
        let changed = false;
        const first: number[] = [];
        for (const s of this.sources) {
          const r = await s.refresh();
          first.push(!r.changed ? s.entryCount : r.reset ? 0 : r.firstChangedEntry);
          changed ||= r.changed;
        }
        if (!changed) return null;
        this.rebuildMerge();
        return first;
      },
      (m, first) => first && m.sourcesChanged(first),
    );
  }

  /** Re-parses every file with another format; every document filters again from scratch. */
  reformat(fn: (s: FileSource) => Promise<void>): Promise<void> {
    return this.exclusive(
      async () => {
        for (const s of this.sources) await fn(s);
        this.rebuildMerge();
      },
      (m) => m.sourcesChanged(null),
    );
  }

  /** After files were added or left out: rebuilds the merge and makes every document filter again. */
  rebuilt(): Promise<void> {
    return this.exclusive(
      async () => this.rebuildMerge(),
      (m) => m.sourcesChanged(null),
    );
  }
}
