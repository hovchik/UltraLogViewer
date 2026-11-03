// Syslog parsing (RFC 5424 and RFC 3164), the normalised line the receivers write, the field names the formats show,
// and the TCP framing of RFC 6587. Network input is untrusted: every parser here accepts any text and falls back to
// "the whole text is the message".

export interface SyslogMessage {
  /** The PRI value 0–191, or -1 when the message had none. */
  pri: number;
  /** 5424, 3164, or 0 when no header was recognised (the whole text is the message). */
  rfc: 0 | 3164 | 5424;
  time: string;
  host: string;
  app: string;
  pid: string;
  msgid: string;
  /** The structured data as written ('' for none), and its parameters as "SD-ID.name" = value. */
  structuredData: string;
  params: [string, string][];
  message: string;
}

export const FACILITIES = [
  'kern', 'user', 'mail', 'daemon', 'auth', 'syslog', 'lpr', 'news', 'uucp', 'cron', 'authpriv', 'ftp', 'ntp', 'security',
  'console', 'solaris-cron', 'local0', 'local1', 'local2', 'local3', 'local4', 'local5', 'local6', 'local7',
];
export const SEVERITIES = ['emerg', 'alert', 'crit', 'err', 'warning', 'notice', 'info', 'debug'];
const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

export const facilityName = (pri: number) => (pri < 0 ? '' : FACILITIES[Math.min(Math.floor(pri / 8), FACILITIES.length - 1)]);
export const severityName = (pri: number) => (pri < 0 ? '' : SEVERITIES[pri % 8]);

const BSD_TIME = /^([A-Z][a-z]{2}) {1,2}(\d{1,2}) (\d{2}):(\d{2}):(\d{2})(?:\.(\d{1,6}))? /;
const ISO_TIME = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,9})?(?:Z|[+-]\d{2}:\d{2}) /;
const TAG = /^([^\s:[\]]{1,64})(?:\[([^\]\s]{1,32})\])?:(?: |$)/;

const isDigit = (c: string | undefined) => c !== undefined && c >= '0' && c <= '9';

/** "<PRI>" at the start: 1–3 digits, no leading zero, at most 191. Returns [length taken (0 = none), pri]. */
function readPri(s: string): [number, number] {
  if (s.length < 3 || s[0] !== '<') return [0, -1];
  let i = 1;
  let v = 0;
  while (i < s.length && i <= 4 && isDigit(s[i])) v = v * 10 + (s.charCodeAt(i++) - 48);
  const digits = i - 1;
  if (digits === 0 || digits > 3 || s[i] !== '>' || (digits > 1 && s[1] === '0') || v > 191) return [0, -1];
  return [i + 1, v];
}

/** Parses one received message (framing already removed). Never throws. */
export function parseSyslog(s: string): SyslogMessage {
  const [at, pri] = readPri(s);
  if (at > 0 && s.length >= at + 2 && s[at] === '1' && s[at + 1] === ' ') {
    const m = parse5424(s, at + 2, pri);
    if (m) return m;
  }
  return parse3164(s, at, pri);
}

function parse5424(s: string, start: number, pri: number): SyslogMessage | null {
  let i = start;
  const field = () => {
    let j = s.indexOf(' ', i);
    if (j < 0) j = s.length;
    const v = s.slice(i, j);
    i = Math.min(s.length, j + 1);
    return v === '-' ? '' : v;
  };
  const time = field();
  const host = field();
  const app = field();
  const pid = field();
  const msgid = field();
  if (i >= s.length && !msgid && !app) return null; // too short to be RFC 5424
  const params: [string, string][] = [];
  const sdStart = i;
  const sdEnd = readStructuredData(s, i, params);
  let sd = '';
  if (sdEnd < 0) params.length = 0; // malformed structured data: keep the text as the message
  else {
    sd = s.slice(sdStart, sdEnd) === '-' ? '' : s.slice(sdStart, sdEnd);
    i = s[sdEnd] === ' ' ? sdEnd + 1 : sdEnd;
  }
  let message = i < s.length ? s.slice(i) : '';
  if (message.charCodeAt(0) === 0xfeff) message = message.slice(1); // the BOM RFC 5424 puts before UTF-8 text
  return { pri, rfc: 5424, time, host, app, pid, msgid, structuredData: sd, params, message };
}

const SD_STOP = new Set([' ', ']', '"', '=']);

/** Reads "-" or one or more [SD-ID name="value" …] elements. Returns the end index, or -1 when malformed. */
export function readStructuredData(s: string, i: number, params: [string, string][]): number {
  if (s[i] === '-' && (i + 1 === s.length || s[i + 1] === ' ')) return i + 1;
  if (s[i] !== '[') return -1;
  while (s[i] === '[') {
    let j = i + 1;
    while (j < s.length && !SD_STOP.has(s[j])) j++;
    const id = s.slice(i + 1, j);
    if (!id || j >= s.length) return -1;
    i = j;
    while (s[i] === ' ') {
      j = ++i;
      while (j < s.length && !SD_STOP.has(s[j])) j++;
      if (j + 1 >= s.length || s[j] !== '=' || s[j + 1] !== '"' || j === i) return -1;
      const name = s.slice(i, j);
      i = j + 2;
      let v = '';
      while (i < s.length && s[i] !== '"') {
        if (s[i] === '\\' && (s[i + 1] === '"' || s[i + 1] === '\\' || s[i + 1] === ']')) i++;
        v += s[i++];
      }
      if (i >= s.length) return -1;
      i++; // closing quote
      if (params.length < 50) params.push([`${id}.${name}`, v]);
    }
    if (s[i] !== ']') return -1;
    i++;
  }
  return i;
}

function parse3164(s: string, at: number, pri: number): SyslogMessage {
  const base = { pri, time: '', host: '', app: '', pid: '', msgid: '', structuredData: '', params: [] as [string, string][] };
  let rest = s.slice(at);
  const tm = BSD_TIME.exec(rest) ?? ISO_TIME.exec(rest);
  if (!tm) return { ...base, rfc: 0, message: rest.trimStart() };
  const time = tm[0].slice(0, -1);
  rest = rest.slice(tm[0].length);
  // HOSTNAME is optional in practice: "Oct 11 22:14:15 su: …" has the tag right after the time.
  let host = '';
  if (!TAG.test(rest)) {
    const sp = rest.indexOf(' ');
    host = sp < 0 ? rest : rest.slice(0, sp);
    rest = sp < 0 ? '' : rest.slice(sp + 1);
  }
  let app = '';
  let pid = '';
  const t = TAG.exec(rest);
  if (t) {
    app = t[1];
    pid = t[2] ?? '';
    rest = rest.slice(t[0].length);
  }
  return { ...base, rfc: 3164, time, host, app, pid, message: rest };
}

const p2 = (n: number) => String(n).padStart(2, '0');

/** RFC 3339 with milliseconds and the local offset ("2024-05-01T10:00:00.000+02:00"). */
export function isoLocal(d: Date): string {
  const off = -d.getTimezoneOffset();
  const sign = off >= 0 ? '+' : '-';
  const a = Math.abs(off);
  return `${d.getFullYear()}-${p2(d.getMonth() + 1)}-${p2(d.getDate())}T${p2(d.getHours())}:${p2(d.getMinutes())}:${p2(d.getSeconds())}.${String(d.getMilliseconds()).padStart(3, '0')}${sign}${p2(Math.floor(a / 60))}:${p2(a % 60)}`;
}

/**
 * RFC 3164 "Mmm dd hh:mm:ss" as an RFC 3339 time: the year is this year, or last year when that would put the time more
 * than two days in the future (December messages read in January); the zone is the local one.
 */
export function bsdToIso(bsd: string, now: Date): string {
  const m = BSD_TIME.exec(bsd + ' ');
  if (!m) return bsd;
  const mon = MONTHS.indexOf(m[1]);
  const day = Number(m[2]);
  const h = Number(m[3]);
  if (mon < 0 || day < 1 || h > 23) return bsd;
  const frac = m[6] ? Number((m[6] + '00').slice(0, 3)) : 0;
  const make = (y: number) => {
    if (day > new Date(y, mon + 1, 0).getDate()) return null;
    return new Date(y, mon, day, h, Math.min(59, Number(m[4])), Math.min(59, Number(m[5])), frac);
  };
  let d = make(now.getFullYear());
  if (d && d.getTime() > now.getTime() + 2 * 86400_000) d = make(now.getFullYear() - 1);
  return d ? isoLocal(d) : bsd;
}

/** A header token (no spaces, at most `max` characters), or "-" when empty. */
function token(v: string, max: number): string {
  if (!v) return '-';
  return v.slice(0, max).replace(/[ \t]/g, '_');
}

/**
 * The line a syslog receiver writes for one message: always RFC 5424, so a capture holds one format whatever the senders
 * use. RFC 3164 headers are converted; a missing time or host is filled with the receive time and the sender's address,
 * and a missing PRI with 13 (user.notice), as RFC 3164 asks of relays.
 */
export function syslogToLine(raw: string, received: Date, sender: string): string {
  const m = parseSyslog(cleanLine(raw));
  const pri = m.pri < 0 ? 13 : m.pri;
  let time = m.rfc === 3164 && BSD_TIME.test(m.time + ' ') ? bsdToIso(m.time, received) : m.time;
  if (!time) time = isoLocal(received);
  const host = m.host || sender;
  const head = `<${pri}>1 ${token(time, 64)} ${token(host, 255)} ${token(m.app, 48)} ${token(m.pid, 128)} ${token(m.msgid, 32)} ${m.structuredData || '-'}`;
  return m.message ? `${head} ${m.message}` : head;
}

/**
 * Removes NULs and other control characters except tab, so one message stays one line and nothing can hide in the
 * view: line breaks become a space, the rest (C0, DEL, C1) are dropped.
 */
// eslint-disable-next-line no-control-regex
const CONTROL = /[\u0000-\u0008\u000a-\u001f\u007f-\u009f]/;
// eslint-disable-next-line no-control-regex
const CONTROL_ALL = /\r\n|[\r\n]|[\u0000-\u0008\u000b-\u001f\u007f-\u009f]/g;
export function cleanLine(s: string): string {
  if (!CONTROL.test(s)) return s;
  return s.replace(CONTROL_ALL, (c) => (c === '\n' || c === '\r' || c === '\r\n' ? ' ' : ''));
}

enum St {
  Start,
  Count,
  Octets,
  Line,
  SkipLine,
}

/**
 * Splits a TCP byte stream into messages. Syslog mode follows RFC 6587 frame by frame: a frame that starts with a
 * digit is octet-counted ("LEN SP MSG"), anything else ends at a line feed. Lines mode splits at line feeds only.
 * Messages longer than `max` bytes are cut off (and counted); malformed counts fall back to a line.
 */
export class StreamFramer {
  private buf: Buffer;
  private len = 0;
  private state = St.Start;
  private count = 0;
  private digits = 0;
  private remaining = 0;
  truncated = 0;

  private max: number;

  constructor(private syslog: boolean, max: number) {
    // grows up to `max` as messages need it: every connection has one, and senders are not trusted
    this.max = Math.max(16, max);
    this.buf = Buffer.alloc(Math.min(this.max, 4096));
  }

  feed(data: Uint8Array, emit: (m: string) => void) {
    for (let k = 0; k < data.length; k++) {
      const b = data[k];
      switch (this.state) {
        case St.Start:
          if (this.syslog && (b === 10 || b === 13 || b === 32 || b === 0)) continue; // padding between frames
          if (this.syslog && b >= 48 && b <= 57) {
            this.state = St.Count;
            this.count = b - 48;
            this.digits = 1;
            this.add(b);
            continue;
          }
          this.state = St.Line;
          this.line(b, emit);
          continue;
        case St.Count:
          if (b >= 48 && b <= 57 && this.digits < 9) {
            this.count = this.count * 10 + (b - 48);
            this.digits++;
            this.add(b);
          } else if (b === 32 && this.count > 0) {
            this.len = 0;
            this.remaining = this.count;
            this.state = St.Octets;
          } else {
            this.state = St.Line; // not a count after all: the digits start a line
            this.line(b, emit);
          }
          continue;
        case St.Octets:
          this.add(b);
          if (--this.remaining === 0) {
            if (this.count > this.max) this.truncated++;
            this.emit(emit);
          }
          continue;
        case St.Line:
          this.line(b, emit);
          continue;
        case St.SkipLine:
          if (b === 10) this.state = St.Start;
          continue;
      }
    }
  }

  private line(b: number, emit: (m: string) => void) {
    if (b === 10) {
      if (this.len > 0 && this.buf[this.len - 1] === 13) this.len--;
      this.emit(emit);
    } else if (!this.add(b)) {
      this.truncated++;
      this.emit(emit);
      this.state = St.SkipLine;
    }
  }

  /** The connection ended: a partial line still counts as a message. */
  end(emit: (m: string) => void) {
    if ((this.state === St.Line || this.state === St.Count) && this.len > 0) this.emit(emit);
    this.state = St.Start;
  }

  /** Appends a byte; false when the message is already `max` bytes long. */
  private add(b: number): boolean {
    if (this.len >= this.max) return false;
    if (this.len === this.buf.length) {
      const bigger = Buffer.alloc(Math.min(this.max, this.buf.length * 2));
      this.buf.copy(bigger, 0, 0, this.len);
      this.buf = bigger;
    }
    this.buf[this.len++] = b;
    return true;
  }

  private emit(emit: (m: string) => void) {
    const s = this.buf.toString('utf-8', 0, this.len);
    this.len = 0;
    this.state = St.Start;
    emit(s);
  }
}
