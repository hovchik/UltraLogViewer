// Shared types between the engine (worker thread) and the renderer.

import type { RemoteStatus } from './remoteTarget';

/** Severity levels, ordered so that a higher number is more severe. */
export enum Level {
  None = 0,
  Trace = 1,
  Debug = 2,
  Info = 3,
  Warn = 4,
  Error = 5,
  Fatal = 6,
}

export const LEVEL_NAMES = ['NONE', 'TRACE', 'DEBUG', 'INFO', 'WARN', 'ERROR', 'FATAL'] as const;
export const LEVEL_COUNT = LEVEL_NAMES.length;

export interface TextPattern {
  text: string;
  regex: boolean;
  caseSensitive: boolean;
  wholeWord: boolean;
}

export interface FilterRule extends TextPattern {
  id: string;
  enabled: boolean;
  /** include: keep entries matching any include rule; exclude: drop entries matching any exclude rule. */
  mode: 'include' | 'exclude';
  /** Restrict matching to a single parsed column (e.g. "logger"); empty = whole entry text. */
  column?: string;
  /** The whole (trimmed) value must equal `text`, e.g. "logger = com.acme.Db" from a click on a cell. */
  exact?: boolean;
  /** `text` is a message template (see patterns.ts): the entry's template must equal it. `column` is ignored. */
  patternMatch?: boolean;
}

export interface FilterSpec {
  /** Indexed by Level; false hides that level. */
  levels: boolean[];
  rules: FilterRule[];
  /** Quick-filter query (see parseQueryExpr), AND-ed with everything else. */
  query?: string;
  queryCaseSensitive?: boolean;
  timeFrom?: number | null;
  timeTo?: number | null;
  /** Only show bookmarked rows (keys from rowKey()). */
  bookmarksOnly?: boolean;
  bookmarks?: string[];
  /** Also show N entries before/after each matching entry (grep -C). */
  context?: number;
  /** Collapse consecutive view rows with the same repeat key into one row (`RowData.repeat`). */
  groupRepeats?: 'off' | 'exact' | 'pattern';
  /** Per-source entry counts: entries below them are hidden ("Clear" in live tail). */
  clearedBefore?: number[] | null;
}

export interface CustomFormat {
  name: string;
  /** Regex with named groups: timestamp, level, thread, logger, message and any others. */
  pattern: string;
}

export interface ColumnDef {
  key: string;
  title: string;
  width: number;
}

export interface SourceInfo {
  id: number;
  path: string;
  displayName: string;
  size: number;
  lines: number;
  entries: number;
  /** Format name (also the key for per-format column settings). */
  format: string;
  formatId: string;
  /** Short format name for the picker and the status bar. */
  formatLabel: string;
  /** The format was detected (false: the user picked it). */
  formatAuto: boolean;
  /** Sampled non-empty lines from the start of the file that start an entry in this format. */
  formatMatched: number;
  formatSampled: number;
  encoding: string;
  compressed: boolean;
  live: boolean;
  /** 'gzip', 'bzip2', 'zip' or '' for a plain file. */
  compression?: string;
  /** This file is still being indexed (each file of a merge reports its own progress, 0..1). */
  indexing?: boolean;
  progress?: number;
  /** Only a time window was opened (windowFrom..windowTo, either may be open). */
  partial?: boolean;
  windowFrom?: number | null;
  windowTo?: number | null;
  /** 'hit' or 'grown' when the index came from the index cache. */
  cacheState?: string;
  /** For the local copy of a remote file: where it comes from and the state of the connection. */
  remote?: RemoteStatus;
}

export interface DocInfo {
  id: number;
  title: string;
  sources: SourceInfo[];
  columns: ColumnDef[];
  /** Total entries (before filtering). */
  total: number;
  /** Rows in the current view (after filtering). */
  viewCount: number;
  levelCounts: number[];
  indexing: boolean;
  progress: number;
  timeMin: number | null;
  timeMax: number | null;
  merged: boolean;
  /** "Show in context": every entry is shown and rows the filter rejects come back with `context: true`. */
  ignoreFilter: boolean;
}

export interface RowData {
  row: number;
  key: string;
  source: number;
  entry: number;
  /** 1-based first line number within its source file. */
  line: number;
  lineCount: number;
  level: Level;
  time: number | null;
  fields: Record<string, string>;
  text: string;
  /** True when the row is only shown as filter context. */
  context?: boolean;
  /** Rows collapsed into this one by "group repeats" (1 = not grouped). */
  repeat: number;
}

export interface SearchResult {
  /** Row positions (in the current view) with at least one match, ascending. */
  rows: number[];
  total: number;
  truncated: boolean;
  cancelled?: boolean;
}

/** The most common values of one parsed field, counted over the view (or a sample of it). */
export interface FieldStat {
  key: string;
  /** Rows with a non-empty value. */
  present: number;
  distinct: number;
  /** More than the tracked maximum of distinct values: `distinct` is a lower bound. */
  distinctCapped: boolean;
  /** [value, count], most common first. */
  top: [string, number][];
}

/** `scanned` of `viewCount` rows were read (`sampled` when fewer). */
export interface FieldStats {
  scanned: number;
  viewCount: number;
  sampled: boolean;
  fields: FieldStat[];
  cancelled?: boolean;
}

export interface PatternStat {
  template: string;
  count: number;
  /** Indexed by Level. */
  levels: number[];
  /** First message seen with this template (first line, at most 300 chars). */
  sample: string;
  /** Counts in 24 equal buckets of the scanned sequence. */
  spark: number[];
}

/** Message templates over the view (or a sample of it), most common first. */
export interface PatternsResult {
  scanned: number;
  viewCount: number;
  sampled: boolean;
  /** Distinct templates seen (the overflow pattern counts as one). */
  total: number;
  patterns: PatternStat[];
  cancelled?: boolean;
}

export interface Histogram {
  start: number;
  bucketMs: number;
  /** counts[bucket] = [other, warn, error] */
  counts: [number, number, number][];
}

export const rowKey = (source: number, entry: number) => `${source}:${entry}`;

// ---------------------------------------------------------------- analytics

/** State of a document's background analysis (see analytics.ts). */
export type AnalyticsState = 'waiting' | 'running' | 'paused' | 'done' | 'cancelled' | 'error';

/** [value, count], most common first. */
export type ValueCount = [string, number];

export interface AnalyticsPattern {
  template: string;
  sample: string;
  count: number;
  /** Indexed by Level. */
  levels: number[];
  /** First and last entry time (null when the entries have none). */
  first: number | null;
  last: number | null;
  /** Counts over `AnalyticsResult.spark` buckets. */
  spark: number[];
  /** Durations ("took 120 ms") found in these messages, in ms. */
  durCount: number;
  durAvg: number;
  durMax: number;
}

export interface AnalyticsNumeric {
  count: number;
  min: number;
  max: number;
  avg: number;
  p50: number;
  p90: number;
  p99: number;
  /** Equal-width buckets from `start`, `width` wide. */
  hist: { start: number; width: number; counts: number[] };
}

export interface AnalyticsField {
  key: string;
  present: number;
  distinct: number;
  distinctCapped: boolean;
  top: ValueCount[];
  /** Set when (nearly) every value is a number. */
  numeric?: AnalyticsNumeric;
}

export interface AnalyticsBurst {
  from: number;
  to: number;
  errors: number;
  total: number;
  /** errors / the typical errors per bucket. */
  factor: number;
}

export interface AnalyticsResult {
  state: AnalyticsState;
  error?: string;
  /** 0..1 over the whole run (index pass, then the entry texts). */
  progress: number;
  /** Entry texts read so far, of `total` entries. */
  scanned: number;
  total: number;
  elapsedMs: number;
  /** Bumped whenever the analysis starts over (the data was replaced). */
  run: number;
  summary: {
    entries: number;
    sources: number;
    bytes: number;
    timed: number;
    timeMin: number | null;
    timeMax: number | null;
    /** Entries per minute over the span, and the busiest timeline bucket per minute. */
    ratePerMin: number;
    peakPerMin: number;
    peakAt: number | null;
    multiline: number;
    avgLength: number;
    maxLength: number;
    patterns: number;
    exceptions: number;
  };
  levels: number[];
  /** counts[bucket][level]. */
  timeline: { start: number; bucketMs: number; counts: number[][] } | null;
  /** Pattern sparklines: `buckets` equal parts of [start, end] (by time), or of the entry order when the log has no times. */
  spark: { start: number; end: number; buckets: number; byTime: boolean };
  /** Entries per local hour of day: [all, warnings, errors]. */
  hours: [number, number, number][];
  /** Entries per weekday, Sunday first. */
  weekdays: number[];
  bursts: AnalyticsBurst[];
  /** Longest silences between consecutive entries. */
  gaps: { from: number; to: number; ms: number }[];
  sources: { name: string; entries: number; levels: number[]; timeMin: number | null; timeMax: number | null }[];
  /** Most common templates. */
  patterns: AnalyticsPattern[];
  /** Templates with the most errors (and fatals). */
  errorPatterns: AnalyticsPattern[];
  /** Templates first seen in the last quarter of the time span. */
  newPatterns: AnalyticsPattern[];
  /** Templates that stopped in the first half of the span after being common. */
  stoppedPatterns: AnalyticsPattern[];
  /** Templates with the longest average duration. */
  slowPatterns: AnalyticsPattern[];
  exceptions: { name: string; count: number; first: number | null; last: number | null; levels: number[] }[];
  fields: AnalyticsField[];
  durations: AnalyticsNumeric | null;
  ips: ValueCount[];
  urls: ValueCount[];
  methods: ValueCount[];
}
