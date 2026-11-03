import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import * as fs from 'node:fs';
import * as http from 'node:http';
import type { AddressInfo } from 'node:net';
import * as path from 'node:path';
import { aiConfig, AiConfig, AiError, aiHost, buildRequest, parseResponse, sendAi } from '../src/core/aiClient';
import { explainEntryPrompt, explainPatternPrompt, extractPrompt, filterPrompt, parseExtract, parseFilter, plainAnswer } from '../src/core/aiPrompts';
import { luhn, redact } from '../src/core/aiRedaction';
import { escapeRegex, guessRegex } from '../src/core/aiRegexGuess';
import { validateQuery } from '../src/core/query';
import { customFormat } from '../src/core/formats';

/**
 * Redaction, the offline regex guesser, prompt building, answer checking and both wire formats against a local test
 * server (no real network). Redaction and guesses use test/fixtures/ai-vectors.json, which the .NET tests share.
 */
const vectors = JSON.parse(fs.readFileSync(path.join(__dirname, 'fixtures', 'ai-vectors.json'), 'utf-8')) as {
  redact: { in: string; out: string; count: number }[];
  guess: { line: string; pattern: string; groups: Record<string, string> }[];
};

describe('redaction', () => {
  it('matches the shared vectors', () => {
    for (const v of vectors.redact) expect(redact(v.in), v.in).toEqual({ text: v.out, count: v.count });
  });

  it('masks each kind and keeps ordinary text', () => {
    expect(redact('mail a.b+c@mail.example.org').text).toBe('mail <email>');
    expect(redact('Authorization: Basic dXNlcjpwYXNz').text).toBe('Authorization: Basic <token>');
    expect(redact("pwd: abc; secret='x y'").text).toBe('pwd: <redacted>; secret=<redacted>');
    expect(redact('gh ghp_' + 'a'.repeat(36)).text).toBe('gh <key>');
    expect(redact('card 5555-5555-5555-4444').text).toBe('card <card>');
    const plain = 'ok 2024-05-01T10:00:00.123Z took 15ms status=500';
    expect(redact(plain).text).toBe(plain);
    expect(redact('Using basic auth, Bearer short').text).toBe('Using basic auth, Bearer short');
    expect(luhn('4111111111111111')).toBe(true);
    expect(luhn('4111111111111112')).toBe(false);
  });
});

describe('regex guess', () => {
  it('matches the shared vectors and parses the line', () => {
    for (const v of vectors.guess) {
      const p = guessRegex(v.line);
      expect(p, v.line).toBe(v.pattern);
      expect(new RegExp(p).exec(v.line)?.groups).toEqual(v.groups);
    }
  });

  it.each([
    ['2024-05-01 10:00:00 INFO x', 'level'],
    ['WARN: disk almost full', 'level'],
    ['10.1.2.3 GET /', 'ip'],
    ['a=1 b="two words" rest of it', 'b'],
    ['com.acme.Db query slow', 'logger'],
  ])('finds the token class in %s', (line, group) => {
    const p = guessRegex(line);
    expect(new RegExp(p).exec(line)?.groups?.[group]).toBeDefined();
    expect(p.endsWith('(?<message>.*)$')).toBe(true);
  });

  it('always produces a regex that matches, usable as a custom format', () => {
    for (const line of ['', '   ', '[unterminated', '"open quote', 'message=hi there', '### header ###', 'x=1 x=2 x=3', '€ ünïcödé 文字']) {
      expect(new RegExp(guessRegex(line)).test(line), line).toBe(true);
    }
    expect(guessRegex('message=hello')).toContain('(?<msg>');
    expect(escapeRegex('a.b*(c)/d #')).toBe('a\\.b\\*\\(c\\)\\/d #');
    const line = '2024-05-01 10:00:00 [main] WARN com.acme.Db - slow query';
    const fields = customFormat({ name: 'g', pattern: guessRegex(line) }, { year: 2024 }).parse(line);
    expect(fields).toMatchObject({ thread: 'main', logger: 'com.acme.Db', message: 'slow query' });
  });
});

describe('prompts and answers', () => {
  it('carry the context and the rules', () => {
    const x = extractPrompt(['line one', 'line two']);
    expect(x.system).toContain('(?<name>...)');
    expect(x.system).toContain('JSON only');
    expect(x.user).toBe('Sample lines (the first one must match):\nline one\nline two');

    const e = explainEntryPrompt('Log4j', ['b1', 'b2'], 'the entry', ['a1'], true);
    expect(e.user).toContain('Log format: Log4j');
    expect(e.user).toContain('filtered view');
    expect(e.user).toContain('b1\nb2\n\n>>> Entry to explain:\nthe entry\n<<<\n\nContext after:\na1');
    expect(explainEntryPrompt('x', [], 'z'.repeat(10_000), [], false).user.length).toBeLessThan(5000);

    expect(explainPatternPrompt('JSON', 'user <*> logged in', 1234, ['user 7 logged in']).user).toContain('seen 1,234 times');

    const f = filterPrompt(
      'errors from the db logger in the last hour',
      [{ key: 'logger', top: ['com.acme.Db', 'com.acme.Api'] }, { key: 'status', top: ['200'] }],
      [{ level: 'ERROR', count: 5 }, { level: 'INFO', count: 90 }],
      1714557600000,
      1714561200000,
      new Date(2024, 4, 1, 12, 0, 0),
    );
    expect(f.user).toContain('Request: errors from the db logger');
    expect(f.user).toContain('- logger: com.acme.Db, com.acme.Api');
    expect(f.user).toContain('Levels present: error (5), info (90)');
    expect(f.user).toContain('Now: 2024-05-01T12:00:00');
    expect(f.system).toContain('since:-3h');
    expect(f.system).toContain('"query"');
  });

  it('checks extract answers', () => {
    const sample = '2024-05-01 10:00:00 INFO hello';
    const ok = parseExtract('Here you go:\n```json\n{"name": "My app", "pattern": "^(?<time>\\\\S+ \\\\S+) (?<level>\\\\w+) (?<message>.*)$"}\n```', sample);
    expect(ok).toEqual({ ok: true, value: { name: 'My app', pattern: '^(?<time>\\S+ \\S+) (?<level>\\w+) (?<message>.*)$' } });
    const err = (a: string) => {
      const r = parseExtract(a, sample);
      return r.ok ? '' : r.error;
    };
    expect(err('I cannot do that')).toContain('JSON');
    expect(err('{"pattern": "unterminated}')).toContain('JSON');
    expect(err('{"pattern": "(?<a>[x"}')).toContain('does not compile');
    expect(err('{"pattern": "^\\\\d+"}')).toContain('no named groups');
    expect(err('{"pattern": "^(?<x>ERROR)"}')).toContain('does not match');
    const unnamed = parseExtract('{"pattern": "(?<message>.*)"}', sample);
    expect(unnamed.ok && unnamed.value.name).toBe('Extracted');
  });

  it('reads filter answers, which the query validator then checks', () => {
    const r = parseFilter('{"query": " level>=error logger:*Db* since:-1h ", "explanation": "Errors from Db"}');
    expect(r).toEqual({ ok: true, value: { query: 'level>=error logger:*Db* since:-1h', explanation: 'Errors from Db' } });
    expect(validateQuery('level>=error logger:*Db* since:-1h')).toBeNull();
    expect(parseFilter('level:error').ok).toBe(false);
    expect(parseFilter('{"query": ""}').ok).toBe(false);
    const bad = parseFilter('{"query": "/[/"}');
    expect(bad.ok && validateQuery(bad.value.query)).toBeTruthy();
  });

  it('renders answers as plain text', () => {
    expect(plainAnswer('## Meaning\n- **Job** finished normally.\n```\ncode\n```')).toBe('Meaning\n• Job finished normally.\ncode');
  });
});

// ------------------------------------------------------------------ wire formats

interface Seen {
  method: string;
  url: string;
  headers: http.IncomingHttpHeaders;
  body: string;
}

describe('wire formats against a local server', () => {
  let server: http.Server;
  let base = '';
  const seen: Seen[] = [];
  let reply: (s: Seen, res: http.ServerResponse) => void = (_s, res) => res.end('{}');

  beforeAll(async () => {
    server = http.createServer((req, res) => {
      let body = '';
      req.on('data', (c) => (body += c));
      req.on('end', () => {
        const s = { method: req.method ?? '', url: req.url ?? '', headers: req.headers, body };
        seen.push(s);
        reply(s, res);
      });
    });
    await new Promise<void>((r) => server.listen(0, '127.0.0.1', r));
    base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`;
  });
  afterAll(() => new Promise<void>((r) => server.close(() => r())));

  const json = (status: number, body: unknown) => (_s: Seen, res: http.ServerResponse) => {
    res.writeHead(status, { 'content-type': 'application/json' });
    res.end(typeof body === 'string' ? body : JSON.stringify(body));
  };
  const P = { system: 'SYS', user: 'USER TEXT' };
  // The Anthropic URL is fixed, so route its requests to the local server through the fetch seam.
  const viaLocal: typeof fetch = (url, init) => fetch(String(url).replace('https://api.anthropic.com', base), init);

  it('Claude Messages API end to end', async () => {
    seen.length = 0;
    reply = json(200, { content: [{ type: 'thinking', thinking: '' }, { type: 'text', text: 'Hello ' }, { type: 'text', text: 'there' }], stop_reason: 'end_turn' });
    const c: AiConfig = { provider: 'anthropic', model: 'claude-opus-5-5', baseUrl: '', key: 'sk-test-key' };
    expect(await sendAi(c, P, { fetch: viaLocal })).toBe('Hello there');
    const s = seen[0];
    expect(s.method).toBe('POST');
    expect(s.url).toBe('/v1/messages');
    expect(s.headers['x-api-key']).toBe('sk-test-key');
    expect(s.headers['anthropic-version']).toBe('2026-10-21');
    expect(s.headers['anthropic-beta']).toBe('server-side-fallback-2026-07-01');
    expect(s.headers.authorization).toBeUndefined();
    const b = JSON.parse(s.body);
    expect(b).toMatchObject({ model: 'claude-opus-5-5', max_tokens: 16000, system: 'SYS', messages: [{ role: 'user', content: 'USER TEXT' }], fallbacks: 'default' });
    expect(b.temperature).toBeUndefined();
    // other models get no fallback parameter (it would be rejected)
    const r = buildRequest({ ...c, model: 'claude-haiku-4-5' }, P);
    expect(r.headers['anthropic-beta']).toBeUndefined();
    expect(JSON.parse(r.body).fallbacks).toBeUndefined();
  });

  it('OpenAI-compatible chat completions end to end', async () => {
    seen.length = 0;
    reply = json(200, { choices: [{ index: 0, message: { role: 'assistant', content: '  local answer ' } }] });
    const c = aiConfig({ provider: 'openai', openaiBaseUrl: `${base}/v1/`, openaiModel: 'llama3.1' }, null);
    expect(await sendAi(c, P)).toBe('local answer');
    expect(seen[0].url).toBe('/v1/chat/completions');
    expect(seen[0].headers.authorization).toBeUndefined(); // no key: no header
    expect(JSON.parse(seen[0].body)).toEqual({ model: 'llama3.1', stream: false, messages: [{ role: 'system', content: 'SYS' }, { role: 'user', content: 'USER TEXT' }] });
    await sendAi({ ...c, key: 'k-123' }, P);
    expect(seen[1].headers.authorization).toBe('Bearer k-123');
  });

  it.each([
    [401, { type: 'error', error: { type: 'authentication_error', message: 'invalid x-api-key' } }, 'API key was rejected (401)'],
    [429, { type: 'error', error: { type: 'rate_limit_error', message: 'slow down' } }, 'Rate limited'],
    [529, { type: 'error', error: { type: 'overloaded_error', message: 'Overloaded' } }, 'overloaded (529)'],
    [500, '<html>oops</html>', 'had an error (500)'],
    [200, 'not json', 'not valid JSON'],
    [200, { content: [], stop_reason: 'refusal' }, 'declined'],
    [200, { content: [] }, 'empty answer'],
  ])('turns HTTP %i into a clear message', async (status, body, expected) => {
    reply = json(status, body);
    const c: AiConfig = { provider: 'anthropic', model: 'claude-opus-5-5', baseUrl: '', key: 'sk-secret-value' };
    const e = await sendAi(c, P, { fetch: viaLocal }).catch((x) => x);
    expect(e).toBeInstanceOf(AiError);
    expect(e.message).toContain(expected);
    expect(e.message).not.toContain('sk-secret-value');
  });

  it('masks a key the server echoes in an error', () => {
    expect(() => parseResponse('openai', 401, '{"error":{"message":"Incorrect API key provided: sk-proj-abcdefghijklmnop1234"}}')).toThrow(/provided: <key>$/);
  });

  it('times out, cancels and reports an unreachable endpoint', async () => {
    reply = () => {}; // never answers
    const c = aiConfig({ provider: 'openai', openaiBaseUrl: `${base}/v1` }, null);
    const e = await sendAi(c, P, { timeoutMs: 150 }).catch((x) => x);
    expect(e).toBeInstanceOf(AiError);
    expect(e.message).toBe(`No answer from ${aiHost(c)} after 0 s`);

    const ctl = new AbortController();
    setTimeout(() => ctl.abort(), 100);
    const a = await sendAi(c, P, { signal: ctl.signal, timeoutMs: 30_000 }).catch((x) => x);
    expect(a.name).toBe('AbortError');

    // a port that was just free: nothing listens there
    const probe = http.createServer();
    await new Promise<void>((r) => probe.listen(0, '127.0.0.1', r));
    const port = (probe.address() as AddressInfo).port;
    await new Promise<void>((r) => probe.close(() => r()));
    const down = aiConfig({ provider: 'openai', openaiBaseUrl: `http://127.0.0.1:${port}/v1` }, null);
    const d = await sendAi(down, P).catch((x) => x);
    expect(d.message).toBe(`Could not reach 127.0.0.1:${port}: the connection failed`);
  });

  it('parses responses directly', () => {
    expect(parseResponse('openai', 200, '{"choices":[{"message":{"content":"x"}}]}')).toBe('x');
    expect(() => parseResponse('anthropic', 404, '{"error":{"message":"model: nope"}}')).toThrow('Model or endpoint not found (404). Check the model name and base URL: model: nope');
    expect(aiHost({ provider: 'anthropic', baseUrl: '' })).toBe('api.anthropic.com');
    expect(aiHost({ provider: 'openai', baseUrl: 'http://localhost:11434/v1' })).toBe('localhost:11434');
  });
});
