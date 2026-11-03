import type { AiSettings } from '../core/aiClient';
import type { TimeMode } from '../core/timestamp';
import type { OpenWindow } from '../core/timeWindow';
import { DEFAULT_FRAMEWORK_PACKAGES } from '../core/folding';
import type { CustomFormat, DocInfo, FieldStats, FilterRule, FilterSpec, PatternsResult, SearchResult, TextPattern } from '../core/types';

export interface HighlightRule extends TextPattern {
  id: string;
  enabled: boolean;
  color: string;
  /** Color the whole row instead of just the matched text. */
  row: boolean;
}

export interface Bookmark {
  key: string;
  line: number;
  source: number;
  preview: string;
  note: string;
  /** Position in the current view (null when filtered out); refreshed after each view change. */
  row?: number | null;
}

/** A row kept in the strip above the grid. */
export interface Pin {
  key: string;
  line: number;
  source: number;
  preview: string;
  /** Position in the current view (null when filtered out); refreshed after each view change. */
  row?: number | null;
}

export interface FilterPreset {
  name: string;
  rules: FilterRule[];
  query: string;
}

export interface Settings {
  theme?: 'light' | 'dark' | 'system';
  highlights?: HighlightRule[];
  presets?: FilterPreset[];
  customFormats?: CustomFormat[];
  recent?: string[];
  /** The ssh program for remote files (empty: $ULV_SSH, else ssh from the PATH). */
  sshProgram?: string;
  /** What "Multi-host tail…" was last opened with. */
  multiHostHosts?: string;
  multiHostPath?: string;
  showHistogram?: boolean;
  showDetail?: boolean;
  showSidebar?: boolean;
  detailHeight?: number;
  /** Bookmarks of single-file tabs, by file path. */
  bookmarks?: Record<string, { line: number; note: string }[]>;
  columnWidths?: Record<string, number>;
  hiddenColumns?: string[];
  /** Field keys shown as extra columns, by format name (sources[0].format). */
  extraColumns?: Record<string, string[]>;
  /** Column keys in display order, by format name. */
  columnOrder?: Record<string, string[]>;
  /** Quick-filter queries, most recent first. */
  queryHistory?: string[];
  /** Tint whole rows by level (the pre-phase-2 look) instead of a level stripe in the gutter. */
  tintRows?: boolean;
  /** Colour dates, numbers, ids, URLs… in the message column. */
  tokenColors?: boolean;
  density?: 'compact' | 'comfortable';
  /** Soft-wrap the message column (up to 8 lines). */
  wrap?: boolean;
  detailDock?: 'bottom' | 'right';
  detailWidth?: number;
  timeMode?: TimeMode;
  timeUtc?: boolean;
  /** Mark time gaps of at least this many ms between consecutive rows (0 = off). */
  gapMs?: number;
  histogramHeight?: number;
  /** Fold runs of framework frames in stack traces (expansion and details pane). */
  foldFrames?: boolean;
  frameworkPackages?: string[];
  /** Workspace files, most recent first. */
  recentWorkspaces?: string[];
  /** Save the index of big files so reopening them is instant (default on). */
  indexCache?: boolean;
  /** Pinned rows by file path: their 1-based lines. */
  pins?: Record<string, number[]>;
  /** Editor for source links: vscode, rider, intellij or custom (default vscode). */
  editor?: string;
  /** Command line of the custom editor, with {path}, {line} and {col}. */
  editorCommand?: string;
  /** Folders that relative source paths in stack traces are resolved against, first match wins. */
  sourceRoots?: string[];
  /** AI features (off by default). The API keys are not here: the main process keeps them (safeStorage). */
  ai?: AiSettings;
}

/** Display settings with their defaults resolved. */
export interface ViewSettings {
  tintRows: boolean;
  tokenColors: boolean;
  density: 'compact' | 'comfortable';
  wrap: boolean;
  detailDock: 'bottom' | 'right';
  detailWidth: number;
  timeMode: TimeMode;
  timeUtc: boolean;
  gapMs: number;
  histogramHeight: number;
  foldFrames: boolean;
  frameworkPackages: string[];
}

export function viewSettings(s: Settings): ViewSettings {
  return {
    tintRows: s.tintRows ?? false,
    tokenColors: s.tokenColors ?? true,
    density: s.density ?? 'compact',
    wrap: s.wrap ?? false,
    detailDock: s.detailDock ?? 'bottom',
    detailWidth: Math.max(200, s.detailWidth ?? 420),
    timeMode: s.timeMode ?? 'original',
    timeUtc: s.timeUtc ?? false,
    gapMs: s.gapMs ?? 10000,
    histogramHeight: Math.max(40, Math.min(240, s.histogramHeight ?? 64)),
    foldFrames: s.foldFrames ?? true,
    frameworkPackages: s.frameworkPackages ?? DEFAULT_FRAMEWORK_PACKAGES,
  };
}

/** Row height and font size per density; continuation/wrapped lines use round(fontSize * 1.4). */
export const DENSITY = {
  compact: { rowH: 20, fontSize: 12.5 },
  comfortable: { rowH: 26, fontSize: 13.5 },
} as const;

export interface Tab {
  id: number;
  paths: string[];
  info: DocInfo;
  filter: FilterSpec;
  search: { pattern: TextPattern; result: SearchResult | null; current: number };
  bookmarks: Map<string, Bookmark>;
  /** Selected view rows: anchor..focus. */
  anchor: number;
  focus: number;
  topRow: number;
  follow: boolean;
  formatId: string;
  encoding?: string;
  /** The time window this tab was opened with (null: the whole file). */
  window?: OpenWindow | null;
  error?: string;
  progress?: { phase: string; fraction: number };
  /** What the last filter or search scanned and how long it took, e.g. "Filtered 8,695,680 entries in 0.82 s". */
  scanCost?: string;
  /** The latest filter run (rows it finds arrive as partial views tagged with it) and the epoch the grid shows. */
  filterSeq?: number;
  filtering?: boolean;
  partialEpoch?: number;
  /** "Show in context": every entry is shown, rows the filter hides are dimmed. */
  contextMode?: boolean;
  /** Line of the entry that was focused when context mode was entered (for the banner). */
  contextLine?: number;
  fieldStats?: FieldStats | null;
  /** When the last field-stats run started (ms). */
  statsAt?: number;
  /** Fields expanded in the sidebar's Fields section. */
  expandedFields?: Set<string>;
  /** Keys of multi-line entries expanded inline in the grid. */
  expanded: Set<string>;
  /** Keys of entries whose framework frames are shown unfolded. */
  unfolded: Set<string>;
  /** Patterns view shown instead of the grid. */
  showPatterns?: boolean;
  patterns?: PatternsResult | null;
  /** Selected pattern (index into patterns.patterns). */
  patternSel?: number;
  /** Patterns being computed. */
  patternsBusy?: boolean;
  /** When the last patterns run started (ms). */
  patternsAt?: number;
  /** Pinned rows, by entry key (see pins.ts). */
  pins?: Map<string, Pin>;
  /** The field the timeline is stacked by ('level' when unset). */
  histogramGroup?: string;
  /** Rows appended to the view while not following (the "↓ N new" pill). */
  newRows?: number;
  /** The right-hand pane of a split view: a document over the same files, with its own filter. */
  split?: Tab | null;
  /** For a split pane: the tab it belongs to. */
  splitOf?: Tab;
  /** Selecting an entry in one pane of a split scrolls the other to it, or to the nearest entry by time (default on). */
  syncSplit?: boolean;
  /** Number shown after the title of a duplicated tab ("app.log (2)"); 0 or absent for the first one. */
  copy?: number;
  /** Column choices of this tab alone (duplicates and split panes, not saved); absent: the per-format settings. */
  columns?: { extra: string[]; order: string[]; hidden: string[] };
  /** Settings to apply once the tab's document is ready (workspace load). */
  pending?: {
    bookmarks: { path: string; line: number; note: string }[];
    pins?: { path: string; line: number }[];
    columns?: { extraColumns: string[]; columnOrder: string[]; hiddenColumns: string[] };
  };
}

export const defaultFilter = (): FilterSpec => ({ levels: [true, true, true, true, true, true, true], rules: [], query: '', context: 0, groupRepeats: 'off', clearedBefore: null });

/** "×N" badge text for grouped rows: ×1.2k above 9999, ×3.4M above 999,999. */
export function fmtRepeat(n: number): string {
  if (n > 999_999) return `×${(n / 1_000_000).toFixed(1)}M`;
  if (n > 9999) return `×${(n / 1000).toFixed(1)}k`;
  return `×${n}`;
}

export const uid = () => Math.random().toString(36).slice(2, 10);

export const DEFAULT_HIGHLIGHTS: HighlightRule[] = [
  { id: 'h-exc', text: '\\b\\w+(Exception|Error)\\b', regex: true, caseSensitive: true, wholeWord: false, enabled: true, color: '#d9480f', row: false },
  { id: 'h-timeout', text: 'timeout|timed out', regex: true, caseSensitive: false, wholeWord: false, enabled: true, color: '#9c36b5', row: false },
  { id: 'h-uuid', text: '\\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\\b', regex: true, caseSensitive: false, wholeWord: false, enabled: true, color: '#1971c2', row: false },
];
