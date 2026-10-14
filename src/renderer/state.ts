import type { CustomFormat, DocInfo, FieldStats, FilterRule, FilterSpec, SearchResult, TextPattern } from '../core/types';

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
}

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
  error?: string;
  progress?: { phase: string; fraction: number };
  /** "Show in context": every entry is shown, rows the filter hides are dimmed. */
  contextMode?: boolean;
  /** Line of the entry that was focused when context mode was entered (for the banner). */
  contextLine?: number;
  fieldStats?: FieldStats | null;
  /** When the last field-stats run started (ms). */
  statsAt?: number;
  /** Fields expanded in the sidebar's Fields section. */
  expandedFields?: Set<string>;
}

export const defaultFilter = (): FilterSpec => ({ levels: [true, true, true, true, true, true, true], rules: [], query: '', context: 0 });

export const uid = () => Math.random().toString(36).slice(2, 10);

export const DEFAULT_HIGHLIGHTS: HighlightRule[] = [
  { id: 'h-exc', text: '\\b\\w+(Exception|Error)\\b', regex: true, caseSensitive: true, wholeWord: false, enabled: true, color: '#d9480f', row: false },
  { id: 'h-timeout', text: 'timeout|timed out', regex: true, caseSensitive: false, wholeWord: false, enabled: true, color: '#9c36b5', row: false },
  { id: 'h-uuid', text: '\\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\\b', regex: true, caseSensitive: false, wholeWord: false, enabled: true, color: '#1971c2', row: false },
];
