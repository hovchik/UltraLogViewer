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
    if (name === '__proto__') continue;
    // A property already shown as time/level/logger/message/error is skipped, but its children still count.
    if (skip.has(name) && !(v && typeof v === 'object' && !Array.isArray(v))) continue;
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

// ------------------------------------------------------------------ logfmt
//
// Built-in formats for databases, Rust, macOS, Laravel and logfmt. Each regex is written from the format's
// documented default layout; test/fixtures/format-samples.json shows what each one accepts.

/** logfmt `key=value` pairs: values are bare up to a space, or "quoted" with backslash escapes. */
export function logfmtPairs(line: string, requireEquals = false): [string, string][] {
  const out: [string, string][] = [];
  let i = 0;
  const isSep = (c: string) => c === ' ' || c === '\t';
  while (i < line.length) {
    while (i < line.length && isSep(line[i])) i++;
    const ks = i;
    while (i < line.length && !isSep(line[i]) && line[i] !== '=' && line[i] !== '"') i++;
    const key = line.slice(ks, i);
    if (!key) {
      if (i < line.length) i++; // stray '=' or '"'
      continue;
    }
    if (i >= line.length || line[i] !== '=') {
      if (!requireEquals) out.push([key, '']);
      continue;
    }
    i++;
    let value = '';
    if (line[i] === '"') {
      i++;
      while (i < line.length && line[i] !== '"') {
        if (line[i] === '\\' && i + 1 < line.length) {
          i++;
          value += line[i] === 'n' ? '\n' : line[i] === 't' ? '\t' : line[i];
        } else value += line[i];
        i++;
      }
      i++;
    } else {
      const vs = i;
      while (i < line.length && !isSep(line[i])) i++;
      value = line.slice(vs, i);
    }
    out.push([key, value]);
  }
  return out;
}

const LOGFMT_KEYS = {
  time: ['ts', 'time', 't', 'timestamp', '@timestamp', 'date', 'datetime'],
  level: ['level', 'lvl', 'severity', 'loglevel', 'log.level'],
  message: ['msg', 'message', 'event'],
  logger: ['logger', 'component', 'module', 'name', 'source'],
};
const LOGFMT_START = /^[A-Za-z_@][\w.@/-]*=/;

/** A logfmt line: starts with key=value, has at least two pairs, and one of them is a time, level or message key. */
function isLogfmt(line: string): boolean {
  if (!LOGFMT_START.test(line)) return false;
  const pairs = logfmtPairs(line, true);
  if (pairs.length < 2) return false;
  const known = [...LOGFMT_KEYS.time, ...LOGFMT_KEYS.level, ...LOGFMT_KEYS.message];
  return pairs.some(([k]) => known.includes(k.toLowerCase()));
}

function pickPair(pairs: [string, string][], keys: string[]): [string, string | null] {
  for (const k of keys) for (const [pk, v] of pairs) if (pk.toLowerCase() === k) return [v, pk];
  return ['', null];
}

export function logfmtFormat(): LogFormat {
  return {
    id: 'logfmt',
    name: 'logfmt (key=value: Go kit, logrus, Grafana, Heroku…)',
    columns: [C_TIME, C_LEVEL, C_LOGGER, C_MESSAGE],
    multiline: false,
    isEntryStart: () => true,
    quick(line) {
      const pairs = logfmtPairs(line, true);
      if (!pairs.length) return { level: detectLevel(line), time: null };
      const [lv, lk] = pickPair(pairs, LOGFMT_KEYS.level);
      return { level: lk === null ? detectLevel(line) : parseLevel(lv), time: parseTimestamp(pickPair(pairs, LOGFMT_KEYS.time)[0] || null) };
    },
    parse(entry) {
      const pairs = logfmtPairs(entry, true);
      const [time, tk] = pickPair(pairs, LOGFMT_KEYS.time);
      const [level, lk] = pickPair(pairs, LOGFMT_KEYS.level);
      const [msg, mk] = pickPair(pairs, LOGFMT_KEYS.message);
      const [logger, gk] = pickPair(pairs, LOGFMT_KEYS.logger);
      const lvl = levelText(lk === null ? detectLevel(entry) : parseLevel(level));
      const d: Record<string, string> = { time, level: lvl || level, logger, message: mk === null ? entry : msg };
      for (const [k, v] of pairs) {
        if (k === tk || k === lk || k === mk || k === gk || k === '__proto__' || Object.hasOwn(d, k) || Object.keys(d).length >= MAX_FIELDS) continue;
        d[k] = v;
      }
      return d;
    },
  };
}

// ------------------------------------------------------------ Rust tracing

// Microsecond (or finer) RFC 3339 time, as tracing prints it; generic "ISO INFO name: text" lines stay "timestamped".
const RUST = /^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{6,9}(?:Z|[+-]\d{2}:\d{2})) {1,2}(TRACE|DEBUG|INFO|WARN|ERROR) (?:((?:[\w-]+(?:\{[^}]*\})?)(?::[\w-]+(?:\{[^}]*\})?)*): )??([A-Za-z_]\w*(?:::\w+)*): (.*)$/;
const RUST_FIELD = /(?<![\w.])([A-Za-z_][\w.]*)=("(?:[^"\\]|\\.)*"|[^\s{}]+)/g;

/**
 * Rust `tracing_subscriber::fmt` default output:
 *   2024-05-01T10:00:00.123456Z  INFO request{id=7}:db: my_app::store: saved rows=3
 * Span fields and trailing key=value fields become fields.
 */
export function rustTracingFormat(): LogFormat {
  return {
    id: 'rust',
    name: 'Rust tracing (fmt)',
    columns: [C_TIME, C_LEVEL, col('spans', 'Spans', 160), col('target', 'Target', 180), C_MESSAGE],
    multiline: true,
    isEntryStart: (line) => RUST.test(line),
    quick(line) {
      const m = RUST.exec(line);
      return m ? { level: parseLevel(m[2]), time: parseTimestamp(m[1]) } : { level: detectLevel(line), time: null };
    },
    parse(entry) {
      const m = RUST.exec(firstLine(entry));
      if (!m) return { message: entry };
      const d: Record<string, string> = { time: m[1], level: levelText(parseLevel(m[2])), spans: m[3] ?? '', target: m[4], message: m[5] + restLines(entry) };
      for (const f of `${m[3] ?? ''} ${m[5]}`.matchAll(RUST_FIELD)) {
        const v = f[2];
        if (!Object.hasOwn(d, f[1]) && f[1] !== '__proto__') d[f[1]] = v.length > 1 && v[0] === '"' ? v.slice(1, -1).replace(/\\"/g, '"').replace(/\\\\/g, '\\') : v;
      }
      return d;
    },
  };
}

// --------------------------------------------------------------- PostgreSQL

const POSTGRES =
  /^(?<time>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}(?:\.\d+)?)(?: (?<tz>[A-Z]{2,5}|[+-]\d{2}(?::?\d{2})?))? \[(?<pid>\d+)\](?::? \[\d+(?:-\d+)?\])? (?:user=(?<user>[^,\s]*),db=(?<db>[^,\s]*)(?:,app=(?<app>[^,]*?))?(?:,client=(?<client>\S*?))? |(?<user2>[^@\s]*)@(?<db2>\S*) )?(?<sev>DEBUG[1-5]?|LOG|INFO|NOTICE|WARNING|ERROR|FATAL|PANIC|DETAIL|HINT|STATEMENT|CONTEXT|QUERY|LOCATION): {1,2}(?<msg>.*)$/;
const PG_DETAIL = new Set(['DETAIL', 'HINT', 'STATEMENT', 'CONTEXT', 'QUERY', 'LOCATION']);

function pgSeverity(sev: string): Level {
  if (sev === 'LOG' || sev === 'INFO' || sev === 'NOTICE') return Level.Info;
  if (sev === 'WARNING') return Level.Warn;
  if (sev === 'ERROR') return Level.Error;
  if (sev === 'FATAL' || sev === 'PANIC') return Level.Fatal;
  return sev.startsWith('DEBUG') ? Level.Debug : Level.None;
}

function pgTime(g: Record<string, string | undefined>): number | null {
  const tz = g.tz ?? '';
  const iso = g.time!.replace(' ', 'T');
  // Named zones other than UTC/GMT are read as local time.
  return parseTimestamp(tz === 'UTC' || tz === 'GMT' ? iso + 'Z' : tz && (tz[0] === '+' || tz[0] === '-') ? iso + (tz.length === 3 ? tz + ':00' : tz) : iso);
}

/**
 * PostgreSQL with the default `log_line_prefix = '%m [%p] '` (Debian: `'%m [%p] %q%u@%d '`, pgBadger:
 * `'%t [%p]: [%l-1] user=%u,db=%d,app=%a,client=%h '`). DETAIL/HINT/STATEMENT/CONTEXT lines belong to the message
 * above them, so an ERROR keeps its statement.
 */
export function postgresFormat(): LogFormat {
  const scan = (line: string): QuickInfo | null => {
    const m = POSTGRES.exec(line);
    if (!m || PG_DETAIL.has(m.groups!.sev)) return null;
    return { level: pgSeverity(m.groups!.sev), time: pgTime(m.groups!) };
  };
  return {
    id: 'postgres',
    name: 'PostgreSQL',
    columns: [C_TIME, col('pid', 'PID', 64), col('user', 'User', 90), col('db', 'DB', 90), C_LEVEL, C_MESSAGE],
    multiline: true,
    isEntryStart: (line) => scan(line) !== null,
    scan,
    quick: (line) => scan(line) ?? { level: detectLevel(line), time: null },
    parse(entry) {
      const m = POSTGRES.exec(firstLine(entry));
      if (!m) return { message: entry };
      const g = m.groups!;
      const d: Record<string, string> = {
        time: g.time + (g.tz ? ' ' + g.tz : ''),
        pid: g.pid,
        user: g.user ?? g.user2 ?? '',
        db: g.db ?? g.db2 ?? '',
        level: levelText(pgSeverity(g.sev)),
        severity: g.sev,
        message: g.msg + restLines(entry),
      };
      if (g.app !== undefined) d.app = g.app;
      if (g.client !== undefined) d.client = g.client;
      const dm = /\bduration: (\d+(?:\.\d+)? ms)/.exec(g.msg);
      if (dm) d.duration = dm[1];
      return d;
    },
  };
}

// -------------------------------------------------------------------- MySQL

const MYSQL_ERROR = /^(\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})?) +(\d+) \[(System|Note|Warning|Error|ERROR|Warn)\](?: \[(MY-\d+)\])?(?: \[(\w+)\])? ?(.*)$/;
const mysqlLevel = (s: string) => (s === 'Warning' || s === 'Warn' ? Level.Warn : s === 'Error' || s === 'ERROR' ? Level.Error : Level.Info);

/** MySQL / MariaDB error log: `2024-05-01T10:00:00.123456Z 0 [Warning] [MY-010068] [Server] CA certificate is self signed.` */
export function mysqlErrorFormat(): LogFormat {
  return {
    id: 'mysql-error',
    name: 'MySQL error log',
    columns: [C_TIME, col('thread', 'Thread', 64), C_LEVEL, col('code', 'Code', 100), col('subsystem', 'Subsystem', 90), C_MESSAGE],
    multiline: true,
    isEntryStart: (line) => MYSQL_ERROR.test(line),
    quick(line) {
      const m = MYSQL_ERROR.exec(line);
      return m ? { level: mysqlLevel(m[3]), time: parseTimestamp(m[1]) } : { level: detectLevel(line), time: null };
    },
    parse(entry): Record<string, string> {
      const m = MYSQL_ERROR.exec(firstLine(entry));
      if (!m) return { message: entry };
      return { time: m[1], thread: m[2], level: levelText(mysqlLevel(m[3])), code: m[4] ?? '', subsystem: m[5] ?? '', message: m[6] + restLines(entry) };
    },
  };
}

/** Lines MySQL writes at the top of the general and slow query logs, and again after every restart. */
const MYSQL_PREAMBLE = /^(?:.*mysqld(?:\.exe)?, Version: .*started with:|Tcp port: \d+ .*|Time +Id +Command +Argument)\s*$/;
const MYSQL_GENERAL =
  /^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2}))\s+(\d+) (Connect|Query|Quit|Init DB|Prepare|Execute|Close stmt|Reset stmt|Field List|Statistics|Ping|Shutdown|Change user|Binlog Dump|Long Data|Refresh|Kill|Debug|Processlist|Set option|Fetch|Daemon|Error|Sleep|Time|Register Slave|Connect Out|Create DB|Drop DB)(?:\t(.*))?$/;
const isMysqlGeneral = (line: string) => MYSQL_GENERAL.test(line) || MYSQL_PREAMBLE.test(line);
const generalLevel = (command: string, arg: string) =>
  command === 'Error' ? Level.Error : command === 'Connect' && arg.startsWith('Access denied') ? Level.Warn : Level.Info;

/** MySQL general query log: `2024-05-01T10:00:00.123456Z\t    8 Query\tSELECT 1`. */
export function mysqlGeneralFormat(): LogFormat {
  return {
    id: 'mysql-general',
    name: 'MySQL general query log',
    columns: [C_TIME, col('id', 'Thread', 64), col('command', 'Command', 90), C_LEVEL, C_MESSAGE],
    multiline: true,
    isEntryStart: isMysqlGeneral,
    quick(line) {
      const m = MYSQL_GENERAL.exec(line);
      return m ? { level: generalLevel(m[3], m[4] ?? ''), time: parseTimestamp(m[1]) } : { level: Level.None, time: null };
    },
    parse(entry): Record<string, string> {
      const m = MYSQL_GENERAL.exec(firstLine(entry));
      if (!m) return { message: entry };
      return { time: m[1], id: m[2], command: m[3], level: levelText(generalLevel(m[3], m[4] ?? '')), message: (m[4] ?? '') + restLines(entry) };
    },
  };
}

const SLOW_TIME = /^# Time: (?:(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})?)|(\d{2})(\d{2})(\d{2}) +(\d{1,2}):(\d{2}):(\d{2}))\s*$/;
const SLOW_USER = /^# User@Host: (\S*?)\[[^\]]*\] @ (\S*) \[([^\]]*)\](?:\s+Id:\s*(\d+))?/;

function slowTime(m: RegExpExecArray): number | null {
  return m[1] ? parseTimestamp(m[1]) : new Date(2000 + +m[2], +m[3] - 1, +m[4], +m[5], +m[6], +m[7]).getTime();
}

/**
 * MySQL slow query log. Each entry starts at `# Time:` and holds the `# User@Host` and `# Query_time` lines and the
 * statement. Servers that print `# Time:` only when the second changes (MySQL 5.6, MariaDB) put queries of the same
 * second in one entry.
 */
export function mysqlSlowFormat(): LogFormat {
  return {
    id: 'mysql-slow',
    name: 'MySQL slow query log',
    columns: [C_TIME, col('query_time', 'Query time', 90), col('rows_examined', 'Rows examined', 100), col('user', 'User', 90), col('db', 'DB', 80), C_MESSAGE],
    multiline: true,
    isEntryStart: (line) => line.startsWith('# Time: ') || MYSQL_PREAMBLE.test(line),
    quick(line) {
      const m = SLOW_TIME.exec(line);
      return m ? { level: Level.Info, time: slowTime(m) } : { level: Level.None, time: null };
    },
    parse(entry) {
      const lines = entry.split('\n');
      if (!SLOW_TIME.test(lines[0].replace(/\r$/, ''))) return { message: entry };
      const d: Record<string, string> = { time: lines[0].slice(8).trim(), query_time: '', rows_examined: '', user: '', db: '', level: levelText(Level.Info), message: '' };
      const sql: string[] = [];
      for (const raw of lines.slice(1)) {
        const l = raw.replace(/\r$/, '');
        let m: RegExpExecArray | null;
        if ((m = SLOW_USER.exec(l))) {
          d.user = m[1];
          d.host = m[2] || m[3];
          if (m[4] !== undefined) d.id = m[4];
        } else if (l.startsWith('#')) {
          // "# Query_time: 2.5  Lock_time: 0.0001 Rows_sent: 1  Rows_examined: 100000", MariaDB "# Schema: shop ..."
          for (const s of l.matchAll(/(\w+): (\S+)/g)) {
            const k = s[1].toLowerCase();
            d[k === 'schema' ? 'db' : k] = s[2];
          }
        } else if ((m = /^use (\w+);$/i.exec(l))) d.db = m[1];
        else if (!/^SET timestamp=/i.test(l) && l) sql.push(l);
      }
      d.message = sql.length ? sql.join('\n') : entry;
      return d;
    },
  };
}

// ------------------------------------------------------------------ MongoDB

const MONGO_HEAD = /^\s*\{"t":\{"\$date":"([^"]+)"\},\s*"s":"(\w+)"/;

/** F fatal, E error, W warning, I info, D1 debug, D2–D5 more verbose debug (shown as trace). */
function mongoSeverity(s: string): Level {
  switch (s) {
    case 'F':
      return Level.Fatal;
    case 'E':
      return Level.Error;
    case 'W':
      return Level.Warn;
    case 'I':
      return Level.Info;
    case 'D':
    case 'D1':
      return Level.Debug;
    default:
      return s.length === 2 && s[0] === 'D' ? Level.Trace : Level.None;
  }
}

/** MongoDB 4.4+ structured log: `{"t":{"$date":"…"},"s":"I","c":"NETWORK","id":22943,"ctx":"listener","msg":"…","attr":{…}}`. */
export function mongoFormat(): LogFormat {
  return {
    id: 'mongodb',
    name: 'MongoDB (4.4+ JSON)',
    columns: [C_TIME, C_LEVEL, col('component', 'Component', 90), col('ctx', 'Context', 110), col('id', 'ID', 64), C_MESSAGE],
    multiline: false,
    isEntryStart: () => true,
    quick(line) {
      const m = MONGO_HEAD.exec(line);
      return m ? { level: mongoSeverity(m[2]), time: parseTimestamp(m[1]) } : { level: detectLevel(line), time: null };
    },
    parse(entry) {
      const o = tryJson(entry);
      if (!o) return { message: entry };
      const t = o.t;
      const time = t && typeof t === 'object' ? str((t as Record<string, unknown>).$date) : str(t);
      const d: Record<string, string> = { time, level: levelText(mongoSeverity(str(o.s))), component: str(o.c), ctx: str(o.ctx), id: str(o.id), message: str(o.msg) };
      addExtras(d, o, '', new Set(['s', 'c', 'ctx', 'id', 'msg']), 0, { n: Object.keys(d).length });
      delete d['t.$date']; // already the time
      return d;
    },
  };
}

// ---------------------------------------------------------- macOS unified log

const MACOS = /^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d+[+-]\d{4})\s+(0x[0-9a-f]+)\s+(\w+)\s+(0x[0-9a-f]+)\s+(\d+)\s+(?:(\d+)\s+)?(.*)$/;
const MACOS_REST = /^(.+?)(?:\[\d+\])?: (?:\(([^)]*)\) )?(?:\[([^\]:]+)(?::([^\]]*))?\] )?([\s\S]*)$/;
const MACOS_PREAMBLE = /^(?:Filtering the log data using .*|Skipping info and debug messages.*|Timestamp +Thread +Type +Activity +PID.*|-{20,}|Log +- Default:.*)$/;
const isMacos = (line: string) => MACOS.test(line) || MACOS_PREAMBLE.test(line);
const macLevel = (type: string) =>
  type === 'Debug' ? Level.Debug : type === 'Info' || type === 'Default' ? Level.Info : type === 'Error' ? Level.Error : type === 'Fault' ? Level.Fatal : Level.None;

/** macOS unified log as printed by `log show` (default style). */
export function macosFormat(): LogFormat {
  return {
    id: 'macos',
    name: 'macOS unified log (log show)',
    columns: [C_TIME, C_LEVEL, col('process', 'Process', 140), col('pid', 'PID', 64), col('subsystem', 'Subsystem', 160), C_MESSAGE],
    multiline: true,
    isEntryStart: isMacos,
    quick(line) {
      const m = MACOS.exec(line);
      return m ? { level: macLevel(m[3]), time: parseTimestamp(m[1]) } : { level: Level.None, time: null };
    },
    parse(entry) {
      const m = MACOS.exec(firstLine(entry));
      if (!m) return { message: entry };
      const d: Record<string, string> = {
        time: m[1], level: levelText(macLevel(m[3])), process: '', pid: m[5], subsystem: '', message: m[7] + restLines(entry), thread: m[2], type: m[3], activity: m[4],
      };
      if (m[6] !== undefined) d.ttl = m[6];
      const r = MACOS_REST.exec(m[7] + restLines(entry));
      if (r) {
        d.process = r[1];
        if (r[2] !== undefined) d.library = r[2];
        d.subsystem = r[3] ?? '';
        if (r[4] !== undefined) d.category = r[4];
        d.message = r[5];
      }
      return d;
    },
  };
}

// ------------------------------------------------------------------ Laravel

const LARAVEL = /^\[(\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:?\d{2})?)\] ([\w-]+)\.(DEBUG|INFO|NOTICE|WARNING|ERROR|CRITICAL|ALERT|EMERGENCY): (.*)$/;

/** Laravel (Monolog line format): `[2024-05-01 10:00:00] production.ERROR: message {"exception":"…"} []` plus a stack trace. */
export function laravelFormat(): LogFormat {
  return {
    id: 'laravel',
    name: 'Laravel',
    columns: [C_TIME, col('channel', 'Env', 90), C_LEVEL, C_MESSAGE],
    multiline: true,
    isEntryStart: (line) => LARAVEL.test(line),
    quick(line) {
      const m = LARAVEL.exec(line);
      return m ? { level: parseLevel(m[3]), time: parseTimestamp(m[1]) } : { level: detectLevel(line), time: null };
    },
    parse(entry) {
      const m = LARAVEL.exec(firstLine(entry));
      if (!m) return { message: entry };
      const d: Record<string, string> = { time: m[1], channel: m[2], level: levelText(parseLevel(m[3])), message: m[4] + restLines(entry) };
      const e = /"exception":"\[object\] \(([\w\\]+)\(code: (-?\d+)\)/.exec(m[4]);
      if (e) {
        d.exception = e[1].replace(/\\\\/g, '\\');
        d.code = e[2];
      }
      return d;
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

/** The chosen format and how it was chosen, shown to the user as the file's format. */
export interface FormatDetection {
  format: LogFormat;
  /** Picked by detection (false: the user chose it). */
  auto: boolean;
  /** Non-empty sample lines that start an entry in this format, out of `sampled`. */
  matched: number;
  sampled: number;
}

/** Picks the best format for a sample of lines from the start of a file. */
export function detectFormat(sample: string[], opts: DetectOptions = {}): LogFormat {
  return detectFormatInfo(sample, opts).format;
}

/** detectFormat, plus how well the sample fits the chosen format. */
export function detectFormatInfo(sample: string[], opts: DetectOptions = {}): FormatDetection {
  const ctx: FormatContext = { year: opts.year ?? new Date().getFullYear() };
  const lines = sample.filter((l) => l.trim().length > 0).slice(0, 500);
  const sampled = lines.length;
  const plain = (auto: boolean): FormatDetection => ({ format: plainFormat(), auto, matched: sampled, sampled });

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
  // Order matters: specific formats come before the general ones that would also accept their lines (MongoDB before
  // JSON; the timestamp-first formats before "timestamped"). The slow query log is mostly SQL lines, so it is only
  // picked when its "# Query_time:" lines are there (it can always be chosen by hand). Multi-line formats need fewer
  // matching lines (stack traces, SQL), but their first line must match.
  const slow = mysqlSlowFormat();
  candidates.push(
    { f: slow, min: lines.some((l) => l.startsWith('# Query_time:')) ? 0 : 2, test: slow.isEntryStart },
    { f: mongoFormat(), min: 0.6, test: (l) => MONGO_HEAD.test(l) },
    { f: jsonFormat(), min: 0.6, test: (l) => tryJson(l) !== null },
    { f: accessLogFormat(), min: 0.6, test: (l) => ACCESS.test(l) },
    { f: syslog5424Format(), min: 0.6, test: (l) => RFC5424.test(l) },
    { f: logcatFormat(ctx), min: 0.6, test: (l) => LOGCAT.test(l) },
    { f: syslogFormat(ctx), min: 0.5, test: (l) => RFC3164.test(l) },
    { f: logfmtFormat(), min: 0.6, test: isLogfmt },
    { f: postgresFormat(), min: 0.3, test: (l) => POSTGRES.test(l) },
    { f: mysqlErrorFormat(), min: 0.3, test: (l) => MYSQL_ERROR.test(l) },
    { f: mysqlGeneralFormat(), min: 0.3, test: isMysqlGeneral },
    { f: rustTracingFormat(), min: 0.3, test: (l) => RUST.test(l) },
    { f: macosFormat(), min: 0.3, test: isMacos },
    { f: laravelFormat(), min: 0.3, test: (l) => LARAVEL.test(l) },
    { f: timestampedFormat(ctx), min: 0.1, test: (l) => matchTimestamp(l, ctx.year) !== null },
  );
  const count = (test: (l: string) => boolean) => lines.reduce((n, l) => n + (test(l) ? 1 : 0), 0);

  if (opts.force) {
    if (opts.force === 'plain') return plain(false);
    const forced = candidates.find((c) => c.f.id === opts.force);
    if (forced) return { format: forced.f, auto: false, matched: count(forced.test), sampled };
  }
  if (!lines.length) return plain(true);
  for (const c of candidates) {
    const hits = count(c.test);
    // The first non-empty line must start an entry, otherwise we'd be guessing.
    if (hits / lines.length >= c.min && hits > 0 && (c.min === 0 || c.test(lines[0]) || hits / lines.length > 0.5)) {
      return { format: c.f, auto: true, matched: hits, sampled };
    }
  }
  return plain(true);
}

export const FORMAT_IDS = [
  'json', 'access', 'syslog5424', 'logcat', 'syslog', 'timestamped', 'logfmt', 'postgres', 'mysql-error', 'mysql-general', 'mysql-slow', 'mongodb', 'rust', 'macos', 'laravel', 'plain',
] as const;

/** Short names for the format picker and the status bar. */
export const FORMAT_LABELS: Record<string, string> = {
  timestamped: 'Timestamped text',
  json: 'JSON lines',
  syslog: 'Syslog RFC 3164',
  syslog5424: 'Syslog RFC 5424',
  access: 'Apache / Nginx',
  w3c: 'W3C / IIS',
  logcat: 'Android logcat',
  logfmt: 'logfmt',
  postgres: 'PostgreSQL',
  'mysql-error': 'MySQL error log',
  'mysql-general': 'MySQL general log',
  'mysql-slow': 'MySQL slow log',
  mongodb: 'MongoDB JSON',
  rust: 'Rust tracing',
  macos: 'macOS unified log',
  laravel: 'Laravel',
  plain: 'Plain text',
};

export function formatLabel(id: string, name: string): string {
  return FORMAT_LABELS[id] ?? (id.startsWith('custom:') ? `Custom: ${name}` : name);
}
