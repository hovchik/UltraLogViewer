// Background analytics: one pass over every entry of a document (the whole log, not the filtered view) that works out
// levels over time, error bursts, silences, message templates (most common, error-heavy, new, stopped, slow), exceptions,
// field value distributions (with number statistics), durations, IPs, URLs and HTTP methods.
//
// It must never slow down opening files or the UI, so:
// - it waits until no file is being indexed and no filter, search or other interactive scan is running, and pauses
//   (between batches) whenever one starts;
// - the facts the index already has (level, time) are counted in slices on the engine thread, yielding in between;
// - entry texts are read and parsed on at most half of the scan pool's threads, a few batches at a time;
// - snapshots are published while it runs (incremental results), at most a few times a second;
// - a tail append only reads the new entries (the last entry of a file, which the tail re-reads, is taken back out).

import { messageOf, templateFromTokens, templateInput } from './patterns';
import { tokenize } from './tokenizer';
import type { FileSource } from './fileSource';
import type { LogFormat } from './formats';
import {
  AnalyticsBurst,
  AnalyticsField,
  AnalyticsNumeric,
  AnalyticsPattern,
  AnalyticsResult,
  AnalyticsState,
  Level,
  LEVEL_COUNT,
  ValueCount,
} from './types';

// ------------------------------------------------------------ per-entry facts

/** What the scan workers work out for each entry (columnar, one array element per entry). */
export interface AnalyticsRows {
  analytics: true;
  tpl: string[];
  sample: string[];
  /** Parsed fields worth counting (short single-line values), null when none. */
  fields: (Record<string, string> | null)[];
  /** First exception name in the entry (stack traces included), '' when none. */
  exc: string[];
  /** First duration in the message, in ms; NaN when none. */
  dur: number[];
  len: number[];
  lines: number[];
  ip: string[];
  url: string[];
  method: string[];
}

/** Fields not counted: shown elsewhere (level), or every value differs (message, time). */
const SKIP_FIELDS = new Set(['message', 'msg', 'time', 'timestamp', 'level', '@t', '@m', '@mt']);
const MAX_VALUE = 200;

/** First line of a message, at most 300 characters (as entryScan.patternSample). */
function patternSample(msg: string): string {
  const i = msg.indexOf('\n');
  const first = i < 0 ? msg : msg.slice(0, i);
  return first.length > 300 ? first.slice(0, 300) : first;
}
const EXC_RE = /\b(?:[A-Za-z_$][\w$]*\.)*[A-Z][\w$]*(?:Exception|Error|Fault)\b/;
const EXC_SCAN = 8000;
const UNIT_MS: Record<string, number> = { ns: 1e-6, us: 1e-3, µs: 1e-3, ms: 1, s: 1000, sec: 1000, m: 60_000, min: 60_000, h: 3_600_000 };
const UNIT_AFTER = /^ (ns|us|µs|ms|sec|s|min|h)\b/;
const DUR_RE = /^(\d+(?:\.\d+)?)([a-zµ]+)$/;

/** "120ms" -> 120, "1.5s" -> 1500; NaN when not a duration. */
export function durationMs(token: string): number {
  const m = DUR_RE.exec(token);
  if (!m) return NaN;
  const unit = UNIT_MS[m[2]];
  return unit === undefined ? NaN : Number(m[1]) * unit;
}

/** Evaluates the analytics rows op over entry texts (runs on the scan pool). */
export function analyticsEvaluator(fmt: LogFormat): (texts: string[]) => AnalyticsRows {
  return (texts) => {
    const n = texts.length;
    const out: AnalyticsRows = {
      analytics: true,
      tpl: new Array(n),
      sample: new Array(n),
      fields: new Array(n),
      exc: new Array(n),
      dur: new Array(n),
      len: new Array(n),
      lines: new Array(n),
      ip: new Array(n),
      url: new Array(n),
      method: new Array(n),
    };
    for (let i = 0; i < n; i++) {
      const text = texts[i];
      const parsed = fmt.parse(text);
      const msg = messageOf(text, parsed);
      const line = templateInput(msg);
      const tokens = tokenize(line);
      out.tpl[i] = templateFromTokens(line, tokens);
      out.sample[i] = patternSample(msg);
      let dur = NaN, ip = '', url = '', method = '';
      for (const tk of tokens) {
        if (tk.kind === 'duration' && Number.isNaN(dur)) dur = durationMs(line.substr(tk.start, tk.length));
        else if (tk.kind === 'number' && Number.isNaN(dur)) {
          // "took 120 ms": a number followed by a unit
          const u = UNIT_AFTER.exec(line.slice(tk.start + tk.length, tk.start + tk.length + 9));
          if (u) dur = Number(line.substr(tk.start, tk.length)) * UNIT_MS[u[1]];
        }
        else if (tk.kind === 'ip' && !ip) ip = line.substr(tk.start, tk.length).replace(/:\d+$/, '');
        else if (tk.kind === 'url' && !url) url = line.substr(tk.start, tk.length).replace(/[?#].*$/, '');
        else if (tk.kind === 'method' && !method) method = line.substr(tk.start, tk.length);
      }
      let fields: Record<string, string> | null = null;
      for (const k in parsed) {
        if (SKIP_FIELDS.has(k.toLowerCase())) continue;
        const v = parsed[k].trim();
        if (!v || v.length > MAX_VALUE || v.includes('\n')) continue;
        (fields ??= {})[k] = v;
      }
      const em = EXC_RE.exec(text.length > EXC_SCAN ? text.slice(0, EXC_SCAN) : text);
      let nl = 1;
      for (let j = text.indexOf('\n'); j >= 0; j = text.indexOf('\n', j + 1)) nl++;
      out.fields[i] = fields;
      out.exc[i] = em ? em[0] : '';
      out.dur[i] = dur;
      out.len[i] = msg.length;
      out.lines[i] = nl;
      out.ip[i] = ip;
      out.url[i] = url;
      out.method[i] = method;
    }
    return out;
  };
}

// ------------------------------------------------------------------ counting

const PATTERNS_CAP = 20_000;
const VALUES_CAP = 5000;
const FIELDS_CAP = 64;
const RESERVOIR = 2048;
const SPARK_BUCKETS = 48;
const OTHER = '(other)';

/** A value -> count table that drops its rarest values when full (see pruneRare in document.ts). */
class Counter {
  readonly values = new Map<string, number>();
  capped = false;
  add(v: string, sign: number) {
    const x = this.values.get(v);
    if (x !== undefined) {
      if (x + sign <= 0) this.values.delete(v);
      else this.values.set(v, x + sign);
      return;
    }
    if (sign < 0) return; // pruned meanwhile
    if (this.values.size >= VALUES_CAP) {
      for (let limit = 1; this.values.size > VALUES_CAP / 2; limit++) for (const [k, n] of this.values) if (n <= limit) this.values.delete(k);
      this.capped = true;
    }
    this.values.set(v, 1);
  }
  top(n: number): ValueCount[] {
    return topEntries(this.values, n);
  }
}

function topEntries(m: Map<string, number>, n: number): ValueCount[] {
  const out: ValueCount[] = [];
  for (const e of m) {
    if (out.length === n && e[1] <= out[n - 1][1]) continue;
    let i = out.length < n ? out.length : n - 1;
    out[i] = e;
    while (i > 0 && (out[i - 1][1] < out[i][1] || (out[i - 1][1] === out[i][1] && out[i - 1][0] > out[i][0]))) {
      [out[i - 1], out[i]] = [out[i], out[i - 1]];
      i--;
    }
  }
  return out;
}

/** Number statistics with a uniform sample for percentiles (deterministic, so tests are stable). */
class Numbers {
  count = 0;
  sum = 0;
  min = Infinity;
  max = -Infinity;
  private sample: number[] = [];
  private seen = 0;
  private seed = 0x9e3779b9;

  add(x: number) {
    this.count++;
    this.sum += x;
    if (x < this.min) this.min = x;
    if (x > this.max) this.max = x;
    this.seen++;
    if (this.sample.length < RESERVOIR) this.sample.push(x);
    else {
      this.seed = (Math.imul(this.seed, 1664525) + 1013904223) >>> 0;
      const j = this.seed % this.seen;
      if (j < RESERVOIR) this.sample[j] = x;
    }
  }
  remove(x: number) {
    // min, max and the sample keep it: a tail re-read of one entry
    this.count--;
    this.sum -= x;
  }
  result(): AnalyticsNumeric | null {
    if (this.count <= 0 || !this.sample.length) return null;
    const s = [...this.sample].sort((a, b) => a - b);
    const q = (p: number) => s[Math.min(s.length - 1, Math.floor(p * s.length))];
    const lo = this.min, hi = this.max;
    const buckets = 24;
    const width = hi > lo ? (hi - lo) / buckets : 1;
    const counts = new Array(buckets).fill(0);
    for (const x of s) counts[Math.min(buckets - 1, Math.floor((x - lo) / width))]++;
    // scale the sample's shape to the full count
    const f = this.count / s.length;
    return {
      count: this.count,
      min: lo,
      max: hi,
      avg: this.sum / this.count,
      p50: q(0.5),
      p90: q(0.9),
      p99: q(0.99),
      hist: { start: lo, width, counts: counts.map((c) => Math.round(c * f)) },
    };
  }
}

const NUM_RE = /^-?\d+(?:\.\d+)?$/;

interface FieldAcc {
  key: string;
  present: number;
  values: Counter;
  numeric: number;
  nums: Numbers;
}

interface PatternAcc {
  template: string;
  sample: string;
  count: number;
  levels: number[];
  first: number;
  last: number;
  spark: number[];
  durCount: number;
  durSum: number;
  durMax: number;
}

interface ExcAcc {
  name: string;
  count: number;
  first: number;
  last: number;
  levels: number[];
}

/** How a pattern's sparkline buckets are laid out. */
export interface SparkLayout {
  start: number;
  end: number;
  byTime: boolean;
}

/** Counts of the entry-text facts; rows can be added and (for a tail re-read) taken out again. */
export class TextStats {
  scanned = 0;
  multiline = 0;
  lenSum = 0;
  maxLen = 0;
  patterns = new Map<string, PatternAcc>();
  other: PatternAcc | null = null;
  exceptions = new Map<string, ExcAcc>();
  fields = new Map<string, FieldAcc>();
  durations = new Numbers();
  ips = new Counter();
  urls = new Counter();
  methods = new Counter();

  constructor(readonly spark: SparkLayout) {}

  private bucket(time: number, pos: number): number {
    const { start, end, byTime } = this.spark;
    const x = byTime ? time : pos;
    if (!Number.isFinite(x)) return -1;
    const span = end - start;
    if (span <= 0) return 0;
    return Math.max(0, Math.min(SPARK_BUCKETS - 1, Math.floor(((x - start) / span) * SPARK_BUCKETS)));
  }

  /** Adds (sign 1) or takes out (sign -1) row `i` of `rows`; `pos` = its place in the whole log (0..1), for timeless logs. */
  add(rows: AnalyticsRows, i: number, level: Level, time: number, pos: number, sign: 1 | -1) {
    this.scanned += sign;
    const lines = rows.lines[i];
    if (lines > 1) this.multiline += sign;
    this.lenSum += sign * rows.len[i];
    if (sign > 0 && rows.len[i] > this.maxLen) this.maxLen = rows.len[i];
    const timed = !Number.isNaN(time);
    const b = this.bucket(time, pos);

    // template
    const tpl = rows.tpl[i];
    let p = this.patterns.get(tpl);
    if (!p) {
      if (sign < 0) p = this.other ?? undefined;
      else if (this.patterns.size >= PATTERNS_CAP) p = this.other ??= newPattern(OTHER, rows.sample[i]);
      else this.patterns.set(tpl, (p = newPattern(tpl, rows.sample[i])));
    }
    if (p) {
      p.count += sign;
      p.levels[level] += sign;
      if (b >= 0) p.spark[b] += sign;
      if (timed && sign > 0) {
        if (time < p.first) p.first = time;
        if (time > p.last) p.last = time;
      }
      const d = rows.dur[i];
      if (!Number.isNaN(d)) {
        p.durCount += sign;
        p.durSum += sign * d;
        if (sign > 0 && d > p.durMax) p.durMax = d;
      }
    }
    const d = rows.dur[i];
    if (!Number.isNaN(d)) {
      if (sign > 0) this.durations.add(d);
      else this.durations.remove(d);
    }

    // exception
    const ex = rows.exc[i];
    if (ex) {
      let e = this.exceptions.get(ex);
      if (!e && sign > 0 && this.exceptions.size < VALUES_CAP)
        this.exceptions.set(ex, (e = { name: ex, count: 0, first: Infinity, last: -Infinity, levels: new Array(LEVEL_COUNT).fill(0) }));
      if (e) {
        e.count += sign;
        e.levels[level] += sign;
        if (timed && sign > 0) {
          if (time < e.first) e.first = time;
          if (time > e.last) e.last = time;
        }
      }
    }

    // fields
    const f = rows.fields[i];
    if (f) {
      for (const k in f) {
        const lk = k.toLowerCase();
        let acc = this.fields.get(lk);
        if (!acc) {
          if (sign < 0 || this.fields.size >= FIELDS_CAP) continue;
          this.fields.set(lk, (acc = { key: k, present: 0, values: new Counter(), numeric: 0, nums: new Numbers() }));
        }
        const v = f[k];
        acc.present += sign;
        acc.values.add(v, sign);
        if (NUM_RE.test(v)) {
          acc.numeric += sign;
          if (sign > 0) acc.nums.add(Number(v));
          else acc.nums.remove(Number(v));
        }
      }
    }
    if (rows.ip[i]) this.ips.add(rows.ip[i], sign);
    if (rows.url[i]) this.urls.add(rows.url[i], sign);
    if (rows.method[i]) this.methods.add(rows.method[i], sign);
  }
}

function newPattern(template: string, sample: string): PatternAcc {
  return { template, sample, count: 0, levels: new Array(LEVEL_COUNT).fill(0), first: Infinity, last: -Infinity, spark: new Array(SPARK_BUCKETS).fill(0), durCount: 0, durSum: 0, durMax: 0 };
}

// ------------------------------------------------------------- index facts

const NICE_MS = [1, 2, 5, 10, 20, 50, 100, 250, 500, 1e3, 2e3, 5e3, 1e4, 15e3, 3e4, 6e4, 12e4, 3e5, 6e5, 9e5, 18e5, 36e5, 72e5, 108e5, 216e5, 432e5, 864e5, 1728e5, 6048e5, 2592e6, 31536e6];
const TIMELINE_BUCKETS = 120;
const MAX_GAPS = 8;

/** Facts the index answers without reading an entry: levels, times, per-source counts. */
export class IndexStats {
  entries = 0;
  timed = 0;
  levels = new Array(LEVEL_COUNT).fill(0);
  hours: [number, number, number][] = Array.from({ length: 24 }, () => [0, 0, 0] as [number, number, number]);
  weekdays = new Array(7).fill(0);
  timeline: { start: number; bucketMs: number; counts: number[][] } | null = null;
  gaps: { from: number; to: number; ms: number }[] = [];
  private prev = NaN;

  constructor(range: [number, number] | null) {
    if (!range) return;
    const span = Math.max(1, range[1] - range[0]);
    const bucketMs = NICE_MS.find((x) => span / x <= TIMELINE_BUCKETS) ?? Math.ceil(span / TIMELINE_BUCKETS);
    const start = Math.floor(range[0] / bucketMs) * bucketMs;
    const count = Math.floor((range[1] - start) / bucketMs) + 1;
    this.timeline = { start, bucketMs, counts: Array.from({ length: count }, () => new Array(LEVEL_COUNT).fill(0)) };
  }

  add(level: Level, time: number) {
    this.entries++;
    this.levels[level]++;
    if (Number.isNaN(time)) return;
    this.timed++;
    const tl = this.timeline;
    if (tl) {
      const b = Math.floor((time - tl.start) / tl.bucketMs);
      if (b >= 0 && b < tl.counts.length) tl.counts[b][level]++;
    }
    const d = new Date(time);
    const hr = this.hours[d.getHours()];
    hr[0]++;
    if (level === Level.Warn) hr[1]++;
    else if (level >= Level.Error) hr[2]++;
    this.weekdays[d.getDay()]++;
    const gap = time - this.prev;
    if (gap > 0 && (this.gaps.length < MAX_GAPS || gap > this.gaps[this.gaps.length - 1].ms)) {
      this.gaps.push({ from: this.prev, to: time, ms: gap });
      this.gaps.sort((a, b) => b.ms - a.ms);
      if (this.gaps.length > MAX_GAPS) this.gaps.pop();
    }
    if (!(time < this.prev)) this.prev = time;
  }
}

/** Buckets whose errors stand out from the typical bucket (median + 4 MADs, at least 3 errors), merged when adjacent. */
export function findBursts(tl: { start: number; bucketMs: number; counts: number[][] } | null): AnalyticsBurst[] {
  if (!tl) return [];
  const errs = tl.counts.map((c) => c[Level.Error] + c[Level.Fatal]);
  const active = errs.filter((_, i) => tl.counts[i].some((x) => x > 0));
  if (active.length < 4) return [];
  const sorted = [...active].sort((a, b) => a - b);
  const median = sorted[Math.floor(sorted.length / 2)];
  const dev = active.map((x) => Math.abs(x - median)).sort((a, b) => a - b);
  const mad = dev[Math.floor(dev.length / 2)] * 1.4826;
  const mean = active.reduce((a, b) => a + b, 0) / active.length;
  const threshold = Math.max(3, median + 4 * Math.max(mad, 1), mean * 2);
  const out: AnalyticsBurst[] = [];
  let cur: AnalyticsBurst | null = null;
  for (let i = 0; i < errs.length; i++) {
    if (errs[i] < threshold) {
      cur = null;
      continue;
    }
    const total = tl.counts[i].reduce((a, b) => a + b, 0);
    const from = tl.start + i * tl.bucketMs;
    if (cur) {
      cur.to = from + tl.bucketMs;
      cur.errors += errs[i];
      cur.total += total;
    } else out.push((cur = { from, to: from + tl.bucketMs, errors: errs[i], total, factor: 0 }));
  }
  const typical = Math.max(1, median);
  for (const b of out) b.factor = b.errors / Math.max(1, Math.round((b.to - b.from) / tl.bucketMs)) / typical;
  return out.sort((a, b) => b.errors - a.errors).slice(0, 10);
}

// --------------------------------------------------------------- the result

const finite = (x: number) => (Number.isFinite(x) ? x : null);

function patternOut(p: PatternAcc): AnalyticsPattern {
  return {
    template: p.template,
    sample: p.sample,
    count: p.count,
    levels: p.levels,
    first: finite(p.first),
    last: finite(p.last),
    spark: p.spark,
    durCount: p.durCount,
    durAvg: p.durCount ? p.durSum / p.durCount : 0,
    durMax: p.durMax,
  };
}

function topBy<T>(items: Iterable<T>, n: number, score: (x: T) => number): T[] {
  const out: { x: T; s: number }[] = [];
  for (const x of items) {
    const s = score(x);
    if (!(s > 0)) continue;
    if (out.length === n && s <= out[n - 1].s) continue;
    let i = out.length < n ? out.length : n - 1;
    out[i] = { x, s };
    while (i > 0 && out[i - 1].s < out[i].s) {
      [out[i - 1], out[i]] = [out[i], out[i - 1]];
      i--;
    }
  }
  return out.map((o) => o.x);
}

export interface ResultInput {
  state: AnalyticsState;
  error?: string;
  progress: number;
  total: number;
  elapsedMs: number;
  run: number;
  index: IndexStats | null;
  text: TextStats;
  sources: FileSource[];
}

export function buildResult(r: ResultInput): AnalyticsResult {
  const idx = r.index;
  const t = r.text;
  const sources = r.sources.map((s) => {
    const st = s.stats();
    return { name: s.info().displayName, entries: s.entryCount, levels: [...st.levelCounts], timeMin: finite(st.min), timeMax: finite(st.max), size: s.info().size };
  });
  const timeMin = sources.reduce((m, s) => (s.timeMin != null && s.timeMin < m ? s.timeMin : m), Infinity);
  const timeMax = sources.reduce((m, s) => (s.timeMax != null && s.timeMax > m ? s.timeMax : m), -Infinity);
  const entries = sources.reduce((n, s) => n + s.entries, 0);
  const levels = idx ? idx.levels : new Array(LEVEL_COUNT).fill(0).map((_, l) => sources.reduce((n, s) => n + (s.levels[l] ?? 0), 0));
  const span = timeMax - timeMin;
  const tl = idx?.timeline ?? null;
  let peak = 0, peakAt: number | null = null;
  if (tl) {
    tl.counts.forEach((c, i) => {
      const n = c.reduce((a, b) => a + b, 0);
      if (n > peak) {
        peak = n;
        peakAt = tl.start + i * tl.bucketMs;
      }
    });
  }
  const all = [...t.patterns.values()];
  if (t.other) all.push(t.other);
  const errs = (p: PatternAcc) => p.levels[Level.Error] + p.levels[Level.Fatal];
  const lateFrom = timeMin + span * 0.75;
  const earlyTo = timeMin + span * 0.5;
  const timed = span > 0 && Number.isFinite(span);
  const avgInterval = idx && idx.timed > 1 && timed ? span / idx.timed : 0;
  const numeric = (f: FieldAcc) => f.numeric >= Math.max(5, f.present * 0.9) && f.values.values.size > 10;
  const fields: AnalyticsField[] = [...t.fields.values()]
    .filter((f) => f.present > 0)
    .sort((a, b) => b.present - a.present || (a.key < b.key ? -1 : 1))
    .slice(0, 40)
    .map((f) => ({
      key: f.key,
      present: f.present,
      distinct: f.values.values.size,
      distinctCapped: f.values.capped,
      top: f.values.top(12),
      numeric: numeric(f) ? f.nums.result() ?? undefined : undefined,
    }));
  return {
    state: r.state,
    error: r.error,
    progress: r.progress,
    scanned: t.scanned,
    total: r.total,
    elapsedMs: r.elapsedMs,
    run: r.run,
    summary: {
      entries,
      sources: sources.length,
      bytes: sources.reduce((n, s) => n + s.size, 0),
      timed: idx?.timed ?? 0,
      timeMin: finite(timeMin),
      timeMax: finite(timeMax),
      ratePerMin: timed && idx ? (idx.timed / span) * 60_000 : 0,
      peakPerMin: tl ? (peak / tl.bucketMs) * 60_000 : 0,
      peakAt,
      multiline: t.multiline,
      avgLength: t.scanned ? t.lenSum / t.scanned : 0,
      maxLength: t.maxLen,
      patterns: all.length,
      exceptions: t.exceptions.size,
    },
    levels,
    timeline: tl,
    spark: { start: t.spark.start, end: t.spark.end, buckets: SPARK_BUCKETS, byTime: t.spark.byTime },
    hours: idx?.hours ?? [],
    weekdays: idx?.weekdays ?? [],
    bursts: findBursts(tl),
    gaps: (idx?.gaps ?? []).filter((g) => g.ms >= Math.max(1000, avgInterval * 20)),
    sources: sources.map(({ size: _size, ...s }) => s),
    patterns: topBy(all, 100, (p) => p.count).map(patternOut),
    errorPatterns: topBy(all, 30, errs).map(patternOut),
    newPatterns: timed ? topBy(all, 15, (p) => (p.first >= lateFrom && p.template !== OTHER && p.count >= 2 ? p.count : 0)).map(patternOut) : [],
    stoppedPatterns: timed ? topBy(all, 15, (p) => (p.last <= earlyTo && p.template !== OTHER && p.count >= 10 ? p.count : 0)).map(patternOut) : [],
    slowPatterns: topBy(all, 15, (p) => (p.durCount >= 3 && p.template !== OTHER ? p.durSum / p.durCount : 0)).map(patternOut),
    exceptions: topBy(t.exceptions.values(), 30, (e) => e.count).map((e) => ({ name: e.name, count: e.count, first: finite(e.first), last: finite(e.last), levels: e.levels })),
    fields,
    durations: t.durations.result(),
    ips: t.ips.top(15),
    urls: t.urls.top(15),
    methods: t.methods.top(10),
  };
}

// ------------------------------------------------------------------ the job

/** What the job needs of a document (LogDocument). */
export interface AnalyticsDoc {
  readonly sources: FileSource[];
  readonly indexing: boolean;
  baseCount(): number;
  entryAt(base: number): [number, number];
  timeRange(): [number, number] | null;
  onIndexChanged(fn: (firstChanged: number[] | null) => void): () => void;
}

export interface AnalyticsOptions {
  /** True while something the user waits for runs (indexing, filtering, searching…): the job pauses meanwhile. */
  busy: () => boolean;
  /** Receives snapshots while running and the final result. */
  emit: (r: AnalyticsResult) => void;
  /** Entry batches read at once (default 2). */
  concurrency?: number;
  /** Entries per batch. */
  batch?: number;
  /** Least time between published snapshots. */
  emitMs?: number;
  /** Base rows counted per slice of the index pass. */
  slice?: number;
}

const sleep = (ms: number) => new Promise<void>((r) => setTimeout(r, ms));
const tick = () => new Promise<void>((r) => setImmediate(r));
const INDEX_WEIGHT = 0.08;

/** One document's analysis: runs in the background, can be paused, resumed and restarted, follows tail appends. */
export class AnalyticsJob {
  state: AnalyticsState = 'waiting';
  private error?: string;
  private run = 0;
  private gen = 0;
  private text: TextStats = new TextStats({ start: 0, end: 1, byTime: false });
  private index: IndexStats | null = null;
  private indexDone = 0;
  /** Per source: entries read into `text` (in order), and the facts of the last one (a tail re-reads it). */
  private cursor: number[] = [];
  private last: ({ rows: AnalyticsRows; i: number; level: Level; time: number; pos: number } | null)[] = [];
  private startedAt = 0;
  private elapsed = 0;
  private emittedAt = 0;
  private loop: Promise<void> | null = null;
  private dirty = false;
  private reset = true;
  private unlisten: () => void;
  private readonly opts: Required<AnalyticsOptions>;

  constructor(private doc: AnalyticsDoc, opts: AnalyticsOptions) {
    this.opts = { concurrency: 2, batch: 4000, emitMs: 400, slice: 200_000, ...opts };
    this.unlisten = doc.onIndexChanged((first) => this.indexChanged(first));
  }

  /** Starts (or continues) the analysis in the background. */
  start() {
    if (this.state === 'cancelled') return;
    if (this.state === 'paused') this.state = 'waiting';
    this.kick();
  }

  /** Stops reading after the batches in flight; `start` continues where it stopped. */
  pause() {
    if (this.state === 'done' || this.state === 'cancelled') return;
    this.state = 'paused';
    this.gen++;
    this.publish(true);
  }

  /** Throws away what was counted and starts over. */
  restart() {
    if (this.state === 'cancelled') return;
    this.reset = true;
    this.gen++;
    this.state = 'waiting';
    this.kick();
  }

  /** For good (the document closes). */
  cancel() {
    this.state = 'cancelled';
    this.gen++;
    this.unlisten();
  }

  /** The files grew (tail): reads the new entries once the job is idle again. */
  dataChanged() {
    if (this.state === 'cancelled' || this.state === 'paused') return;
    this.dirty = true;
    if (this.state === 'done') {
      this.state = 'waiting';
      this.kick();
    }
  }

  snapshot(): AnalyticsResult {
    return this.result();
  }

  private indexChanged(first: number[] | null) {
    if (this.state === 'cancelled') return;
    this.gen++; // batches in flight may be of re-indexed entries
    if (!first || first.length !== this.cursor.length) {
      this.reset = true;
    } else {
      for (let s = 0; s < first.length && !this.reset; s++) {
        const keep = first[s];
        const c = this.cursor[s];
        if (keep >= c) continue;
        const last = this.last[s];
        if (keep === c - 1 && last) {
          this.text.add(last.rows, last.i, last.level, last.time, last.pos, -1);
          this.cursor[s] = keep;
          this.last[s] = null;
        } else this.reset = true;
      }
    }
    this.dirty = true;
    if (this.state !== 'paused') {
      this.state = 'waiting';
      this.kick();
    }
  }

  private kick() {
    if (this.loop) return;
    this.loop = this.runLoop().finally(() => {
      this.loop = null;
      // changed while finishing
      if ((this.state === 'waiting' || this.state === 'running') && (this.dirty || this.reset)) this.kick();
    });
  }

  private total() {
    return this.doc.sources.reduce((n, s) => n + s.entryCount, 0);
  }

  private result(): AnalyticsResult {
    const total = this.total();
    const textDone = total ? Math.min(1, this.text.scanned / total) : 1;
    const idxDone = this.indexDone;
    const progress = this.state === 'done' ? 1 : INDEX_WEIGHT * idxDone + (1 - INDEX_WEIGHT) * textDone;
    const running = this.state === 'running' ? Date.now() - this.startedAt : 0;
    return buildResult({
      state: this.state,
      error: this.error,
      progress,
      total,
      elapsedMs: this.elapsed + running,
      run: this.run,
      index: this.index,
      text: this.text,
      sources: this.doc.sources,
    });
  }

  private publish(force = false) {
    const now = Date.now();
    if (!force && now - this.emittedAt < this.opts.emitMs) return;
    this.emittedAt = now;
    this.opts.emit(this.result());
  }

  /** Waits while the document indexes or the user waits for something else. False when superseded. */
  private async idle(gen: number): Promise<boolean> {
    let waited = false;
    while (gen === this.gen && (this.doc.indexing || this.opts.busy())) {
      if (!waited && this.state === 'running') {
        this.elapsed += Date.now() - this.startedAt;
        this.state = 'waiting';
        this.publish(true);
      }
      waited = true;
      await sleep(120);
    }
    if (gen !== this.gen) return false;
    if (waited || this.state !== 'running') {
      this.state = 'running';
      this.startedAt = Date.now();
    }
    return true;
  }

  private async runLoop() {
    while (this.state === 'waiting' || this.state === 'running') {
      const gen = ++this.gen;
      this.dirty = false;
      try {
        if (!(await this.idle(gen))) {
          const st = this.state as AnalyticsState;
          if (st === 'paused' || st === 'cancelled') return;
          continue;
        }
        if (this.reset) this.begin();
        const fresh = await this.indexPass(gen);
        if (!fresh) continue;
        if (!(await this.textPass(gen))) continue;
        if (gen !== this.gen) continue;
        if (this.dirty || this.reset) continue;
        this.elapsed += Date.now() - this.startedAt;
        this.state = 'done';
        this.publish(true);
        return;
      } catch (e) {
        if (gen !== this.gen) continue;
        this.state = 'error';
        this.error = (e as Error).message;
        this.publish(true);
        return;
      }
    }
  }

  /** Starts over: new sparkline layout from the current time range. */
  private begin() {
    this.reset = false;
    this.run++;
    this.elapsed = 0;
    this.startedAt = Date.now();
    const range = this.doc.timeRange();
    this.text = new TextStats(range ? { start: range[0], end: range[1], byTime: true } : { start: 0, end: 1, byTime: false });
    this.cursor = this.doc.sources.map(() => 0);
    this.last = this.doc.sources.map(() => null);
    this.index = null;
    this.indexDone = 0;
    this.publish(true);
  }

  /** Counts the index facts over every base row (again: they are cheap and the merge order may have changed). */
  private async indexPass(gen: number): Promise<boolean> {
    const range = this.doc.timeRange();
    // the files grew well past the sparkline layout: start over so the sparklines cover the whole span
    const sp = this.text.spark;
    if (sp.byTime && range && range[1] > sp.end + (sp.end - sp.start) * 0.25 + 60_000) {
      this.reset = true;
      return false;
    }
    const stats = new IndexStats(range);
    const n = this.doc.baseCount();
    const sources = this.doc.sources;
    const step = this.opts.slice;
    for (let b = 0; b < n; ) {
      if (!(await this.idle(gen))) return false;
      const end = Math.min(n, b + step);
      for (; b < end; b++) {
        const [s, e] = this.doc.entryAt(b);
        const src = sources[s];
        stats.add(src.level(e), src.time(e));
      }
      if (!this.index) this.indexDone = b / n;
      this.publish();
      await tick();
    }
    if (gen !== this.gen) return false;
    this.index = stats;
    this.indexDone = 1;
    this.publish();
    return true;
  }

  /** Reads the entries not read yet, a few batches at a time, and counts them. */
  private async textPass(gen: number): Promise<boolean> {
    const sources = this.doc.sources;
    const total = Math.max(1, this.total());
    const before: number[] = []; // entries in the sources before each one, for timeless sparklines
    sources.reduce((n, s, i) => ((before[i] = n), n + s.entryCount), 0);
    const plan: { s: number; from: number; to: number }[] = [];
    sources.forEach((src, s) => {
      for (let e = this.cursor[s] ?? 0; e < src.entryCount; e += this.opts.batch) plan.push({ s, from: e, to: Math.min(src.entryCount, e + this.opts.batch) });
    });
    const inFlight: { s: number; from: number; to: number; result: Promise<AnalyticsRows> }[] = [];
    let next = 0;
    try {
      while (next < plan.length || inFlight.length) {
        if (!(await this.idle(gen))) return false;
        while (next < plan.length && inFlight.length < this.opts.concurrency) {
          const p = plan[next++];
          const entries: number[] = [];
          for (let e = p.from; e < p.to; e++) entries.push(e);
          inFlight.push({ ...p, result: sources[p.s].evalRows(entries, { kind: 'analytics' }) as Promise<AnalyticsRows> });
        }
        const job = inFlight.shift()!;
        const rows = await job.result;
        if (gen !== this.gen) return false;
        if (job.from !== this.cursor[job.s]) continue; // (a tail rewound this source)
        const src = sources[job.s];
        const count = Math.min(rows.tpl.length, src.entryCount - job.from);
        for (let i = 0; i < count; i++) {
          const e = job.from + i;
          const level = src.level(e), time = src.time(e);
          const pos = (before[job.s] + e) / total;
          this.text.add(rows, i, level, time, pos, 1);
          if (i === count - 1) this.last[job.s] = { rows, i, level, time, pos };
        }
        this.cursor[job.s] = job.from + count;
        this.publish();
        await tick();
      }
    } finally {
      for (const j of inFlight) j.result.catch(() => undefined);
    }
    return true;
  }
}
