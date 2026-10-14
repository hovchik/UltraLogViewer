// Automatic token colouring: finds dates, numbers, GUIDs, IPs, URLs, paths, quoted strings,
// key=value keys, exception names, HTTP methods and keywords in a line of log text.

export type TokenKind = 'url' | 'guid' | 'date' | 'ip' | 'path' | 'exception' | 'key' | 'string' | 'duration' | 'number' | 'method' | 'keyword';

export interface Token {
  start: number;
  length: number;
  kind: TokenKind;
}

/** Lines longer than this are only tokenized in their first MAX_TOKENIZE characters. */
export const MAX_TOKENIZE = 2000;

// Alternatives in priority order: at a given position the first one that matches wins.
const ALTS: [TokenKind, string][] = [
  ['url', String.raw`\b(?:https?|ftp|wss?):\/\/[^\s"'<>]+`],
  ['guid', String.raw`\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b`],
  ['date', String.raw`\b\d{4}-\d{2}-\d{2}(?:[T ]\d{2}:\d{2}(?::\d{2}(?:[.,]\d+)?)?(?:Z|[+-]\d{2}:?\d{2})?)?\b`],
  ['date', String.raw`\b\d{2}:\d{2}:\d{2}(?:[.,]\d+)?\b`],
  ['ip', String.raw`\b(?:25[0-5]|2[0-4]\d|1?\d?\d)(?:\.(?:25[0-5]|2[0-4]\d|1?\d?\d)){3}(?::\d{1,5})?\b`],
  ['path', String.raw`(?<![\w/.])(?:\/[\w.\-@~]+){2,}\/?`],
  ['path', String.raw`\b[A-Za-z]:\\[^\s"'<>:*?|]+`],
  ['exception', String.raw`\b[A-Za-z_][\w.$]*(?:Exception|Error)\b`],
  ['key', String.raw`\b[A-Za-z_][\w.\-]*(?==)`],
  ['string', String.raw`"[^"\n]{0,200}"`],
  ['string', String.raw`'[^'\n]{0,200}'`],
  ['duration', String.raw`\b\d+(?:\.\d+)?(?:ms|µs|us|ns|s|sec|m|min|h)\b`],
  ['number', String.raw`(?<![\w.])-?\d+(?:\.\d+)?\b`],
  ['method', String.raw`\b(?:GET|POST|PUT|DELETE|PATCH|HEAD|OPTIONS)\b`],
  ['keyword', String.raw`\b(?:true|false|null|None|nil|undefined)\b`],
];

const RE = new RegExp(ALTS.map(([, p]) => `(${p})`).join('|'), 'g');
const URL_TRAIL = /[.,;:)\]}]+$/;

/** Non-overlapping tokens in ascending order, at most `maxTokens` of them. */
export function tokenize(text: string, maxTokens = 300): Token[] {
  const out: Token[] = [];
  if (!text || maxTokens <= 0) return out;
  const s = text.length > MAX_TOKENIZE ? text.slice(0, MAX_TOKENIZE) : text;
  RE.lastIndex = 0;
  let m: RegExpExecArray | null;
  while ((m = RE.exec(s))) {
    let g = 1;
    while (g <= ALTS.length && m[g] === undefined) g++;
    const kind = ALTS[g - 1][0];
    let length = m[0].length;
    if (kind === 'url') length -= URL_TRAIL.exec(m[0])?.[0].length ?? 0;
    if (length <= 0) {
      RE.lastIndex = m.index + 1;
      continue;
    }
    out.push({ start: m.index, length, kind });
    RE.lastIndex = m.index + length;
    if (out.length >= maxTokens) break;
  }
  return out;
}
