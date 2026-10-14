// Shared types between the engine (worker thread) and the renderer.

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
}

export interface FilterSpec {
  /** Indexed by Level; false hides that level. */
  levels: boolean[];
  rules: FilterRule[];
  /** Quick-filter query (see parseQuery), AND-ed with everything else. */
  query?: string;
  queryCaseSensitive?: boolean;
  timeFrom?: number | null;
  timeTo?: number | null;
  /** Only show bookmarked rows (keys from rowKey()). */
  bookmarksOnly?: boolean;
  bookmarks?: string[];
  /** Also show N entries before/after each matching entry (grep -C). */
  context?: number;
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
  format: string;
  encoding: string;
  compressed: boolean;
  live: boolean;
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
}

export interface SearchResult {
  /** Row positions (in the current view) with at least one match, ascending. */
  rows: number[];
  total: number;
  truncated: boolean;
  cancelled?: boolean;
}

export interface Histogram {
  start: number;
  bucketMs: number;
  /** counts[bucket] = [other, warn, error] */
  counts: [number, number, number][];
}

export const rowKey = (source: number, entry: number) => `${source}:${entry}`;
