// Reading and testing entry texts in batches: the expensive part of search, filtering, group repeats, patterns, field
// stats and export. Pure, so the very same code runs on the engine thread and on the ScanPool's worker threads (which
// read the file themselves, see scanWorker.ts); whoever runs a batch, the answer is the same.

import type { LogFormat } from './formats';
import { compileMatcher, compileQuery, Matcher, ruleMatcher, TIME_FIELD } from './query';
import { fieldValue, groupValue } from './histogramGroups';
import { fnv1a64, messageOf, repeatKey, template } from './patterns';
import { FilterSpec, Level, TextPattern } from './types';

/** Level names used when matching `level:` queries (WARNING also matches "warn"). */
const LEVEL_MATCH_NAMES = ['NONE', 'TRACE', 'DEBUG', 'INFO', 'WARNING', 'ERROR', 'FATAL'];

const strip = (l: string) => (l.charCodeAt(l.length - 1) === 13 ? l.slice(0, -1) : l);

/**
 * Splits the decoded text of consecutive entries; `counts[i]` is the number of lines of entry i. Each line loses one
 * trailing "\r" and the lines of an entry are joined with "\n".
 */
export function splitEntries(text: string, counts: ArrayLike<number>): string[] {
  const lines = text.split('\n');
  const out: string[] = new Array(counts.length);
  let li = 0;
  for (let i = 0; i < counts.length; i++) {
    const count = counts[i];
    if (count === 1) out[i] = strip(lines[li] ?? '');
    else out[i] = lines.slice(li, li + count).map(strip).join('\n');
    li += count;
  }
  return out;
}

/** The part of a filter that needs the entry text. */
export type TextFilter = Pick<FilterSpec, 'rules' | 'query' | 'queryCaseSensitive'>;

/**
 * Compiles the text part of a filter: some include rule matches (when there are any), no exclude rule matches, and the
 * quick-filter query holds. Null when the filter has no text part.
 */
export function compileTextTest(spec: TextFilter, fmt: LogFormat): ((text: string, level: Level, time: number) => boolean) | null {
  const compiled = (mode: 'include' | 'exclude') =>
    spec.rules.filter((r) => r.enabled && r.text && r.mode === mode).map((r) => ({ m: ruleMatcher(r), col: r.patternMatch ? undefined : r.column, pattern: !!r.patternMatch }));
  const includes = compiled('include');
  const excludes = compiled('exclude');
  const query = spec.query?.trim() ? compileQuery(spec.query, !!spec.queryCaseSensitive) : null;
  if (!includes.length && !excludes.length && !query) return null;
  // Per-entry state lives here, and the closures are made once, so testing an entry allocates only what parsing needs.
  let text = '';
  let level = Level.None;
  let time = NaN;
  let parsed: Record<string, string> | null = null;
  let tpl: string | null = null;
  const field = (k: string): string => {
    // The level is already indexed: answer without parsing the entry.
    if (k === 'level') return LEVEL_MATCH_NAMES[level];
    if (k === TIME_FIELD) return Number.isNaN(time) ? '' : String(time);
    const f = (parsed ??= fmt.parse(text));
    if (k in f) return f[k];
    const lk = k.toLowerCase();
    const alias = lk === 'timestamp' ? 'time' : lk;
    return f[alias] ?? f[Object.keys(f).find((x) => x.toLowerCase() === lk) ?? ''] ?? '';
  };
  const test = (r: { m: Matcher; col: string | undefined; pattern: boolean }) =>
    r.pattern ? r.m((tpl ??= template(messageOf(text, (parsed ??= fmt.parse(text)))))) : r.col ? r.m(field(r.col)) : r.m(text);
  return (t, l, tm) => {
    text = t;
    level = l;
    time = tm;
    parsed = tpl = null;
    if (includes.length && !includes.some(test)) return false;
    if (excludes.some(test)) return false;
    return !query || query(text, field);
  };
}

/** What to work out for each entry of a contiguous range. */
export type RangeOp =
  | { kind: 'filter'; filter: TextFilter }
  | { kind: 'search'; pattern: TextPattern }
  | { kind: 'repeat'; mode: 'exact' | 'pattern' };

/** filter / search: 1 per entry that passes. repeat: the 64-bit repeat-key hash of every entry as high/low halves. */
export type RangeResult = Uint8Array | { hi: Uint32Array; lo: Uint32Array };

/**
 * Evaluates a range op over a batch: `texts` and `levels` of its entries, and `candidates` (1 = test this entry, the
 * others are 0 without reading) or null for all of them.
 */
export function rangeEvaluator(op: RangeOp, fmt: LogFormat): (texts: string[], levels: ArrayLike<number>, times: ArrayLike<number>, candidates: Uint8Array | null) => RangeResult {
  if (op.kind === 'repeat') {
    return (texts, levels) => {
      const hi = new Uint32Array(texts.length);
      const lo = new Uint32Array(texts.length);
      for (let i = 0; i < texts.length; i++) [hi[i], lo[i]] = fnv1a64(repeatKey(levels[i], fmt.parse(texts[i]), texts[i], op.mode));
      return { hi, lo };
    };
  }
  const test: (text: string, level: Level, time: number) => boolean =
    op.kind === 'search' ? compileMatcher(op.pattern) : (compileTextTest(op.filter, fmt) ?? (() => true));
  return (texts, levels, times, candidates) => {
    const out = new Uint8Array(texts.length);
    for (let i = 0; i < texts.length; i++) if ((!candidates || candidates[i]) && test(texts[i], levels[i], times[i])) out[i] = 1;
    return out;
  };
}

/** What to work out for each of a list of view rows (entries of one source, in view order). */
export type RowsOp =
  | { kind: 'patterns' }
  | { kind: 'fields' }
  /** One field's value per row, as the grouped timeline counts it. */
  | { kind: 'field'; key: string }
  | { kind: 'export'; format: 'text' | 'csv' | 'jsonl'; columns: string[]; source: string | null };

/** patterns: template and sample of each row. fields: parsed fields. field: the value. export: one output line per row (with "\n"). */
export type RowsResult = { templates: string[]; samples: string[] } | Record<string, string>[] | string[];

/** First line of a message, at most 300 characters: what the patterns view shows for a template. */
export function patternSample(msg: string): string {
  const i = msg.indexOf('\n');
  const first = i < 0 ? msg : msg.slice(0, i);
  return first.length > 300 ? first.slice(0, 300) : first;
}

const csvQuote = (s: string) => `"${s.replace(/"/g, '""')}"`;

/** Evaluates a rows op: `texts` of the rows and their 1-based first `lines`. */
export function rowsEvaluator(op: RowsOp, fmt: LogFormat): (texts: string[], lines: ArrayLike<number>) => RowsResult {
  switch (op.kind) {
    case 'patterns':
      return (texts) => {
        const templates: string[] = new Array(texts.length);
        const samples: string[] = new Array(texts.length);
        for (let i = 0; i < texts.length; i++) {
          const msg = messageOf(texts[i], fmt.parse(texts[i]));
          templates[i] = template(msg);
          samples[i] = patternSample(msg);
        }
        return { templates, samples };
      };
    case 'fields':
      return (texts) => texts.map((t) => fmt.parse(t));
    case 'field': {
      const key = op.key;
      return (texts) => texts.map((t) => groupValue(fieldValue(fmt.parse(t), key)));
    }
    case 'export': {
      const { format, columns, source } = op;
      return (texts, lines) =>
        texts.map((text, i) => {
          if (format === 'text') return text + '\n';
          const fields = fmt.parse(text);
          if (format === 'csv') return (source !== null ? [source] : []).concat([String(lines[i]), ...columns.map((c) => fields[c] ?? '')]).map(csvQuote).join(',') + '\n';
          return JSON.stringify({ ...(source !== null ? { source } : {}), line: lines[i], ...fields }) + '\n';
        });
    }
  }
}

/**
 * A batch of whole entries of one file for a scan worker: byte runs of consecutive entries (which the worker reads from
 * the file itself), the line count, level and 1-based first line of each entry, and the op to evaluate.
 */
export interface EntryJob {
  /** The file to read, and its inode: a worker refuses a file that was replaced since it was indexed. */
  path: string;
  ino: number;
  encoding: string;
  /** [start, end) byte offsets of each run. */
  runs: Float64Array;
  /** Entries in each run. */
  runEntries: Uint32Array;
  counts: Uint32Array;
  levels: Uint8Array;
  /** Entry times (NaN = none), for since:/until: and @time inside the quick-filter query. */
  times: Float64Array;
  lines: Uint32Array;
  /** Range ops only: 1 = test the entry; null = test them all. */
  candidates: Uint8Array | null;
  op: RangeOp | RowsOp;
}

/** Evaluates a job's op over its entry texts. */
export function evaluate(job: EntryJob, fmt: LogFormat, texts: string[]): RangeResult | RowsResult {
  const op = job.op;
  return op.kind === 'filter' || op.kind === 'search' || op.kind === 'repeat'
    ? rangeEvaluator(op, fmt)(texts, job.levels, job.times, job.candidates)
    : rowsEvaluator(op, fmt)(texts, job.lines);
}

/** The typed arrays of a job, to transfer it to a worker without copying. */
export function jobBuffers(job: EntryJob): ArrayBuffer[] {
  const out = [job.runs.buffer, job.runEntries.buffer, job.counts.buffer, job.levels.buffer, job.times.buffer, job.lines.buffer] as ArrayBuffer[];
  if (job.candidates) out.push(job.candidates.buffer as ArrayBuffer);
  return out;
}

/** The typed arrays of a result, to transfer it back. */
export function resultBuffers(r: RangeResult | RowsResult): ArrayBuffer[] {
  if (r instanceof Uint8Array) return [r.buffer as ArrayBuffer];
  if ('hi' in r && r.hi instanceof Uint32Array) return [r.hi.buffer as ArrayBuffer, (r as { lo: Uint32Array }).lo.buffer as ArrayBuffer];
  return [];
}
