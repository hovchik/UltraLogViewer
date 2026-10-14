import { describe, expect, it } from 'vitest';
import { tokenize } from '../src/core/tokenizer';
import { formatClock, formatTimeCell, formatGap, histogramTip, signedDuration } from '../src/core/timestamp';

const toks = (s: string) => tokenize(s).map((t) => [t.kind, s.slice(t.start, t.start + t.length)]);

describe('tokenizer', () => {
  it('finds keys, guids, ips and durations', () => {
    expect(toks('user=bob id=550e8400-e29b-41d4-a716-446655440000 from 10.0.0.12:8080 took 35ms')).toEqual([
      ['key', 'user'],
      ['key', 'id'],
      ['guid', '550e8400-e29b-41d4-a716-446655440000'],
      ['ip', '10.0.0.12:8080'],
      ['duration', '35ms'],
    ]);
  });
  it('finds methods and trims trailing punctuation from urls', () => {
    expect(toks('GET https://x.io/a?b=1, done')).toEqual([
      ['method', 'GET'],
      ['url', 'https://x.io/a?b=1'],
    ]);
  });
  it('finds paths, exceptions, strings and dates', () => {
    expect(toks('at /var/log/app.log')).toEqual([['path', '/var/log/app.log']]);
    expect(toks('java.lang.NullPointerException')).toEqual([['exception', 'java.lang.NullPointerException']]);
    expect(toks('"quoted text"')).toEqual([['string', '"quoted text"']]);
    expect(toks('2024-01-02T03:04:05.123Z')).toEqual([['date', '2024-01-02T03:04:05.123Z']]);
    expect(toks('C:\\logs\\app.log')).toEqual([['path', 'C:\\logs\\app.log']]);
  });
  it('finds numbers, keys and keywords', () => {
    expect(toks('count 42 ok=true')).toEqual([
      ['number', '42'],
      ['key', 'ok'],
      ['keyword', 'true'],
    ]);
  });
  it('does not find numbers inside words', () => {
    expect(toks('v1.2.3').filter(([k]) => k === 'number')).toEqual([]);
  });
  it('caps the token count and the scanned length', () => {
    const line = Array.from({ length: 50 }, (_, i) => `n${i}=${i}`).join(' ');
    expect(tokenize(line, 7)).toHaveLength(7);
    expect(tokenize('x'.repeat(2000) + ' 12345 67890')).toEqual([]);
    expect(tokenize('x'.repeat(1990) + ' 42').map((t) => t.kind)).toEqual(['number']);
  });
  it('returns ascending, non-overlapping tokens', () => {
    const t = tokenize('2024-05-01 10:00:00 GET /api/orders/12 status=200 in 35 ms "ok" from 192.168.1.1 null');
    for (let i = 1; i < t.length; i++) expect(t[i].start).toBeGreaterThanOrEqual(t[i - 1].start + t[i - 1].length);
  });
});

describe('time column formatting', () => {
  const t = Date.UTC(2024, 0, 2, 3, 4, 5, 123);
  it('formats full and time-of-day in UTC and local time', () => {
    expect(formatTimeCell('full', t, 'orig', { utc: true })).toBe('2024-01-02 03:04:05.123Z');
    expect(formatTimeCell('time', t, 'orig', { utc: true })).toBe('03:04:05.123Z');
    expect(formatTimeCell('full', t, 'orig')).toBe(formatClock(t));
    expect(formatClock(t)).toMatch(/^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.123$/);
  });
  it('keeps the original text without a parsed time or in original mode', () => {
    expect(formatTimeCell('full', null, 'orig')).toBe('orig');
    expect(formatTimeCell('original', t, 'orig')).toBe('orig');
    expect(formatTimeCell('delta', null, 'orig', { prev: t })).toBe('orig');
  });
  it('formats relative and delta durations', () => {
    expect(formatTimeCell('relative', t + 1250, '', { ref: t })).toBe('+1.250 s');
    expect(formatTimeCell('relative', t - 182000, '', { ref: t })).toBe('-03m 02.000s');
    expect(formatTimeCell('relative', t, '', { ref: t })).toBe('0 ms');
    expect(formatTimeCell('delta', t + 35, '', { prev: t })).toBe('+35 ms');
    expect(formatTimeCell('delta', t, '', {})).toBe('');
    expect(signedDuration(-5)).toBe('-5 ms');
  });
  it('formats compact gap labels', () => {
    expect(formatGap(850)).toBe('+850ms');
    expect(formatGap(32000)).toBe('+32s');
    expect(formatGap(59999)).toBe('+59s');
    expect(formatGap(125000)).toBe('+2m');
    expect(formatGap(5 * 3600000 + 1)).toBe('+5h');
    expect(formatGap(3 * 86400000)).toBe('+3d');
  });
  it('builds histogram tooltips', () => {
    const a = new Date(2024, 4, 1, 8, 0, 0).getTime();
    expect(histogramTip(a, a + 60000, [5, 2, 1])).toBe('08:00:00 – 08:01:00\n8 entries\n2 warnings · 1 error');
    expect(histogramTip(a, a + 60000, [5, 0, 0])).toBe('08:00:00 – 08:01:00\n5 entries');
    const late = new Date(2024, 4, 1, 23, 59, 0).getTime();
    expect(histogramTip(late, late + 120000, [1, 0, 3])).toBe('2024-05-01 23:59:00 – 2024-05-02 00:01:00\n4 entries\n3 errors');
  });
});
