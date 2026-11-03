// The offline "Extract fields…": guesses a custom-format regex with named groups from one sample line, by the same
// token classes the message colouring uses (timestamp, level, IP, number, key=value, quoted string, logger). It reads
// the line's header left to right and stops at the first word of free text, which becomes the message.
// Keep in sync with dotnet/src/UltraLogViewer.Core/AiRegexGuess.cs: both apps share test/fixtures/ai-vectors.json.

// ISO-like dates with an optional time, a bare time of day, or a syslog "May  1 10:00:00".
const TIME_RE =
  /^(?:\d{4}[-/]\d{2}[-/]\d{2}(?:[T ]\d{2}:\d{2}(?::\d{2}(?:[.,]\d+)?)?(?:Z|[+-]\d{2}:?\d{2})?)?|\d{2}:\d{2}:\d{2}(?:[.,]\d+)?|[A-Z][a-z]{2} +\d{1,2} \d{2}:\d{2}:\d{2}(?:[.,]\d+)?)/;
const LEVEL_RE = /^(?:TRACE|DEBUG|DBG|INFO|INFORMATION|NOTICE|WARN|WARNING|ERROR|ERR|FATAL|CRITICAL|CRIT|SEVERE|VERBOSE)$/i;
const IP_RE = /^\d{1,3}(?:\.\d{1,3}){3}(?::\d{1,5})?$/;
const NUMBER_RE = /^-?\d+(?:\.\d+)?$/;
const KEY_VALUE_RE = /^([A-Za-z_][\w.-]*)=/;
// Logger names: com.acme.Service, Foo.Bar (identifiers with dots), or a capitalised "MyService:".
const DOTTED_RE = /^[A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*)+$/;
const NAME_RE = /^[A-Z][\w$]*$/;
// Unicode-aware like .NET's \w, so both apps treat non-ASCII words alike.
const PUNCT_RE = /^[^\p{L}\p{N}_\s]+$/u;
const isSpace = (c: string) => /\s/.test(c);
const isLetterOrDigit = (c: string) => /[\p{L}\p{N}]/u.test(c);

/** A regex anchored at ^, ending in a message group, that matches `line`. */
export function guessRegex(line: string): string {
  line = line.replace(/[\r\n]+$/, '');
  let out = '^';
  const names = new Set<string>();
  const group = (name: string, body: string) => {
    let n = name;
    for (let i = 2; names.has(n); i++) n = name + i;
    names.add(n);
    return `(?<${n}>${body})`;
  };
  let pos = 0;
  let time = false, level = false, logger = false, thread = false, message = false;
  // Leading "[2024-05-01 10:00:00]" or a bare timestamp.
  const open = line.startsWith('[') ? 1 : 0;
  const tm = TIME_RE.exec(line.slice(open));
  if (tm && (open === 0 || (line.length > open + tm[0].length && line[open + tm[0].length] === ']'))) {
    out += (open ? '\\[' : '') + group('time', generalize(tm[0])) + (open ? '\\]' : '');
    pos = open * 2 + tm[0].length;
    time = true;
  }
  while (pos < line.length) {
    const ws = pos;
    while (pos < line.length && isSpace(line[pos])) pos++;
    if (pos >= line.length) break;
    const sep = pos > ws ? '\\s+' : '';
    const c = line[pos];
    const tm2 = time ? null : TIME_RE.exec(line.slice(pos));
    if (tm2 && (pos + tm2[0].length === line.length || !isLetterOrDigit(line[pos + tm2[0].length]))) {
      time = true;
      out += sep + group('time', generalize(tm2[0]));
      pos += tm2[0].length;
      continue;
    }
    const close = c === '[' ? line.indexOf(']', pos + 1) : -1;
    if (close > 0) {
      const inner = line.slice(pos + 1, close);
      let body: string;
      if (!level && LEVEL_RE.test(inner.trim())) {
        level = true;
        body = '\\[\\s*' + group('level', '[A-Za-z]+') + '\\s*\\]';
      } else if (!thread) {
        thread = true;
        body = '\\[' + group('thread', '[^\\]]*') + '\\]';
      } else body = '\\[' + group('field', '[^\\]]*') + '\\]';
      out += sep + body;
      pos = close + 1;
      continue;
    }
    const q = c === '"' ? line.indexOf('"', pos + 1) : -1;
    if (q > 0) {
      out += sep + '"' + group('text', '[^"]*') + '"';
      pos = q + 1;
      continue;
    }
    let end = pos;
    while (end < line.length && !isSpace(line[end])) end++;
    const word = line.slice(pos, end);
    // A trailing ":" or "," is kept as a literal, so "Db:" can be a logger and "3," a number.
    const tail = word.length > 1 && (word.endsWith(':') || word.endsWith(',')) ? word.slice(-1) : '';
    const core = word.slice(0, word.length - tail.length);
    let piece: string | null = null;
    const kv = KEY_VALUE_RE.exec(word);
    if (PUNCT_RE.test(word)) piece = escapeRegex(word);
    else if (!level && LEVEL_RE.test(core)) {
      level = true;
      piece = group('level', '[A-Za-z]+') + escapeRegex(tail);
    } else if (IP_RE.test(core))
      piece = core.includes(':')
        ? group('ip', '\\d{1,3}(?:\\.\\d{1,3}){3}') + ':' + group('port', '\\d+') + escapeRegex(tail)
        : group('ip', '\\d{1,3}(?:\\.\\d{1,3}){3}') + escapeRegex(tail);
    else if (NUMBER_RE.test(core)) piece = group('number', '-?\\d+(?:\\.\\d+)?') + escapeRegex(tail);
    else if (kv) {
      const key = kv[1];
      const rest = line.slice(pos + kv[0].length);
      let name = identifier(key);
      if (name === 'message') name = 'msg'; // the message group is the rest of the line
      const kq = rest.startsWith('"') ? rest.indexOf('"', 1) : -1;
      if (kq > 0) {
        piece = escapeRegex(key) + '="' + group(name, '[^"]*') + '"';
        end = pos + kv[0].length + kq + 1;
      } else piece = escapeRegex(key) + '=' + group(name, '\\S*');
    } else if (!logger && (DOTTED_RE.test(core) || (tail === ':' && NAME_RE.test(core)))) {
      logger = true;
      piece = group('logger', '[\\w.$]+') + escapeRegex(tail);
    }
    if (piece === null) {
      out += sep + group('message', '.*');
      message = true;
      break;
    }
    out += sep + piece;
    pos = end;
  }
  if (!message) out += '\\s*' + group('message', '.*');
  return out + '$';
}

/** Digit runs become \d{n} (fractions \d+, single digits \d{1,2}), month names [A-Z][a-z]{2}, spaces \s+; the rest is literal. */
export function generalize(s: string): string {
  let out = '';
  const digit = (c: string | undefined) => c !== undefined && c >= '0' && c <= '9';
  for (let i = 0; i < s.length; ) {
    const c = s[i];
    let j = i;
    if (digit(c)) {
      while (j < s.length && digit(s[j])) j++;
      const frac = i > 1 && (s[i - 1] === '.' || s[i - 1] === ',') && digit(s[i - 2]);
      out += frac ? '\\d+' : j - i === 1 ? '\\d{1,2}' : `\\d{${j - i}}`;
    } else if (/[A-Z]/.test(c) && i + 2 < s.length && /[a-z]/.test(s[i + 1]) && /[a-z]/.test(s[i + 2])) {
      j = i + 3;
      out += '[A-Z][a-z]{2}';
    } else if (c === ' ') {
      while (j < s.length && s[j] === ' ') j++;
      out += '\\s+';
    } else {
      j = i + 1;
      out += escapeRegex(c);
    }
    i = j;
  }
  return out;
}

/** Escapes regex metacharacters the same way in .NET and JavaScript (no "\ " or "\#"). */
export function escapeRegex(s: string): string {
  return s.replace(/[\\^$.|?*+()[\]{}/]/g, '\\$&');
}

/** A group name valid in both engines: word characters, starting with a letter. */
export function identifier(key: string): string {
  let s = key.replace(/[^A-Za-z0-9_]/g, '_');
  if (!s || !/[A-Za-z]/.test(s[0])) s = 'f_' + s;
  return s;
}
