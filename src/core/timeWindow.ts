// Which part of a file to open, time specs ("-1h", "now", "2024-05-01T10:00") and the command line.

import { parseTimestamp } from './timestamp';

/**
 * since / until are epoch ms (inclusive); last is a duration back from the newest entry of the file (used when
 * since is not set).
 */
export interface OpenWindow {
  since?: number | null;
  until?: number | null;
  last?: number | null;
}

export const windowEmpty = (w: OpenWindow | null | undefined) => !w || (w.since == null && w.until == null && w.last == null);

const UNITS: Record<string, number> = { ms: 1, s: 1000, sec: 1000, m: 60_000, min: 60_000, h: 3_600_000, hr: 3_600_000, d: 86_400_000, w: 604_800_000 };
const DURATION = /^(?:(\d+(?:\.\d+)?)\s*(ms|s|sec|m|min|h|hr|d|w)\s*)+$/i;

/** "90s", "15m", "1h30m", "2d", "1w" → ms; null if not a duration. */
export function parseDuration(s: string | null | undefined): number | null {
  s = s?.trim();
  if (!s || !DURATION.test(s)) return null;
  let total = 0;
  for (const m of s.matchAll(/(\d+(?:\.\d+)?)\s*(ms|s|sec|m|min|h|hr|d|w)/gi)) total += Number(m[1]) * UNITS[m[2].toLowerCase()];
  return Math.floor(total);
}

/**
 * A point in time: "now", "-1h" (relative to now), a date ("2024-05-01", start of day), a date and time with or
 * without seconds, or a time of day ("10:30", today). Local time unless a zone is given.
 */
export function parsePoint(s: string | null | undefined, now: number): number | null {
  s = s?.trim();
  if (!s) return null;
  if (s.toLowerCase() === 'now') return now;
  if (s[0] === '-') {
    const back = parseDuration(s.slice(1));
    if (back !== null) return now - back;
  }
  if (/^\d{4}-\d{2}-\d{2}$/.test(s)) s += ' 00:00:00';
  else if (/^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}$/.test(s)) s += ':00';
  else if (/^\d{1,2}:\d{2}(:\d{2})?$/.test(s)) {
    const [h, m, sec = 0] = s.split(':').map(Number);
    if (h > 23 || m > 59 || sec > 59) return null;
    const d = new Date(now);
    d.setHours(h, m, sec, 0);
    return d.getTime();
  }
  // Only values that look like a date: parseTimestamp would also read bare numbers as epochs.
  if (!/^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}/.test(s)) return null;
  return parseTimestamp(s);
}

const p2 = (n: number) => String(n).padStart(2, '0');
const day = (ms: number) => {
  const d = new Date(ms);
  return `${d.getFullYear()}-${p2(d.getMonth() + 1)}-${p2(d.getDate())}`;
};
const hm = (ms: number) => {
  const d = new Date(ms);
  return `${p2(d.getHours())}:${p2(d.getMinutes())}`;
};

/** "2024-05-01 10:00–11:00": the shown range, with dates only where they are needed. */
export function describeWindow(from: number | null | undefined, to: number | null | undefined): string {
  if (from == null && to == null) return '';
  if (from == null) return `until ${day(to!)} ${hm(to!)}`;
  if (to == null) return `since ${day(from)} ${hm(from)}`;
  return day(from) === day(to) ? `${day(from)} ${hm(from)}–${hm(to)}` : `${day(from)} ${hm(from)}–${day(to)} ${hm(to)}`;
}

export interface CommandLine {
  files: string[];
  stdin: boolean;
  window: OpenWindow | null;
  errors: string[];
}

/**
 * The command line: files to open, "-" for standard input, and an optional window:
 * `--since -1h`, `--since 2024-05-01T10:00 --until 2024-05-01T11:00`, `--last 30m`. Other switches are ignored.
 */
export function parseCommandLine(args: string[], now: number, isFile: (p: string) => boolean = () => false): CommandLine {
  const out: CommandLine = { files: [], stdin: false, window: null, errors: [] };
  let since: number | null = null, until: number | null = null, last: number | null = null;
  for (let i = 0; i < args.length; i++) {
    const a = args[i];
    const value = (name: string): string | null => {
      if (a.startsWith(name + '=')) return a.slice(name.length + 1);
      if (i + 1 < args.length) return args[++i];
      out.errors.push(`${name} needs a value`);
      return null;
    };
    const opt = (name: string) => a === name || a.startsWith(name + '=');
    if (a === '-') out.stdin = true;
    else if (opt('--since')) {
      const v = value('--since');
      if (v === null) continue;
      since = parsePoint(v, now);
      if (since === null) out.errors.push(`--since: cannot read "${v}"`);
    } else if (opt('--until')) {
      const v = value('--until');
      if (v === null) continue;
      until = parsePoint(v, now);
      if (until === null) out.errors.push(`--until: cannot read "${v}"`);
    } else if (opt('--last')) {
      const v = value('--last');
      if (v === null) continue;
      last = parseDuration(v);
      if (last === null) out.errors.push(`--last: cannot read "${v}"`);
    } else if (a.startsWith('-') && !isFile(a)) continue; // Electron / Chromium switches
    else out.files.push(a);
  }
  if (since !== null || until !== null || last !== null) out.window = { since, until, last: since === null ? last : null };
  return out;
}
