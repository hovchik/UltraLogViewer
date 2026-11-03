// The JSON lines the Windows event log export writes (see winEvent.ts): Windows event columns instead of the generic
// JSON ones. Detected by the fixed order the export writes its first keys in.

import { addExtras, levelText, LogFormat, str, tryJson } from './formats';
import { detectLevel, parseLevel } from './levels';
import { parseTimestamp } from './timestamp';

export const WINEVENT_HEAD = /^\{"time":"([^"]*)","level":"([^"]*)","provider":/;

const KEYS = ['time', 'level', 'provider', 'eventId', 'computer', 'message'];

export function winEventFormat(): LogFormat {
  return {
    id: 'winevent',
    name: 'Windows event log (exported)',
    columns: [
      { key: 'time', title: 'Time', width: 190 },
      { key: 'level', title: 'Level', width: 64 },
      { key: 'provider', title: 'Provider', width: 170 },
      { key: 'eventId', title: 'Event ID', width: 70 },
      { key: 'computer', title: 'Computer', width: 120 },
      { key: 'message', title: 'Message', width: 0 },
    ],
    multiline: false,
    isEntryStart: () => true,
    quick(line) {
      const m = WINEVENT_HEAD.exec(line);
      return m ? { level: parseLevel(m[2]), time: parseTimestamp(m[1]) } : { level: detectLevel(line), time: null };
    },
    parse(entry) {
      const o = tryJson(entry);
      if (!o) return { message: entry };
      const d: Record<string, string> = {};
      for (const k of KEYS) d[k] = str(o[k]);
      if (d.level) d.level = levelText(parseLevel(d.level)) || d.level;
      // No rendered message (plain XML export or a provider without message files): show the data instead.
      if (!d.message && o.data && typeof o.data === 'object' && !Array.isArray(o.data))
        d.message = Object.entries(o.data as Record<string, unknown>).map(([k, v]) => `${k}=${str(v)}`).join(' ');
      addExtras(d, o, '', new Set(KEYS), 0, { n: Object.keys(d).length });
      return d;
    },
  };
}
