// The timeline stacked by one field instead of by level: the TOP most common values, plus OTHER for the rest when there
// is a rest. Counted per time bucket by the document (in view order, from the scan pool's parsed rows).

import { fieldToken } from './query';
import { inClause } from './fieldPicks';

export const GROUP_TOP = 8;
export const GROUP_OTHER = '(other)';
/** The value of entries that do not have the field. */
export const GROUP_NONE = '(none)';
/** Pseudo-field: the file of each entry in a merged view. */
export const SOURCE_FIELD = '__source';
/** Distinct values tracked one by one; rarer values beyond this count as "other" (thread ids, request ids…). */
const DISTINCT_CAP = 50_000;

export interface GroupedHistogram {
  start: number;
  bucketMs: number;
  field: string;
  keys: string[];
  /** counts[bucket][key] */
  counts: number[][];
  totals: number[];
}

/** The value an entry counts under: first line, trimmed, at most 200 characters; empty is GROUP_NONE. */
export function groupValue(raw: string | undefined): string {
  if (!raw) return GROUP_NONE;
  const nl = raw.indexOf('\n');
  const v = (nl >= 0 ? raw.slice(0, nl) : raw).trim();
  if (!v) return GROUP_NONE;
  return v.length > 200 ? v.slice(0, 200) : v;
}

/** A parsed field by name, matched like the quick filter does: exactly, "timestamp" as "time", then ignoring case. */
export function fieldValue(fields: Record<string, string>, key: string): string {
  if (key in fields) return fields[key];
  const lk = key.toLowerCase();
  if (lk === 'timestamp' && 'time' in fields) return fields.time;
  const k = Object.keys(fields).find((x) => x.toLowerCase() === lk);
  return k === undefined ? '' : fields[k];
}

export class HistogramGroups {
  private ids = new Map<string, number>();
  private totals: number[] = [];
  private counts: Map<number, number>[];
  private overflow = 0;
  private overflowCounts: number[];

  constructor(private start: number, private bucketMs: number, buckets: number) {
    this.counts = Array.from({ length: Math.max(1, buckets) }, () => new Map());
    this.overflowCounts = new Array(Math.max(1, buckets)).fill(0);
  }

  add(time: number, value: string) {
    if (Number.isNaN(time)) return;
    const b = Math.floor((time - this.start) / this.bucketMs);
    if (b < 0 || b >= this.counts.length) return;
    let id = this.ids.get(value);
    if (id === undefined) {
      if (this.ids.size >= DISTINCT_CAP) {
        this.overflow++;
        this.overflowCounts[b]++;
        return;
      }
      id = this.ids.size;
      this.ids.set(value, id);
      this.totals.push(0);
    }
    this.totals[id]++;
    const c = this.counts[b];
    c.set(id, (c.get(id) ?? 0) + 1);
  }

  build(field: string): GroupedHistogram {
    const byCount = [...this.ids].sort((a, b) => this.totals[b[1]] - this.totals[a[1]] || (a[0] < b[0] ? -1 : a[0] > b[0] ? 1 : 0));
    const keep = byCount.slice(0, GROUP_TOP);
    const other = this.overflow > 0 || byCount.length > GROUP_TOP;
    const keys = keep.map(([k]) => k);
    if (other) keys.push(GROUP_OTHER);
    const slot = new Map(keep.map(([, id], i) => [id, i]));
    const totals = new Array(keys.length).fill(0);
    const counts = this.counts.map((bucket, b) => {
      const row = new Array(keys.length).fill(0);
      for (const [id, n] of bucket) {
        const k = slot.get(id) ?? keys.length - 1;
        row[k] += n;
        totals[k] += n;
      }
      if (other) {
        row[keys.length - 1] += this.overflowCounts[b];
        totals[keys.length - 1] += this.overflowCounts[b];
      }
      return row;
    });
    return { start: this.start, bucketMs: this.bucketMs, field, keys, counts, totals };
  }
}

/**
 * The quick-filter clause for a legend item: the value itself, entries without the field for GROUP_NONE, and for
 * GROUP_OTHER everything but the values shown.
 */
export function legendClause(h: GroupedHistogram, index: number): string {
  const key = h.keys[index];
  if (key === GROUP_NONE) return `-exists(${h.field})`;
  if (key !== GROUP_OTHER) return fieldToken(h.field, key, '==');
  const shown = h.keys.filter((k) => k !== GROUP_OTHER && k !== GROUP_NONE);
  const clause = shown.length ? `-${inClause(h.field, shown)}` : '';
  return h.keys.includes(GROUP_NONE) ? `${clause} exists(${h.field})`.trim() : clause;
}
