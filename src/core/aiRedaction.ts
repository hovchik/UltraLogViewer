// Masks secrets and personal data in text before it is shown in the AI consent preview (and then sent).
// The rules run in order and each one leaves a placeholder that no later rule matches.
// Keep in sync with dotnet/src/UltraLogViewer.Core/AiRedaction.cs: both apps share test/fixtures/ai-vectors.json.

const HEX = '[0-9A-Fa-f]{1,4}';

const RULES: [string, RegExp, (m: string, ...g: string[]) => string | null][] = [
  // A PEM private key block, whole (its base64 lines are split by + and /, so the token rule would miss parts).
  ['pem', /-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----[\s\S]*?(?:-----END [A-Z0-9 ]*PRIVATE KEY-----|$)/g, () => '<private key>'],
  // Authorization: Bearer xyz (also "Basic"): the scheme stays, the credential (8+ chars) goes.
  ['bearer', /\b([Bb][Ee][Aa][Rr][Ee][Rr]|Basic)(\s+)[A-Za-z0-9\-._~+/]{8,}=*/g, (_m, scheme, ws) => `${scheme}${ws}<token>`],
  // Authorization: <any other scheme> credential, or a bare credential.
  ['authorization', /\b((?:Proxy-)?Authorization)(\s*[:=]\s*)((?:[A-Za-z][\w-]*\s+)?)[A-Za-z0-9\-._~+/]{8,}=*/gi, (_m, name, sep, scheme) => `${name}${sep}${scheme}<token>`],
  ['jwt', /\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+/g, () => '<jwt>'],
  // password=..., "secret": "...", api_key: ..., token=...: keep the key, mask the value.
  [
    'secret',
    // (not \b: DB_PASSWORD and db_password count too)
    /(?<![A-Za-z0-9])((?:pass(?:word|wd|phrase)?|pwd|secret|client[_-]?secret|token|access[_-]?token|refresh[_-]?token|api[_-]?key|access[_-]?key|private[_-]?key|credentials?|auth)"?)(\s*[:=]\s*)("[^"]*"|'[^']*'|[^\s,;&"']+)/gi,
    (_m, key, sep) => `${key}${sep}<redacted>`,
  ],
  // Well-known key formats: OpenAI/Anthropic sk-..., AWS AKIA..., GitHub ghp_..., Slack xox?-..., Google AIza...
  ['key', /\b(?:sk-[A-Za-z0-9_-]{16,}|AKIA[0-9A-Z]{16}|gh[pousr]_[A-Za-z0-9]{30,}|xox[abprs]-[A-Za-z0-9-]{10,}|AIza[0-9A-Za-z_-]{35})/g, () => '<key>'],
  ['email', /\b[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}\b/g, () => '<email>'],
  // 13-19 digits (optionally in groups split by spaces or dashes) that pass the Luhn check and start like a card.
  ['card', /(?<![\d.])[3-6]\d{3}(?:[ -]?\d){9,15}(?![\d.])/g, (m) => (luhn(m) ? '<card>' : null)],
  [
    'ipv6',
    new RegExp(`(?<![\\w:.])(?:(?:${HEX}:){7}${HEX}|(?:${HEX}:){1,6}(?::${HEX}){1,6}|(?:${HEX}:){1,7}:|::(?:${HEX}:){0,5}${HEX})(?![\\w:])`, 'g'),
    () => '<ipv6>',
  ],
  ['ipv4', /(?<![\w.])(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)(?![\w.]*\w)/g, () => '<ip>'],
  // Long random-looking tokens: 32+ chars mixing upper case, lower case and digits (GUIDs and hex hashes stay).
  ['token', /(?<![\w-])(?=[\w-]*[a-z])(?=[\w-]*[A-Z])(?=[\w-]*\d)[A-Za-z0-9_-]{32,}(?![\w-])/g, () => '<key>'],
];

/** The kinds of values that are masked, for the consent dialog. */
export const REDACTION_SUMMARY = 'emails, IP addresses, bearer tokens and JWTs, API keys, password=… values and card numbers';

export function redact(text: string): { text: string; count: number } {
  let count = 0;
  for (const [, re, replace] of RULES) {
    text = text.replace(re, (m: string, ...rest: unknown[]) => {
      const groups = rest.filter((x): x is string => typeof x === 'string' || x === undefined).map((x) => x ?? '');
      const r = replace(m, ...groups);
      if (r === null) return m;
      count++;
      return r;
    });
  }
  return { text, count };
}

export function luhn(s: string): boolean {
  const digits = [...s].filter((c) => c >= '0' && c <= '9').map(Number);
  if (digits.length < 13 || digits.length > 19) return false;
  let sum = 0;
  for (let i = 0; i < digits.length; i++) {
    let d = digits[digits.length - 1 - i];
    if (i % 2 === 1 && (d *= 2) > 9) d -= 9;
    sum += d;
  }
  return sum % 10 === 0;
}
