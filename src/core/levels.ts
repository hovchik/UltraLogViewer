import { Level } from './types';

const WORDS: Record<string, Level> = Object.assign(Object.create(null), {
  TRACE: Level.Trace, VERBOSE: Level.Trace, FINEST: Level.Trace, FINER: Level.Trace, TRC: Level.Trace, VRB: Level.Trace,
  DEBUG: Level.Debug, FINE: Level.Debug, DBG: Level.Debug, DBUG: Level.Debug,
  INFO: Level.Info, INFORMATION: Level.Info, NOTICE: Level.Info, INF: Level.Info, CONFIG: Level.Info,
  WARN: Level.Warn, WARNING: Level.Warn, WRN: Level.Warn,
  ERROR: Level.Error, ERR: Level.Error, SEVERE: Level.Error, ERRO: Level.Error, EROR: Level.Error,
  FATAL: Level.Fatal, CRITICAL: Level.Fatal, CRIT: Level.Fatal, PANIC: Level.Fatal, EMERG: Level.Fatal,
  EMERGENCY: Level.Fatal, ALERT: Level.Fatal, FTL: Level.Fatal,
});

const SINGLE: Record<string, Level> = Object.assign(Object.create(null), { T: Level.Trace, V: Level.Trace, D: Level.Debug, I: Level.Info, W: Level.Warn, E: Level.Error, F: Level.Fatal });

/** Parses a level token such as "WARN", "warning", "E" or a numeric pino/bunyan level. */
export function parseLevel(value: unknown): Level {
  if (value === undefined || value === null) return Level.None;
  if (typeof value === 'number') return levelFromNumber(value);
  if (typeof value === 'string') {
    const direct = WORDS[value];
    if (direct !== undefined) return direct;
  }
  const s = String(value).trim();
  if (!s) return Level.None;
  if (/^\d+$/.test(s)) return levelFromNumber(Number(s));
  const up = s.toUpperCase();
  return WORDS[up] ?? (up.length === 1 ? SINGLE[up] ?? Level.None : Level.None);
}

/** pino/bunyan (10..60) or syslog severity (0..7). */
function levelFromNumber(n: number): Level {
  if (n >= 10) {
    if (n >= 60) return Level.Fatal;
    if (n >= 50) return Level.Error;
    if (n >= 40) return Level.Warn;
    if (n >= 30) return Level.Info;
    if (n >= 20) return Level.Debug;
    return Level.Trace;
  }
  if (n <= 2) return Level.Fatal;
  if (n === 3) return Level.Error;
  if (n === 4) return Level.Warn;
  if (n <= 6) return Level.Info;
  return Level.Debug;
}

export function levelFromSyslogSeverity(sev: number): Level {
  return levelFromNumber(Math.max(0, Math.min(7, sev)));
}

const BARE = /\b(TRACE|VERBOSE|FINEST|FINER|DEBUG|FINE|INFO|INFORMATION|NOTICE|WARN|WARNING|ERROR|ERR|SEVERE|CRITICAL|CRIT|FATAL|PANIC|EMERG)\b/;
const DELIMITED = /[[<(|:\s](trace|debug|info|notice|warn(?:ing)?|error|err|fatal|crit(?:ical)?|alert|emerg)[\]>)|:]/i;

/** Heuristic level detection for free-form text: looks at the beginning of the line only. */
export function detectLevel(text: string): Level {
  const head = text.length > 240 ? text.slice(0, 240) : text;
  const m = BARE.exec(head) ?? DELIMITED.exec(head);
  return m ? parseLevel(m[1]) : Level.None;
}
