// Windows event log: what `wevtutil qe /f:RenderedXml` prints, turned into one JSON line per event (time, level,
// provider, eventId, channel, computer, recordId, task, opcode, keywords, user, message from RenderingInfo when present,
// and data from EventData's named values or UserData's elements), the export/follow logic behind an interface so it can
// be tested anywhere with a fake, and the "Windows event" format for those lines.

import { CaptureFile } from './captureFile';
import { child, attr, parseXml, XmlElement } from './miniXml';
import { validChannel } from './receiverOptions';
import { cleanLine } from './syslog';

export { COMMON_CHANNELS, validChannel } from './receiverOptions';

/** Picks whole <Event> elements out of wevtutil's output, which has no root element. */
export class WinEventSplitter {
  private buf = '';

  constructor(private maxEvent = 4 * 1024 * 1024) {}

  feed(text: string): string[] {
    const list: string[] = [];
    const s = this.buf + text;
    let pos = 0;
    for (;;) {
      const st = findStart(s, pos);
      if (st < 0) {
        pos = Math.max(pos, s.length - 8);
        break;
      }
      let end = s.indexOf('</Event>', st);
      if (end < 0) {
        pos = s.length - st > this.maxEvent ? s.length : st; // a runaway event is dropped
        break;
      }
      end += 8;
      list.push(s.slice(st, end));
      pos = end;
    }
    this.buf = s.slice(pos);
    return list;
  }
}

function findStart(s: string, from: number): number {
  for (;;) {
    const i = s.indexOf('<Event', from);
    if (i < 0 || i + 6 >= s.length) return -1;
    if (/[\s>]/.test(s[i + 6])) return i;
    from = i + 6; // <EventData>, <EventID>…
  }
}

/** Level number to the name Event Viewer shows (0, LogAlways, shows as Information). */
export function levelName(n: string): string {
  switch (n) {
    case '1':
      return 'Critical';
    case '2':
      return 'Error';
    case '3':
      return 'Warning';
    case '4':
    case '0':
    case '':
      return 'Information';
    case '5':
      return 'Verbose';
    default:
      return n;
  }
}

const value = (e: XmlElement | undefined): string => (e ? (e.text + e.children.map(value).join('')).trim() : '');

/** An element's text; one holding elements gives their texts, space-separated. */
function texts(e: XmlElement): string {
  if (!e.children.length) return e.text.trim();
  const out: string[] = [];
  const walk = (x: XmlElement) => {
    if (x.text.trim()) out.push(x.text.trim());
    x.children.forEach(walk);
  };
  walk(e);
  return out.join(' ');
}

/** One event as a JSON line, or null when it is not well-formed. */
export function winEventToJson(eventXml: string): string | null {
  let ev: XmlElement;
  try {
    ev = parseXml(eventXml);
  } catch {
    return null;
  }
  if (ev.local !== 'Event') return null;
  const sys = child(ev, 'System');
  const S = (name: string) => value(child(sys, name));
  const render = child(ev, 'RenderingInfo');
  const keywords = (child(render, 'Keywords')?.children ?? []).map((k) => value(k)).filter(Boolean).join(', ');

  const data: [string, string][] = [];
  const ed = child(ev, 'EventData');
  if (ed) {
    let unnamed = 0;
    for (const d of ed.children) data.push([d.local === 'Data' ? attr(d, 'Name') || `Data${++unnamed}` : d.local, texts(d)]);
  } else {
    // UserData holds one provider-defined element; its leaf elements become fields, nested ones dotted.
    const ud = child(ev, 'UserData')?.children[0];
    const walk = (e: XmlElement, prefix: string) => {
      for (const c of e.children) {
        const k = prefix + c.local;
        if (c.children.length) walk(c, k + '.');
        else data.push([k, value(c)]);
      }
    };
    if (ud) walk(ud, '');
  }

  const levelNum = S('Level');
  let level = levelName(levelNum);
  // Security audits have level 0; Event Viewer marks failures only by keyword, which deserves a warning here.
  const kw = S('Keywords').replace(/^0x/i, '');
  if ((levelNum === '0' || levelNum === '') && /^[0-9a-f]{1,16}$/i.test(kw) && (BigInt('0x' + kw) & 0x10000000000000n) !== 0n) level = 'Warning';
  const named = (name: string) => {
    const r = value(child(render, name));
    if (r) return r;
    const v = S(name);
    return v === '0' ? '' : v;
  };
  const provider = child(sys, 'Provider');
  const o: Record<string, unknown> = {
    time: attr(child(sys, 'TimeCreated'), 'SystemTime'),
    level,
    provider: attr(provider, 'Name') || attr(provider, 'EventSourceName'),
    eventId: S('EventID'),
    channel: S('Channel'),
    computer: S('Computer'),
    recordId: S('EventRecordID'),
  };
  const opt = (k: string, v: string) => v && (o[k] = v);
  opt('task', named('Task'));
  opt('opcode', named('Opcode'));
  opt('keywords', keywords);
  opt('user', attr(child(sys, 'Security'), 'UserID'));
  opt('processId', attr(child(sys, 'Execution'), 'ProcessID'));
  opt('threadId', attr(child(sys, 'Execution'), 'ThreadID'));
  opt('activityId', attr(child(sys, 'Correlation'), 'ActivityID'));
  o.message = value(child(render, 'Message'));
  if (data.length) {
    const d: Record<string, string> = {};
    let n = 0;
    for (const [k, v] of data) if (!(k in d) && n++ < 100) d[k] = v;
    o.data = d;
  }
  return cleanLine(JSON.stringify(o));
}

// ----------------------------------------------------------------- export

/** Runs wevtutil.exe with an argument array (never through a shell), passing its output on in pieces. */
export interface Wevtutil {
  run(args: string[], output: (text: string) => void, signal: AbortSignal): Promise<{ code: number; error: string }>;
}

/** The XPath filter for a time range (null ends are open), or "*". */
export function timeQuery(fromMs: number | null, toMs: number | null): string {
  const t = (ms: number) => new Date(ms).toISOString();
  const parts: string[] = [];
  if (fromMs != null) parts.push(`@SystemTime>='${t(fromMs)}'`);
  if (toMs != null) parts.push(`@SystemTime<='${t(toMs)}'`);
  return parts.length ? `*[System[TimeCreated[${parts.join(' and ')}]]]` : '*';
}

/** wevtutil arguments for one query, oldest first. */
export const queryArgs = (channel: string, xpath: string) => ['qe', channel, '/f:RenderedXml', '/rd:false', `/q:${xpath}`];

/** "Open Windows event log…": exports a channel to a capture file, then optionally follows new events by record id. */
export class WinEventLog {
  readonly capture: CaptureFile;
  readonly following: boolean;
  /** Resolves when the first export is done, with its error or null. */
  readonly firstExport: Promise<string | null>;
  events = 0;
  lastRecordId = 0;
  private state = 'reading';
  private stopped = false;
  private abort = new AbortController();
  private splitter = new WinEventSplitter();

  constructor(
    private wevtutil: Wevtutil,
    readonly channel: string,
    from: number | null,
    until: number | null,
    follow: boolean,
    dir: string,
    capBytes = 1024 ** 3,
    private pollMs = 2000,
  ) {
    if (!validChannel(channel)) throw new Error(`Not a channel name: ${channel}`);
    this.capture = CaptureFile.create(dir, `eventlog-${channel.replace(/[^\w-]+/g, '_').slice(0, 40)}`, capBytes);
    this.following = follow && until == null;
    this.firstExport = this.runFirst(from, until);
  }

  get path() {
    return this.capture.path;
  }
  get running() {
    return !this.stopped;
  }
  get status(): string {
    return `Windows event log ${this.channel}\n${this.events.toLocaleString('en-US')} ${this.events === 1 ? 'event' : 'events'} · ${this.state}${this.capture.dropped ? ` · ${this.capture.dropped.toLocaleString('en-US')} dropped` : ''}`;
  }

  private onOutput = (text: string) => {
    for (const ev of this.splitter.feed(text)) {
      const json = winEventToJson(ev);
      if (!json) continue;
      const id = /"recordId":"(\d+)"/.exec(json);
      if (id && Number(id[1]) > this.lastRecordId) this.lastRecordId = Number(id[1]);
      if (this.capture.writeLine(json)) this.events++;
    }
  };

  private async runFirst(from: number | null, until: number | null): Promise<string | null> {
    let error: string | null = null;
    try {
      const r = await this.wevtutil.run(queryArgs(this.channel, timeQuery(from, until)), this.onOutput, this.abort.signal);
      if (r.code !== 0) error = r.error || `wevtutil exited with code ${r.code}`;
    } catch (e) {
      if (this.abort.signal.aborted) return null;
      error = (e as Error).message;
    }
    if (error !== null || !this.following) {
      this.state = error === null ? 'done' : `error: ${error}`;
      this.stopped = true;
      void this.capture.close();
      return error;
    }
    this.state = 'following new events';
    void this.follow();
    return null;
  }

  private async follow() {
    while (!this.abort.signal.aborted) {
      await new Promise((r) => setTimeout(r, this.pollMs));
      if (this.abort.signal.aborted) return;
      try {
        const r = await this.wevtutil.run(queryArgs(this.channel, `*[System[EventRecordID>${this.lastRecordId}]]`), this.onOutput, this.abort.signal);
        this.state = r.code === 0 ? 'following new events' : `error: ${r.error || `code ${r.code}`} (retrying)`;
      } catch (e) {
        if (this.abort.signal.aborted) return;
        this.state = `error: ${(e as Error).message} (retrying)`;
      }
    }
  }

  stop() {
    if (this.stopped) return;
    this.stopped = true;
    this.state = 'stopped';
    this.abort.abort();
    void this.capture.close();
  }
}
