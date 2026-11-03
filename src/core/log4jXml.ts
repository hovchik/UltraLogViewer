// log4j 1 XMLLayout (log4j:event, also what Chainsaw-style senders use) and log4net XmlLayout (log4net:event) events,
// as they arrive over TCP: a stream of event elements with no root and undeclared prefixes. Each event becomes one JSON
// line (time, level, logger, thread, message, exception, …) that the JSON format shows with all its fields.

import { attr, parseXml, XmlElement } from './miniXml';
import { cleanLine, isoLocal } from './syslog';

function start(s: string, from: number): number {
  const a = s.indexOf('<log4j:event', from);
  const b = s.indexOf('<log4net:event', from);
  return a < 0 ? b : b < 0 ? a : Math.min(a, b);
}

/** Picks whole events out of the stream; an event longer than `maxEvent` characters is dropped and counted. */
export class Log4jXmlSplitter {
  private buf = '';
  truncated = 0;

  constructor(private maxEvent: number) {}

  feed(text: string): string[] {
    const events: string[] = [];
    this.buf += text;
    for (;;) {
      const s = this.buf;
      const st = start(s, 0);
      if (st < 0) {
        this.buf = s.slice(Math.max(0, s.length - 16)); // may be the beginning of a start tag
        return events;
      }
      const prefix = s.slice(st + 1, s.indexOf(':event', st));
      const close = `</${prefix}:event>`;
      let end = s.indexOf(close, st);
      if (end < 0) {
        this.buf = s.slice(st);
        if (this.buf.length > this.maxEvent) {
          // an event without an end: drop it and look for the next one
          this.truncated++;
          const next = start(this.buf, 1);
          this.buf = next < 0 ? '' : this.buf.slice(next);
          continue;
        }
        return events;
      }
      end += close.length;
      if (end - st <= this.maxEvent) events.push(s.slice(st, end));
      else this.truncated++;
      this.buf = s.slice(end);
    }
  }
}

/** log4j writes epoch milliseconds, log4net an ISO time; both become ISO with the local offset. */
function time(v: string): string {
  if (/^\d+$/.test(v)) {
    const ms = Number(v);
    if (ms > 0 && ms < 253402300800000) return isoLocal(new Date(ms));
  }
  return v;
}

/**
 * One event as a JSON line, or null when it is not well-formed XML. Attribute and element names are matched without
 * their prefix, so log4j and log4net events read the same.
 */
export function log4jToJson(fragment: string): string | null {
  let ev: XmlElement;
  try {
    ev = parseXml(fragment);
  } catch {
    return null;
  }
  if (ev.local !== 'event') return null;
  let message = '';
  let exception = '';
  let ndc = '';
  const location: [string, string][] = [];
  const props: [string, string][] = [];
  const walk = (e: XmlElement) => {
    for (const c of e.children) {
      switch (c.local) {
        case 'message':
          message = c.text;
          continue;
        case 'throwable':
        case 'exception':
          exception = c.text;
          continue;
        case 'NDC':
          ndc = c.text;
          continue;
        case 'data':
          if ('name' in c.attrs && props.length < 50) props.push([c.attrs.name, c.attrs.value ?? '']);
          break;
        case 'locationInfo':
          for (const k of ['class', 'method', 'file', 'line']) if (c.attrs[k]) location.push([k, c.attrs[k]]);
          break;
      }
      walk(c);
    }
  };
  walk(ev);
  const o: Record<string, unknown> = { time: time(attr(ev, 'timestamp')), level: attr(ev, 'level'), logger: attr(ev, 'logger'), thread: attr(ev, 'thread'), message };
  if (exception) o.exception = exception.trimEnd();
  if (ndc) o.ndc = ndc;
  for (const [k, v] of location) o[k] = v;
  for (const [k, v] of Object.entries(ev.attrs)) {
    const l = k.slice(k.indexOf(':') + 1);
    if (!['logger', 'level', 'thread', 'timestamp'].includes(l) && !k.startsWith('xmlns') && !(l in o)) o[l] = v;
  }
  if (props.length) {
    const p: Record<string, string> = {};
    for (const [k, v] of props) if (!(k in p)) p[k] = v;
    o.properties = p;
  }
  return cleanLine(JSON.stringify(o));
}
