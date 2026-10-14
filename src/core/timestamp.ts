// Timestamp recognition for the formats log files commonly use.

const MONTHS: Record<string, number> = {
  jan: 0, feb: 1, mar: 2, apr: 3, may: 4, jun: 5, jul: 6, aug: 7, sep: 8, oct: 9, nov: 10, dec: 11,
};

export interface TimestampMatch {
  ms: number;
  /** Number of characters consumed, including a leading "[" if present. */
  length: number;
}

// Sticky (y) regexes so we can match at an offset without slicing the line.
const ISO = /(\d{4})[-/.](\d{2})[-/.](\d{2})[T ](\d{2}):(\d{2}):(\d{2})(?:[.,](\d{1,9}))?(?: ?(Z|[+-]\d{2}:?\d{2}))?/y;
const APACHE = /(\d{2})\/([A-Za-z]{3})\/(\d{4}):(\d{2}):(\d{2}):(\d{2})(?:[.,](\d{1,9}))?(?: ([+-]\d{4}))?/y;
const SYSLOG = /([A-Z][a-z]{2}) {1,2}(\d{1,2}) (\d{2}):(\d{2}):(\d{2})(?:\.(\d{1,9}))?/y;
const DMY = /(\d{2})([./-])(\d{2})\2(\d{4})[ T](\d{2}):(\d{2}):(\d{2})(?:[.,](\d{1,9}))?/y;
const TIME_ONLY = /(\d{2}):(\d{2}):(\d{2})(?:[.,](\d{1,9}))?(?!\d)/y;
const EPOCH = /(\d{10}|\d{13})(?:\.(\d{1,9}))?(?!\d)/y;

const isD = (c: number) => c >= 48 && c <= 57;
const d2 = (s: string, i: number) => (s.charCodeAt(i) - 48) * 10 + s.charCodeAt(i + 1) - 48;

/** Hand-rolled parser for the most common shape, "YYYY-MM-DD[T ]HH:MM:SS[.fff][tz]" (~5x faster than the regex). */
function fastIso(s: string, at: number): TimestampMatch | null {
  if (s.length < at + 19) return null;
  for (const i of [0, 1, 2, 3, 5, 6, 8, 9, 11, 12, 14, 15, 17, 18]) if (!isD(s.charCodeAt(at + i))) return null;
  const sep = s.charCodeAt(at + 4);
  if ((sep !== 45 && sep !== 47 && sep !== 46) || s.charCodeAt(at + 7) !== sep) return null;
  const tc = s.charCodeAt(at + 10);
  if ((tc !== 84 && tc !== 32) || s.charCodeAt(at + 13) !== 58 || s.charCodeAt(at + 16) !== 58) return null;
  let i = at + 19;
  let ms = 0;
  const fc = s.charCodeAt(i);
  if ((fc === 46 || fc === 44) && isD(s.charCodeAt(i + 1))) {
    i++;
    let digits = 0;
    while (isD(s.charCodeAt(i))) {
      if (digits < 3) ms = ms * 10 + s.charCodeAt(i) - 48;
      digits++;
      i++;
    }
    if (digits === 1) ms *= 100;
    else if (digits === 2) ms *= 10;
  }
  if (isD(s.charCodeAt(i))) return null;
  let tz: string | undefined;
  let j = s.charCodeAt(i) === 32 ? i + 1 : i;
  const zc = s.charCodeAt(j);
  if (zc === 90) {
    tz = 'Z';
    i = j + 1;
  } else if ((zc === 43 || zc === 45) && isD(s.charCodeAt(j + 1)) && isD(s.charCodeAt(j + 2))) {
    const colon = s.charCodeAt(j + 3) === 58 ? 1 : 0;
    if (isD(s.charCodeAt(j + 3 + colon)) && isD(s.charCodeAt(j + 4 + colon))) {
      tz = s.slice(j, j + 5 + colon);
      i = j + 5 + colon;
    }
  }
  const y = d2(s, at) * 100 + d2(s, at + 2);
  const v = build(y, d2(s, at + 5) - 1, d2(s, at + 8), d2(s, at + 11), d2(s, at + 14), d2(s, at + 17), ms, tz);
  return Number.isNaN(v) ? null : { ms: v, length: i - at };
}

function exec(re: RegExp, s: string, at: number): RegExpExecArray | null {
  re.lastIndex = at;
  return re.exec(s);
}

function frac(f: string | undefined): number {
  if (!f) return 0;
  return Number((f + '00').slice(0, 3));
}

function tzOffsetMs(tz: string | undefined): number | null {
  if (!tz) return null;
  if (tz === 'Z') return 0;
  const m = /^([+-])(\d{2}):?(\d{2})$/.exec(tz);
  if (!m) return null;
  const v = (Number(m[2]) * 60 + Number(m[3])) * 60000;
  return m[1] === '-' ? -v : v;
}

// Local-time conversion is the slow part of parsing millions of lines; the offset only changes
// at DST boundaries, so cache the epoch value of each local hour.
let hourKey = -1;
let hourMs = 0;

function build(y: number, mo: number, d: number, h: number, mi: number, s: number, ms: number, tz: string | undefined): number {
  const off = tzOffsetMs(tz);
  if (off !== null) return Date.UTC(y, mo, d, h, mi, s, ms) - off;
  if (mo > 11 || d > 31 || h > 23 || mi > 59 || s > 60) return NaN;
  const key = ((y * 12 + mo) * 32 + d) * 24 + h;
  if (key !== hourKey) {
    hourKey = key;
    hourMs = new Date(y, mo, d, h).getTime();
  }
  return hourMs + (mi * 60 + s) * 1000 + ms;
}

/**
 * Recognises a timestamp at the start of `s` (optionally inside "[...]").
 * `defaultYear` is used for formats without a year (syslog).
 */
export function matchTimestamp(s: string, defaultYear = new Date().getFullYear(), allowEpoch = false): TimestampMatch | null {
  const skip = s.charCodeAt(0) === 91 /* [ */ ? 1 : 0;
  const c = s.charCodeAt(skip);
  const digit = c >= 48 && c <= 57;
  if (!digit && !(c >= 65 && c <= 90)) return null; // fast path for continuation lines
  if (digit) {
    const f = fastIso(s, skip);
    if (f) {
      if (skip) {
        f.length++;
        if (s.charCodeAt(f.length) === 93 /* ] */) f.length++;
      }
      return f;
    }
  }
  let m: RegExpExecArray | null = null;
  let ms: number;
  if (digit && (m = exec(ISO, s, skip))) {
    ms = build(+m[1], +m[2] - 1, +m[3], +m[4], +m[5], +m[6], frac(m[7]), m[8]);
  } else if (digit && (m = exec(APACHE, s, skip))) {
    const mo = MONTHS[m[2].toLowerCase()];
    if (mo === undefined) return null;
    ms = build(+m[3], mo, +m[1], +m[4], +m[5], +m[6], frac(m[7]), m[8]);
  } else if (!digit && (m = exec(SYSLOG, s, skip))) {
    const mo = MONTHS[m[1].toLowerCase()];
    if (mo === undefined) return null;
    ms = build(defaultYear, mo, +m[2], +m[3], +m[4], +m[5], frac(m[6]), undefined);
  } else if (digit && (m = exec(DMY, s, skip))) {
    // 31.12.2024 / 31-12-2024 are day-first; 12/31/2024 is month-first.
    const a = +m[1], b = +m[3];
    const [d, mo] = m[2] === '/' && b <= 31 && a <= 12 ? [b, a] : [a, b];
    ms = build(+m[4], mo - 1, d, +m[5], +m[6], +m[7], frac(m[8]), undefined);
  } else if (digit && (m = exec(TIME_ONLY, s, skip))) {
    ms = ((+m[1] * 60 + +m[2]) * 60 + +m[3]) * 1000 + frac(m[4]);
  } else if (digit && allowEpoch && (m = exec(EPOCH, s, skip))) {
    ms = m[1].length === 13 ? Number(m[1]) : Number(m[1]) * 1000 + frac(m[2]);
  } else {
    return null;
  }
  if (!Number.isFinite(ms)) return null;
  let length = m[0].length + skip;
  if (skip && s.charCodeAt(length) === 93 /* ] */) length++;
  return { ms, length };
}

/** Parses a standalone timestamp value (e.g. from a JSON field). */
export function parseTimestamp(value: unknown, defaultYear?: number): number | null {
  if (value === null || value === undefined) return null;
  if (typeof value === 'number') {
    if (!Number.isFinite(value)) return null;
    return value > 1e12 ? value : value * 1000;
  }
  const s = String(value).trim();
  const m = matchTimestamp(s, defaultYear, true);
  if (m) return m.ms;
  const d = Date.parse(s);
  return Number.isNaN(d) ? null : d;
}

export function formatTime(ms: number): string {
  const d = new Date(ms);
  const p = (n: number, w = 2) => String(n).padStart(w, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}.${p(d.getMilliseconds(), 3)}`;
}

export function formatDuration(ms: number): string {
  const sign = ms < 0 ? '-' : '';
  ms = Math.abs(ms);
  if (ms < 1000) return `${sign}${ms} ms`;
  if (ms < 60000) return `${sign}${(ms / 1000).toFixed(3)} s`;
  const s = Math.floor(ms / 1000);
  const h = Math.floor(s / 3600), mi = Math.floor((s % 3600) / 60), sec = s % 60;
  const rest = `${String(mi).padStart(2, '0')}m ${String(sec).padStart(2, '0')}.${String(ms % 1000).padStart(3, '0')}s`;
  return h ? `${sign}${h}h ${rest}` : `${sign}${rest}`;
}

// ------------------------------------------------------- display formatting

export type TimeMode = 'original' | 'full' | 'time' | 'relative' | 'delta';

const p2 = (n: number, w = 2) => String(n).padStart(w, '0');

/** `yyyy-MM-dd HH:mm:ss.fff` (or `HH:mm:ss.fff` with `timeOnly`), local or UTC (UTC adds a trailing Z). */
export function formatClock(ms: number, utc = false, timeOnly = false, millis = true): string {
  const d = new Date(ms);
  const [y, mo, da, h, mi, s, f] = utc
    ? [d.getUTCFullYear(), d.getUTCMonth() + 1, d.getUTCDate(), d.getUTCHours(), d.getUTCMinutes(), d.getUTCSeconds(), d.getUTCMilliseconds()]
    : [d.getFullYear(), d.getMonth() + 1, d.getDate(), d.getHours(), d.getMinutes(), d.getSeconds(), d.getMilliseconds()];
  const time = `${p2(h)}:${p2(mi)}:${p2(s)}${millis ? `.${p2(f, 3)}` : ''}`;
  return `${timeOnly ? '' : `${y}-${p2(mo)}-${p2(da)} `}${time}${utc ? 'Z' : ''}`;
}

/** formatDuration with a leading `+` for positive values. */
export function signedDuration(ms: number): string {
  return ms > 0 ? `+${formatDuration(ms)}` : formatDuration(ms);
}

export interface TimeCellContext {
  utc?: boolean;
  /** Time of the focused row (relative mode). */
  ref?: number | null;
  /** Time of the previous row in the view (delta mode); undefined/null for the first row. */
  prev?: number | null;
}

/** Text of the Time column for one row in the given mode. Rows without a parsed time keep the original text. */
export function formatTimeCell(mode: TimeMode, time: number | null, original: string, ctx: TimeCellContext = {}): string {
  if (time === null || mode === 'original') return original;
  switch (mode) {
    case 'full':
      return formatClock(time, ctx.utc);
    case 'time':
      return formatClock(time, ctx.utc, true);
    case 'relative':
      return ctx.ref == null ? original : signedDuration(time - ctx.ref);
    case 'delta':
      return ctx.prev == null ? '' : signedDuration(time - ctx.prev);
  }
  return original;
}

/** Compact gap label for the gutter: `+850ms`, `+32s`, `+2m`, `+5h`, `+3d` (whole units, floored). */
export function formatGap(ms: number): string {
  const sign = ms < 0 ? '-' : '+';
  ms = Math.abs(ms);
  if (ms < 1000) return `${sign}${Math.floor(ms)}ms`;
  if (ms < 60_000) return `${sign}${Math.floor(ms / 1000)}s`;
  if (ms < 3_600_000) return `${sign}${Math.floor(ms / 60_000)}m`;
  if (ms < 86_400_000) return `${sign}${Math.floor(ms / 3_600_000)}h`;
  return `${sign}${Math.floor(ms / 86_400_000)}d`;
}

/** Histogram bucket tooltip: time span, entry count, then warnings/errors (zero parts omitted). */
export function histogramTip(start: number, end: number, counts: [number, number, number]): string {
  const crossesDay = formatClock(start).slice(0, 10) !== formatClock(Math.max(start, end - 1)).slice(0, 10);
  const span = `${formatClock(start, false, !crossesDay, false)} – ${formatClock(end, false, !crossesDay, false)}`;
  const n = counts[0] + counts[1] + counts[2];
  const lines = [span, `${n.toLocaleString('en-US')} ${n === 1 ? 'entry' : 'entries'}`];
  const parts: string[] = [];
  if (counts[1]) parts.push(`${counts[1].toLocaleString('en-US')} ${counts[1] === 1 ? 'warning' : 'warnings'}`);
  if (counts[2]) parts.push(`${counts[2].toLocaleString('en-US')} ${counts[2] === 1 ? 'error' : 'errors'}`);
  if (parts.length) lines.push(parts.join(' · '));
  return lines.join('\n');
}
