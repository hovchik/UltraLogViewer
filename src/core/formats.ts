// Log format definitions and auto-detection.
//
// Each format answers three questions:
//  - does this line start a new entry? (lines that don't are continuation lines, e.g. stack traces)
//  - quick(): level + timestamp, used while indexing every entry
//  - parse(): all columns, used only for rows that are on screen

import { detectLevel, levelFromSyslogSeverity, parseLevel } from './levels';
import { matchTimestamp, parseTimestamp } from './timestamp';
import { ColumnDef, CustomFormat, Level } from './types';

export interface FormatContext {
  /** Year used for timestamps that don't carry one (syslog, logcat). */
  year: number;
}

export interface QuickInfo {
  level: Level;
  time: number | null;
}

export interface LogFormat {
  id: string;
  name: string;
  columns: ColumnDef[];
  multiline: boolean;
  isEntryStart(line: string): boolean;
  quick(line: string): QuickInfo;
  /** isEntryStart + quick in one pass (hot path while indexing); null for continuation lines. */
  scan?(line: string): QuickInfo | null;
  /** `entry` is the full (possibly multi-line) text of the entry. */
  parse(entry: string): Record<string, string>;
}

const col = (key: string, title: string, width: number): ColumnDef => ({ key, title, width });
const C_TIME = col('time', 'Time', 190);
const C_LEVEL = col('level', 'Level', 64);
const C_THREAD = col('thread', 'Thread', 110);
const C_LOGGER = col('logger', 'Logger', 180);
const C_MESSAGE = col('message', 'Message', 0);

const firstLine = (s: string) => {
  const i = s.indexOf('\n');
  return i < 0 ? s : s.slice(0, i);
};
const restLines = (s: string) => {
  const i = s.indexOf('\n');
  return i < 0 ? '' : s.slice(i);
};

// ---------------------------------------------------------------- plain text

export function plainFormat(): LogFormat {
  return {
    id: 'plain',
    name: 'Plain text',
    columns: [C_LEVEL, C_MESSAGE],
    multiline: false,
    isEntryStart: () => true,
    quick: (line) => ({ level: detectLevel(line), time: null }),
    parse: (entry) => ({ level: levelText(detectLevel(entry)), message: entry }),
  };
}

const LEVEL_LABEL = ['', 'TRACE', 'DEBUG', 'INFO', 'WARN', 'ERROR', 'FATAL'];
const levelText = (l: Level) => LEVEL_LABEL[l];

// ---------------------------------------------- timestamped text (log4j & co)

const LEVEL_TOKEN = /^(TRACE|VERBOSE|DEBUG|DBG|INFO|INF|NOTICE|WARN|WARNING|WRN|ERROR|ERR|SEVERE|FATAL|CRITICAL|CRIT|FINE|FINER|FINEST|FTL|VRB|trace|debug|info|warn|warning|error|fatal|critical)\b/;
const LOGGER_TOKEN = /^([A-Za-z_$][\w$.:<>@/-]*)\s*(?:-|:|\|)\s+/;

/**
 * Generic "timestamp first" format. Covers log4j/logback/log4net/NLog/serilog/python
 * logging default layouts such as:
 *   2024-05-01 10:00:00,123 [main] INFO  com.acme.App - Started
 *   2024-05-01T10:00:00.123Z ERROR 1234 --- [worker-1] c.a.Service : boom
 *   2024-05-01 10:00:00.1234|WARN|Acme.Service|Low disk
 *   [2024-05-01 10:00:00] local.ERROR: message
 */
export function timestampedFormat(ctx: FormatContext): LogFormat {
  return {
    id: 'timestamped',
    name: 'Timestamped text (log4j, logback, NLog, Serilog, Python…)',
    columns: [C_TIME, C_LEVEL, C_THREAD, C_LOGGER, C_MESSAGE],
    multiline: true,
    isEntryStart: (line) => matchTimestamp(line, ctx.year) !== null,
    scan(line) {
      const m = matchTimestamp(line, ctx.year);
      return m ? { level: detectLevel(line.slice(m.length, m.length + 120)), time: m.ms } : null;
    },
    quick(line) {
      const m = matchTimestamp(line, ctx.year);
      if (!m) return { level: detectLevel(line), time: null };
      return { level: detectLevel(line.slice(m.length, m.length + 160)), time: m.ms };
    },
    parse(entry): Record<string, string> {
      const line = firstLine(entry);
      const m = matchTimestamp(line, ctx.year);
      const fields: Record<string, string> = { time: '', level: '', thread: '', logger: '', message: '' };
      if (!m) {
        fields.message = entry;
        fields.level = levelText(detectLevel(line));
        return fields;
      }
      fields.time = line.slice(0, m.length).replace(/^\[|\]$/g, '');
      let r = line.slice(m.length);
      let level: Level = Level.None;
      for (let guard = 0; guard < 8; guard++) {
        r = r.replace(/^[\s|,;]+/, '');
        let t: RegExpExecArray | null;
        if (r.startsWith('[') || r.startsWith('(') || r.startsWith('<')) {
          const close = { '[': ']', '(': ')', '<': '>' }[r[0]]!;
          const end = r.indexOf(close);
          if (end < 0 || end > 120) break;
          const inner = r.slice(1, end).trim();
          const lv = parseLevel(inner);
          if (lv !== Level.None && !level) level = lv;
          else if (!fields.thread) fields.thread = inner;
          r = r.slice(end + 1);
        } else if (!level && (t = LEVEL_TOKEN.exec(r))) {
          level = parseLevel(t[1]);
          r = r.slice(t[0].length).replace(/^:/, '');
        } else if ((t = /^\d+\s+---\s+/.exec(r))) {
          // Spring Boot "<pid> --- [thread]"
          r = r.slice(t[0].length);
        } else if (!level && (t = /^[\w-]+\.([A-Za-z]+):\s/.exec(r)) && parseLevel(t[1])) {
          // Laravel/Monolog "local.ERROR: message"
          level = parseLevel(t[1]);
          r = r.slice(t[0].length);
        } else {
          break;
        }
      }
      r = r.replace(/^[\s|]+/, '');
      const lg = LOGGER_TOKEN.exec(r);
      if (lg && lg[1].length < 120 && !/^(https?|ftp)$/i.test(lg[1])) {
        fields.logger = lg[1].replace(/:$/, '');
        r = r.slice(lg[0].length);
      } else if (r.includes('|')) {
        const parts = r.split('|');
        if (parts.length >= 2 && parts[0].length < 120) {
          fields.logger = parts[0].trim();
          r = parts.slice(1).join('|');
        }
      }
      if (!level) level = detectLevel(line);
      fields.level = levelText(level);
      fields.message = r.trimStart() + restLines(entry);
      return fields;
    },
  };
}

// ---------------------------------------------------------------------- JSON

const KEYS = {
  time: ['@timestamp', 'timestamp', 'time', 'ts', '@t', 't', 'date', 'datetime', 'eventtime', 'asctime', 'logtime'],
  level: ['level', '@l', 'severity', 'lvl', 'loglevel', 'log.level', 'levelname', 'severity_text', 'priority'],
  message: ['message', 'msg', '@m', '@mt', 'text', 'log', 'event', 'body'],
  logger: ['logger', 'logger_name', 'loggername', 'name', 'category', 'source', 'sourcecontext', 'module', 'component'],
  thread: ['thread', 'thread_name', 'threadname', 'threadid', 'pid', 'tid'],
};

const ERROR_KEYS = ['exception', '@x', 'error', 'stack', 'stacktrace', 'err'];

const pick = (obj: Record<string, unknown>, keys: string[]): unknown => pickNamed(obj, keys)[0];

/** The first property matching one of the keys, and its name (a dotted path for nested keys). */
function pickNamed(obj: Record<string, unknown>, keys: string[]): [unknown, string | null] {
  for (const k of keys) {
    if (Object.hasOwn(obj, k)) return [obj[k], k];
  }
  const lower = new Map<string, string>();
  for (const k of Object.keys(obj)) if (!lower.has(k.toLowerCase())) lower.set(k.toLowerCase(), k);
  for (const k of keys) {
    const name = lower.get(k);
    if (name !== undefined) return [obj[name], name];
    if (k.includes('.')) {
      const [a, b] = k.split('.');
      const pa = lower.get(a);
      const v = pa === undefined ? undefined : obj[pa];
      if (v && typeof v === 'object' && Object.hasOwn(v, b)) return [(v as Record<string, unknown>)[b], `${pa}.${b}`];
    }
  }
  return [undefined, null];
}

const MAX_FIELDS = 60;

/** Adds the remaining properties as fields, nested objects flattened to dotted names (http.request.path). */
function addExtras(d: Record<string, string>, o: Record<string, unknown>, prefix: string, skip: Set<string>, depth: number, count: { n: number }) {
  for (const [k, v] of Object.entries(o)) {
    if (count.n >= MAX_FIELDS) return;
    const name = prefix + k;
    if (skip.has(name) || name === '__proto__') continue;
    if (v && typeof v === 'object' && !Array.isArray(v) && depth < 4) {
      addExtras(d, v as Record<string, unknown>, name + '.', skip, depth + 1, count);
      continue;
    }
    if (Object.hasOwn(d, name)) continue;
    d[name] = str(v);
    count.n++;
  }
}

const str = (v: unknown) => (v === undefined || v === null ? '' : typeof v === 'object' ? JSON.stringify(v) : String(v));

function tryJson(line: string): Record<string, unknown> | null {
  const t = line.trimStart();
  if (t.charCodeAt(0) !== 123 /* { */) return null;
  try {
    const v = JSON.parse(t);
    return v && typeof v === 'object' && !Array.isArray(v) ? v : null;
  } catch {
    return null;
  }
}

export function jsonFormat(): LogFormat {
  return {
    id: 'json',
    name: 'JSON lines (Serilog, pino, bunyan, ECS, Logstash…)',
    columns: [C_TIME, C_LEVEL, C_LOGGER, C_MESSAGE],
    multiline: false,
    isEntryStart: () => true,
    quick(line) {
      const o = tryJson(line);
      if (!o) return { level: detectLevel(line), time: null };
      return { level: parseLevel(pick(o, KEYS.level)), time: parseTimestamp(pick(o, KEYS.time)) };
    },
    parse(entry): Record<string, string> {
      const o = tryJson(entry);
      if (!o) return { time: '', level: levelText(detectLevel(entry)), logger: '', message: entry };
      const [msg, msgKey] = pickNamed(o, KEYS.message);
      const [ex, errKey] = pickNamed(o, ERROR_KEYS);
      const [time, timeKey] = pickNamed(o, KEYS.time);
      const [level, levelKey] = pickNamed(o, KEYS.level);
      const [logger, loggerKey] = pickNamed(o, KEYS.logger);
      let message = str(msg);
      if (ex) message += (message ? '\n' : '') + (typeof ex === 'object' ? str((ex as Record<string, unknown>).stack ?? ex) : str(ex));
      const d: Record<string, string> = {
        time: str(time),
        level: levelText(parseLevel(level)) || str(level),
        logger: str(logger),
        message: message || entry,
      };
      const skip = new Set([msgKey, errKey, timeKey, levelKey, loggerKey].filter((k): k is string => k !== null));
      addExtras(d, o, '', skip, 0, { n: Object.keys(d).length });
      return d;
    },
  };
}

// ------------------------------------------------------------------- syslog

const RFC3164 = /^([A-Z][a-z]{2} {1,2}\d{1,2} \d{2}:\d{2}:\d{2}(?:\.\d+)?) (\S+) ([^\s:[]+)(?:\[(\d+)\])?:? ?(.*)$/;

export function syslogFormat(ctx: FormatContext): LogFormat {
  return {
    id: 'syslog',
    name: 'Syslog (RFC 3164 / journalctl)',
    columns: [C_TIME, col('host', 'Host', 110), col('app', 'App', 130), col('pid', 'PID', 64), C_LEVEL, C_MESSAGE],
    multiline: true,
    isEntryStart: (line) => RFC3164.test(line),
    quick(line) {
      const m = RFC3164.exec(line);
      if (!m) return { level: detectLevel(line), time: null };
      return { level: detectLevel(m[5]), time: matchTimestamp(m[1], ctx.year)?.ms ?? null };
    },
    parse(entry): Record<string, string> {
      const m = RFC3164.exec(firstLine(entry));
      if (!m) return { message: entry };
      return { time: m[1], host: m[2], app: m[3], pid: m[4] ?? '', level: levelText(detectLevel(m[5])), message: m[5] + restLines(entry) };
    },
  };
}

const RFC5424 = /^<(\d{1,3})>1 (\S+) (\S+) (\S+) (\S+) (\S+) (-|\[.*?\](?:\[.*?\])*) ?(.*)$/;

export function syslog5424Format(): LogFormat {
  return {
    id: 'syslog5424',
    name: 'Syslog (RFC 5424)',
    columns: [C_TIME, col('host', 'Host', 110), col('app', 'App', 120), col('pid', 'PID', 64), col('msgid', 'MsgID', 80), C_LEVEL, C_MESSAGE],
    multiline: true,
    isEntryStart: (line) => RFC5424.test(line),
    quick(line) {
      const m = RFC5424.exec(line);
      if (!m) return { level: detectLevel(line), time: null };
      return { level: levelFromSyslogSeverity(Number(m[1]) % 8), time: parseTimestamp(m[2]) };
    },
    parse(entry): Record<string, string> {
      const m = RFC5424.exec(firstLine(entry));
      if (!m) return { message: entry };
      const sd = m[7] === '-' ? '' : m[7] + ' ';
      return {
        time: m[2], host: m[3], app: m[4], pid: m[5], msgid: m[6],
        level: levelText(levelFromSyslogSeverity(Number(m[1]) % 8)),
        message: sd + m[8] + restLines(entry),
      };
    },
  };
}

// ------------------------------------------------------- Apache / Nginx access

const ACCESS = /^(\S+) \S+ (\S+) \[([^\]]+)\] "([^"]*)" (\d{3}) (\S+)(?: "([^"]*)" "([^"]*)")?(.*)$/;

const statusLevel = (s: number) => (s >= 500 ? Level.Error : s >= 400 ? Level.Warn : Level.Info);

export function accessLogFormat(): LogFormat {
  return {
    id: 'access',
    name: 'Web access log (Apache / Nginx combined)',
    columns: [C_TIME, col('client', 'Client', 120), col('status', 'Status', 60), col('method', 'Method', 70), col('url', 'URL', 320), col('bytes', 'Bytes', 70), col('agent', 'User agent', 220), C_MESSAGE],
    multiline: false,
    isEntryStart: () => true,
    quick(line) {
      const m = ACCESS.exec(line);
      if (!m) return { level: detectLevel(line), time: null };
      return { level: statusLevel(+m[5]), time: matchTimestamp(m[3])?.ms ?? null };
    },
    parse(entry): Record<string, string> {
      const m = ACCESS.exec(entry);
      if (!m) return { message: entry };
      const [method, url, proto] = m[4].split(' ');
      return {
        time: m[3], client: m[1], user: m[2], status: m[5], method: method ?? '', url: url ?? m[4], protocol: proto ?? '',
        bytes: m[6], referer: m[7] ?? '', agent: m[8] ?? '', level: levelText(statusLevel(+m[5])), message: m[4],
      };
    },
  };
}

// ------------------------------------------------------------- W3C / IIS

export function w3cFormat(fieldNames: string[]): LogFormat {
  const idx = (n: string) => fieldNames.indexOf(n);
  const iDate = idx('date'), iTime = idx('time'), iStatus = idx('sc-status');
  const columns: ColumnDef[] = [C_TIME, C_LEVEL];
  for (const f of fieldNames) {
    if (f === 'date' || f === 'time') continue;
    columns.push(col(f, f, f === 'cs-uri-stem' ? 260 : f === 'cs(User-Agent)' ? 200 : 100));
  }
  columns[columns.length - 1] = { ...columns[columns.length - 1], width: 0 };
  const timeOf = (p: string[]) => {
    const raw = `${iDate >= 0 ? p[iDate] : '1970-01-01'} ${iTime >= 0 ? p[iTime] : '00:00:00'}`;
    return { raw, ms: parseTimestamp(raw.replace(' ', 'T') + 'Z') };
  };
  const levelOf = (p: string[]) => (iStatus >= 0 ? statusLevel(+p[iStatus]) : Level.Info);
  return {
    id: 'w3c',
    name: 'W3C extended (IIS)',
    columns,
    multiline: false,
    isEntryStart: () => true,
    quick(line) {
      if (line.startsWith('#')) return { level: Level.None, time: null };
      const p = line.split(' ');
      return { level: levelOf(p), time: timeOf(p).ms };
    },
    parse(entry): Record<string, string> {
      if (entry.startsWith('#')) return { message: entry };
      const p = entry.split(' ');
      const out: Record<string, string> = { time: timeOf(p).raw, level: levelText(levelOf(p)), message: entry };
      fieldNames.forEach((f, i) => {
        if (f !== 'date' && f !== 'time') out[f] = p[i] ?? '';
      });
      return out;
    },
  };
}

// ------------------------------------------------------------ Android logcat

const LOGCAT = /^(\d{2})-(\d{2}) (\d{2}):(\d{2}):(\d{2})\.(\d{3})\s+(\d+)\s+(\d+) ([VDIWEFA]) ([^:]*?)\s*: (.*)$/;

export function logcatFormat(ctx: FormatContext): LogFormat {
  return {
    id: 'logcat',
    name: 'Android logcat (threadtime)',
    columns: [C_TIME, col('pid', 'PID', 60), col('tid', 'TID', 60), C_LEVEL, col('tag', 'Tag', 160), C_MESSAGE],
    multiline: false,
    isEntryStart: () => true,
    quick(line) {
      const m = LOGCAT.exec(line);
      if (!m) return { level: detectLevel(line), time: null };
      return { level: parseLevel(m[9] === 'A' ? 'F' : m[9]), time: new Date(ctx.year, +m[1] - 1, +m[2], +m[3], +m[4], +m[5], +m[6]).getTime() };
    },
    parse(entry): Record<string, string> {
      const m = LOGCAT.exec(entry);
      if (!m) return { message: entry };
      return {
        time: `${m[1]}-${m[2]} ${m[3]}:${m[4]}:${m[5]}.${m[6]}`, pid: m[7], tid: m[8],
        level: levelText(parseLevel(m[9] === 'A' ? 'F' : m[9])), tag: m[10], message: m[11],
      };
    },
  };
}

// ---------------------------------------------------------- custom (user regex)

export function customFormat(def: CustomFormat, ctx: FormatContext): LogFormat {
  const re = new RegExp(def.pattern);
  const groups = [...def.pattern.matchAll(/\(\?<([A-Za-z_]\w*)>/g)].map((m) => m[1]);
  const columns: ColumnDef[] = groups.map((g) => {
    const key = g === 'timestamp' ? 'time' : g;
    const known = { time: C_TIME, level: C_LEVEL, thread: C_THREAD, logger: C_LOGGER, message: C_MESSAGE }[key];
    return known ?? col(key, g[0].toUpperCase() + g.slice(1), 120);
  });
  if (!columns.some((c) => c.width === 0) && columns.length) columns[columns.length - 1] = { ...columns[columns.length - 1], width: 0 };
  const norm = (g: Record<string, string> | undefined) => {
    const out: Record<string, string> = {};
    for (const [k, v] of Object.entries(g ?? {})) out[k === 'timestamp' ? 'time' : k] = v ?? '';
    return out;
  };
  return {
    id: `custom:${def.name}`,
    name: def.name,
    columns,
    multiline: true,
    isEntryStart: (line) => re.test(line),
    quick(line) {
      const m = re.exec(line);
      if (!m) return { level: detectLevel(line), time: null };
      const g = norm(m.groups);
      const ts = g.time ? matchTimestamp(g.time, ctx.year, true)?.ms ?? parseTimestamp(g.time) : null;
      return { level: g.level ? parseLevel(g.level) : detectLevel(line), time: ts };
    },
    parse(entry): Record<string, string> {
      const m = re.exec(firstLine(entry));
      if (!m) return { message: entry };
      const g = norm(m.groups);
      if ('message' in g) g.message += restLines(entry);
      else g.message = entry;
      return g;
    },
  };
}

// ---------------------------------------------------------------- detection

export interface DetectOptions {
  year?: number;
  customFormats?: CustomFormat[];
  /** Force a format id instead of detecting. */
  force?: string;
}

/** Picks the best format for a sample of lines from the start of a file. */
export function detectFormat(sample: string[], opts: DetectOptions = {}): LogFormat {
  const ctx: FormatContext = { year: opts.year ?? new Date().getFullYear() };
  const lines = sample.filter((l) => l.trim().length > 0).slice(0, 500);

  const w3cFields = lines.find((l) => l.startsWith('#Fields:'));
  const candidates: { f: LogFormat; min: number; test: (l: string) => boolean }[] = [];
  for (const def of opts.customFormats ?? []) {
    try {
      const f = customFormat(def, ctx);
      candidates.push({ f, min: 0.5, test: f.isEntryStart });
    } catch {
      /* invalid user regex: skip */
    }
  }
  if (w3cFields) {
    const f = w3cFormat(w3cFields.slice(8).trim().split(/\s+/));
    candidates.push({ f, min: 0, test: () => true });
  }
  candidates.push(
    { f: jsonFormat(), min: 0.6, test: (l) => tryJson(l) !== null },
    { f: accessLogFormat(), min: 0.6, test: (l) => ACCESS.test(l) },
    { f: syslog5424Format(), min: 0.6, test: (l) => RFC5424.test(l) },
    { f: logcatFormat(ctx), min: 0.6, test: (l) => LOGCAT.test(l) },
    { f: syslogFormat(ctx), min: 0.5, test: (l) => RFC3164.test(l) },
    { f: timestampedFormat(ctx), min: 0.1, test: (l) => matchTimestamp(l, ctx.year) !== null },
  );

  if (opts.force) {
    if (opts.force === 'plain') return plainFormat();
    const forced = candidates.find((c) => c.f.id === opts.force);
    if (forced) return forced.f;
  }
  if (!lines.length) return plainFormat();
  for (const c of candidates) {
    const hits = lines.reduce((n, l) => n + (c.test(l) ? 1 : 0), 0);
    // The first non-empty line must start an entry, otherwise we'd be guessing.
    if (hits / lines.length >= c.min && hits > 0 && (c.min === 0 || c.test(lines[0]) || hits / lines.length > 0.5)) return c.f;
  }
  return plainFormat();
}

export const FORMAT_IDS = ['json', 'access', 'syslog5424', 'logcat', 'syslog', 'timestamped', 'plain'] as const;
