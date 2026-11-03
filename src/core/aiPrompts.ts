// Builds the prompts for "Extract fields…", "Explain this entry / pattern" and "Ask AI for a filter", and checks the
// answers. Pure functions; keep in sync with dotnet/src/UltraLogViewer.Core/AiPrompts.cs.

import { formatTime } from './timestamp';

/** One request to the model: fixed instructions plus the user part, which the consent dialog shows redacted and editable. */
export interface AiPrompt {
  system: string;
  user: string;
}

/** Per-entry cap, so a giant stack trace or JSON blob cannot blow up the request. */
export const MAX_ENTRY_CHARS = 4000;
export const MAX_CONTEXT_CHARS = 600;

export const cap = (s: string, max: number) => (s.length <= max ? s : s.slice(0, max) + '…');

/** The quick-filter grammar, as the model needs it (the Help dialog has the long version). */
export const GRAMMAR = `Quick-filter query language:
- words: entries containing all of them (case-insensitive); "exact phrase"; /regex/
- a OR b, a | b: either (OR binds tighter than AND); -word or NOT word: without; and/or/not with parentheses
- field:value: a parsed field contains the value (field:"two words", field:/re/)
- field>=n, >, <, <=, ==, !=: compare numerically (durations like 2s, 150ms work), level>=warn compares by severity
- field in (a,b): equals one of the values; logger:com.acme.* wildcard
- exists(field); field?=x (equals x or is missing)
- since:-3h, until:-10m (relative to now), since:2024-05-01T10:00, since:today
- level:error matches the level; levels are trace, debug, info, warn, error, fatal`;

export function extractPrompt(lines: string[]): AiPrompt {
  return {
    system: `You write regular expressions that parse log lines into fields for a log viewer.
Answer with JSON only, no prose and no code fence: {"name": "<short format name>", "pattern": "<regex>"}
Rules for the pattern:
- It must work in both .NET and JavaScript: named groups as (?<name>...), no lookbehind, no inline flags, no possessive quantifiers.
- Anchor it with ^ and make it match the whole first sample line.
- Use the group names time, level, thread, logger and message for those parts; give other fields short lower-case names.
- End with a message group that captures the rest of the line.
- Prefer general classes (\\d, \\S+, [^\\]]*) over literal values from the samples.`,
    user: 'Sample lines (the first one must match):\n' + lines.map((l) => cap(l, MAX_CONTEXT_CHARS)).join('\n'),
  };
}

const explainSystem = (what: string) => `You help a developer read application logs. Explain ${what}: what it means, the likely cause,
and what to check next. Be concise (at most about 200 words). Use plain text or simple Markdown
(short headings, bullet lists, \`code\`). Some values were replaced by placeholders such as <ip> or <email>
for privacy; do not guess them.`;

export function explainEntryPrompt(format: string, before: string[], entry: string, after: string[], filtered: boolean): AiPrompt {
  let s = `Log format: ${format}\n`;
  if (filtered) s += '(The surrounding lines come from a filtered view.)\n';
  s += '\nContext before:\n';
  for (const l of before) s += cap(l, MAX_CONTEXT_CHARS) + '\n';
  s += '\n>>> Entry to explain:\n' + cap(entry, MAX_ENTRY_CHARS) + '\n<<<\n\nContext after:\n';
  for (const l of after) s += cap(l, MAX_CONTEXT_CHARS) + '\n';
  return { system: explainSystem('the entry between >>> and <<<'), user: s.trimEnd() };
}

export function explainPatternPrompt(format: string, template: string, count: number, samples: string[]): AiPrompt {
  let s = `Log format: ${format}\n`;
  s += `Message pattern (<*> marks values that vary), seen ${count.toLocaleString('en-US')} times:\n`;
  s += cap(template, MAX_ENTRY_CHARS) + '\n\nExamples:\n';
  for (const l of samples) s += cap(l, MAX_CONTEXT_CHARS) + '\n';
  return { system: explainSystem('this log message pattern'), user: s.trimEnd() };
}

const isoLocal = (d: Date) => {
  const p = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}T${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
};

/**
 * @param fields parsed fields with their most common values (the sidebar's field stats)
 * @param levels level names present in the view, with counts
 */
export function filterPrompt(
  question: string,
  fields: { key: string; top: string[] }[],
  levels: { level: string; count: number }[],
  timeMin: number | null,
  timeMax: number | null,
  now: Date,
): AiPrompt {
  let s = `Request: ${question.trim()}\n\n`;
  s += `Now: ${isoLocal(now)}\n`;
  if (timeMin !== null && timeMax !== null) s += `The file covers ${formatTime(timeMin)} to ${formatTime(timeMax)}\n`;
  if (levels.length) s += `Levels present: ${levels.map((l) => `${l.level.toLowerCase()} (${l.count})`).join(', ')}\n`;
  if (fields.length) {
    s += 'Fields and their most common values:\n';
    for (const f of fields.slice(0, 20))
      s += `- ${f.key}${f.top.length ? ': ' + f.top.slice(0, 8).map((v) => cap(v.replace(/\n/g, ' '), 60)).join(', ') : ''}\n`;
  }
  return {
    system: `You turn a request into a query for a log viewer's quick filter.
${GRAMMAR}
Use only fields from the list (or plain words). For times relative to now use since:/until:.
Answer with JSON only, no prose and no code fence: {"query": "<the query>", "explanation": "<one short sentence>"}`,
    user: s.trimEnd(),
  };
}

// ------------------------------------------------------------------ answers

/** The JSON object in an answer: tolerates a code fence or a sentence around it. */
export function jsonObject(answer: string): Record<string, unknown> | null {
  const a = answer.indexOf('{');
  const b = answer.lastIndexOf('}');
  if (a < 0 || b <= a) return null;
  try {
    const v = JSON.parse(answer.slice(a, b + 1));
    return v && typeof v === 'object' && !Array.isArray(v) ? (v as Record<string, unknown>) : null;
  } catch {
    return null;
  }
}

const str = (o: Record<string, unknown>, k: string) => (typeof o[k] === 'string' ? (o[k] as string) : null);

export type Checked<T> = { ok: true; value: T } | { ok: false; error: string };

/** Checks an "Extract fields" answer: valid JSON, a regex that compiles, has named groups and matches the sample. */
export function parseExtract(answer: string, sample: string): Checked<{ name: string; pattern: string }> {
  const o = jsonObject(answer);
  const pattern = o && str(o, 'pattern');
  if (!o || !pattern) return { ok: false, error: 'the answer was not the JSON that was asked for' };
  let re: RegExp;
  try {
    re = new RegExp(pattern);
  } catch (e) {
    return { ok: false, error: `the regex does not compile (${(e as Error).message})` };
  }
  if (!/\(\?<[A-Za-z_]\w*>/.test(pattern)) return { ok: false, error: 'the regex has no named groups' };
  if (!re.test(sample)) return { ok: false, error: 'the regex does not match the line' };
  const name = str(o, 'name')?.trim();
  return { ok: true, value: { name: name ? cap(name, 40) : 'Extracted', pattern } };
}

/** Reads a "filter" answer. The caller checks the query with validateQuery. */
export function parseFilter(answer: string): Checked<{ query: string; explanation: string }> {
  const o = jsonObject(answer);
  const q = o && str(o, 'query');
  if (!o || !q || !q.trim()) return { ok: false, error: 'the answer was not the JSON that was asked for' };
  return { ok: true, value: { query: q.trim(), explanation: str(o, 'explanation')?.trim() ?? '' } };
}

/**
 * Light Markdown for the answer pane: heading marks and emphasis dropped, bullets as •, code fences removed.
 * The answer is shown as plain text (textContent), so nothing in it is ever interpreted as markup or a link.
 */
export function plainAnswer(md: string): string {
  return md
    .replace(/\r\n/g, '\n')
    .split('\n')
    .filter((l) => !l.trimStart().startsWith('```'))
    .map((l) => {
      let t = l.trimStart();
      const indent = l.slice(0, l.length - t.length);
      if (t.startsWith('#')) t = t.replace(/^#+/, '').trim();
      else if (t.startsWith('- ') || t.startsWith('* ')) t = '• ' + t.slice(2);
      return indent + t.replaceAll('**', '').replaceAll('__', '');
    })
    .join('\n')
    .trim();
}
