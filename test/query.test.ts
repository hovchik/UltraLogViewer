import { describe, expect, it } from 'vitest';
import {
  compileQuery,
  compileQueryWithTimeRange,
  describeQuery,
  fieldToken,
  parseQueryExpr,
  QueryError,
  shortcutReference,
  splitToken,
  TIME_FIELD,
  TIME_SHORTCUTS,
  validateQuery,
} from '../src/core/query';

const NOW = Date.UTC(2024, 4, 1, 12, 0, 0);
const d = (q: string) => describeQuery(parseQueryExpr(q, false, NOW));

/** Evaluates a query against an entry text and a field map (keys matched case-insensitively, like LogDocument). */
function e(q: string, text = '', fields: Record<string, string> = {}, cs = false): boolean {
  const f = new Map(Object.entries(fields).map(([k, v]) => [k.toLowerCase(), v]));
  return compileQuery(q, cs, NOW)(text, (k) => f.get(k.toLowerCase()) ?? '');
}

function error(q: string): string {
  try {
    compileQuery(q, false, NOW);
  } catch (err) {
    expect(err).toBeInstanceOf(QueryError);
    return (err as Error).message;
  }
  throw new Error(`no error for ${q}`);
}

describe('quick filter: parser', () => {
  it('keeps the old syntax', () => {
    expect(d('a b')).toBe('(and "a" "b")');
    expect(d('timeout OR refused -retry logger:Db')).toBe('(and (or "timeout" "refused") (not "retry") logger:"Db")');
    expect(d('a | b')).toBe('(or "a" "b")');
    expect(d('a AND NOT b')).toBe('(and "a" (not "b"))');
    expect(d('"exact phrase"')).toBe('"exact phrase"');
    expect(d('logger:/Db$/')).toBe('logger:/Db$/');
    expect(d('level:"err')).toBe('level:"err"'); // unclosed quote while typing
    expect(d('logger:"" ""')).toBe('');
    expect(d('/usr/')).toBe('/usr/'); // regex token, as before
    expect(d('--verbose')).toBe('(not "-verbose")');
    expect(d('-')).toBe('"-"');
    expect(d('http://example.com')).toBe('http:"//example.com"'); // field:value as before
    expect(d('me@host:22')).toBe('"me@host:22"');
    expect(d('foo()')).toBe('"foo()"');
    expect(d('a)')).toBe('"a)"');
  });

  it('parses boolean operators with parentheses', () => {
    expect(d('a b OR c')).toBe('(and "a" (or "b" "c"))'); // OR binds tighter, as it always did
    expect(d('(a or b) and not c')).toBe('(and (or "a" "b") (not "c"))');
    expect(d('a AND (b OR (c d))')).toBe('(and "a" (or "b" (and "c" "d")))');
    expect(d('NOT (a b)')).toBe('(not (and "a" "b"))');
    expect(d('-(a OR b)')).toBe('(not (or "a" "b"))');
    expect(d('not not a')).toBe('"a"');
    expect(d('not a or b')).toBe('(or (not "a") "b")');
    expect(d('(x foo())')).toBe('(and "x" "foo()")');
    expect(d('(level:error or exists(traceId))')).toBe('(or level:"error" exists(traceid))');
    expect(d('a && (b || c)')).toBe('(and "a" (or "b" "c"))');
  });

  it('tolerates queries still being typed', () => {
    expect(d('a or')).toBe('"a"');
    expect(d('a and')).toBe('"a"');
    expect(d('a not')).toBe('"a"');
    expect(d('OR a')).toBe('"a"');
    expect(d('(a or b')).toBe('(or "a" "b")');
    expect(d('(')).toBe('');
    expect(d('a status>=')).toBe('"a"');
    expect(d('a exists(tra')).toBe('"a"');
    expect(d('a user in (ali')).toBe('"a"');
    expect(d('a since:')).toBe('"a"');
  });

  it('reports bad queries clearly', () => {
    expect(error('a )')).toBe('Unmatched ")"');
    expect(error('a ()')).toBe('Empty parentheses "()"');
    expect(error('exists()')).toBe('exists() needs a field name, e.g. exists(traceId)');
    expect(error('user in ()')).toBe('user in (): add at least one value, e.g. user in (alice,bob)');
    expect(error('since:soon')).toContain('since:soon: use a relative time such as -3h');
    expect(error('duration>2x')).toContain('duration>2x: unknown unit');
    expect(error('level>=loud')).toContain('"loud" is not a level');
    expect(validateQuery('/(unclosed/')).not.toBeNull();
    expect(validateQuery('status>=500 and (user in (a,b) or exists(x)) since:-1h')).toBeNull();
  });

  it('parses comparisons, lists and exists', () => {
    expect(d('status>=500')).toBe('status>=500');
    expect(d('STATUS!=200')).toBe('status!=200');
    expect(d('user?=bob')).toBe('user?=bob');
    expect(d('x==1')).toBe('x==1');
    expect(d('a<b')).toBe('a<b');
    expect(d('user=="john smith"')).toBe('user==john smith');
    expect(d('http.status<=499')).toBe('http.status<=499');
    expect(d('user in (alice, bob)')).toBe('user in (alice,bob)');
    expect(d('user IN ("a b",c)')).toBe('user in (a b,c)');
    expect(d('-user in (alice)')).toBe('(not user in (alice))');
    expect(d('exists(traceId) x')).toBe('(and exists(traceid) "x")');
    expect(d('a->b')).toBe('"a->b"');
    expect(d('x=>y')).toBe('"x=>y"');
    expect(d('a=b')).toBe('"a=b"'); // a single "=" stays text, so "status=200" still finds that text
  });

  it('resolves relative and absolute times', () => {
    const t = (q: string) => parseQueryExpr(q, false, NOW) as { kind: 'time'; since: boolean; ms: number };
    expect(t('since:-3h')).toMatchObject({ kind: 'time', since: true, ms: NOW - 3 * 3_600_000 });
    expect(t('until:-10m')).toMatchObject({ since: false, ms: NOW - 10 * 60_000 });
    expect(t('since:1h30m').ms).toBe(NOW - 90 * 60_000);
    expect(t('until:now').ms).toBe(NOW);
    expect(t('since:2024-05-01T10:00').ms).toBe(new Date(2024, 4, 1, 10, 0).getTime());
    expect(t('since:2024-05-01T10:00Z').ms).toBe(Date.UTC(2024, 4, 1, 10, 0));
    expect(t('since:"2024-05-01 10:00:05.5+02:00"').ms).toBe(Date.UTC(2024, 4, 1, 8, 0, 5, 500));
    expect(t('since:2024-05-01').ms).toBe(new Date(2024, 4, 1).getTime());
    const today = t('since:today').ms;
    expect(t('since:yesterday').ms).toBe(today - 86_400_000);
    expect(today <= NOW && NOW - today < 86_400_000).toBe(true);
  });
});

describe('quick filter: evaluation', () => {
  it('compares numbers and durations', () => {
    expect(e('status>=500', '', { status: '503' })).toBe(true);
    expect(e('status>=500', '', { status: '200' })).toBe(false);
    expect(e('status>=500')).toBe(false);
    expect(e('status>=500', '', { status: 'abc' })).toBe(false);
    expect(e('status<400', '', { status: '399' })).toBe(true);
    expect(e('status>4e2', '', { status: '401' })).toBe(true);
    expect(e('status!=200', '', { status: '404' })).toBe(true);
    expect(e('status!=200', '', { status: '200' })).toBe(false);
    expect(e('status!=200')).toBe(false); // comparisons need the field...
    expect(e('-status==200')).toBe(true); // ...but a negated one does not
    expect(e('status==200', '', { status: '200.0' })).toBe(true);

    expect(e('duration>2s', '', { duration: '250ms' })).toBe(false);
    expect(e('duration>2s', '', { duration: '2.5s' })).toBe(true);
    expect(e('duration>2s', '', { duration: '1m' })).toBe(true);
    expect(e('duration>=2m', '', { duration: '2m30s' })).toBe(true);
    expect(e('duration>2s', '', { duration: '3' })).toBe(true); // a bare number is read in the query's unit
    expect(e('duration>2s', '', { duration: '1.5' })).toBe(false);
    expect(e('duration>500ms', '', { duration: '1500' })).toBe(true);
    expect(e('duration>2', '', { duration: '2.5s' })).toBe(true); // and the other way round
    expect(e('duration<1h', '', { duration: '59m' })).toBe(true);
    expect(e('duration==1s', '', { duration: '1000ms' })).toBe(true);
    expect(e('latency>100us', '', { latency: '0.2ms' })).toBe(true);
  });

  it('compares text, dates and levels', () => {
    expect(e('user==bob', '', { user: 'Bob' })).toBe(true);
    expect(e('user==bob', '', { user: 'Bob' }, true)).toBe(false);
    expect(e('user!=bob', '', { user: 'alice' })).toBe(true);
    expect(e('name>m', '', { name: 'zed' })).toBe(true);
    expect(e('name>m', '', { name: 'abe' })).toBe(false);
    expect(e('time>2024-05-01T10:00', '', { time: '2024-05-01 10:30:00' })).toBe(true);
    expect(e('time>2024-05-01T10:00', '', { time: '2024-05-01 09:30:00' })).toBe(false);

    expect(e('level>=warn', '', { level: 'WARNING' })).toBe(true);
    expect(e('level>=warn', '', { level: 'FATAL' })).toBe(true);
    expect(e('level>=warn', '', { level: 'INFO' })).toBe(false);
    expect(e('level<info', '', { level: 'DEBUG' })).toBe(true);
    expect(e('level==e', '', { level: 'ERROR' })).toBe(true);
    expect(e('level:warn', '', { level: 'WARNING' })).toBe(true); // contains, as before
  });

  it('matches lists, exists and optional fields', () => {
    expect(e('user in (alice,bob)', '', { user: 'Alice' })).toBe(true);
    expect(e('user in (alice,bob)', '', { user: 'carol' })).toBe(false);
    expect(e('user in (alice,bob)')).toBe(false);
    expect(e('status in (500, 503)', '', { status: '503' })).toBe(true);
    expect(e('user in ("Mary Ann")', '', { user: 'mary ann' })).toBe(true);

    expect(e('exists(traceId)', '', { traceId: 'abc' })).toBe(true);
    expect(e('exists(traceId)', '', { traceId: '' })).toBe(false);
    expect(e('exists(traceId)')).toBe(false);
    expect(e('not exists(traceId)')).toBe(true);

    expect(e('env?=prod')).toBe(true);
    expect(e('env?=prod', '', { env: 'PROD' })).toBe(true);
    expect(e('env?=prod', '', { env: 'dev' })).toBe(false);
    expect(e('http.status>=500', '', { 'http.status': '502' })).toBe(true); // dotted JSON extras
  });

  it('filters by the indexed time', () => {
    const at = (ms: number) => ({ [TIME_FIELD]: String(ms) });
    expect(e('since:-3h', '', at(NOW - 3_600_000))).toBe(true);
    expect(e('since:-3h', '', at(NOW - 4 * 3_600_000))).toBe(false);
    expect(e('since:-3h')).toBe(false); // no timestamp: like the time-range filter, it does not pass
    expect(e('until:-10m', '', at(NOW - 3_600_000))).toBe(true);
    expect(e('until:-10m', '', at(NOW - 60_000))).toBe(false);
    expect(e('since:-1h until:-10m error', 'an error', at(NOW - 30 * 60_000))).toBe(true);
  });

  it('combines clauses', () => {
    const f = { status: '503', user: 'bob', level: 'ERROR' };
    expect(e('status>=500 and (user in (alice,bob) or exists(traceId))', 'x', f)).toBe(true);
    expect(e('status>=500 and not user==bob', 'x', f)).toBe(false);
    expect(e('(timeout or refused) -retry', 'connection refused', f)).toBe(true);
    expect(e('(timeout or refused) -retry', 'refused, retry', f)).toBe(false);
    expect(e('level:error OR status<300', '', f)).toBe(true);
  });

  it('runs indexed clauses before parsing the entry', () => {
    const asked: string[] = [];
    const field = (k: string) => {
      asked.push(k);
      return k === 'level' ? 'INFO' : k === TIME_FIELD ? String(NOW - 86_400_000) : 'x';
    };
    // The parsed-field clause comes first in the text but runs last; level and time reject the entry first.
    expect(compileQuery('logger:Db level:error', false, NOW)('text', field)).toBe(false);
    expect(asked).toEqual(['level']);
    asked.length = 0;
    expect(compileQuery('status>=500 since:-1h', false, NOW)('text', field)).toBe(false);
    expect(asked).toEqual([TIME_FIELD]);
    asked.length = 0;
    expect(compileQuery('logger:Db OR level:info', false, NOW)('text', field)).toBe(true);
    expect(asked).toEqual(['level']);
    asked.length = 0;
    // Text clauses run before parsed fields too.
    expect(compileQuery('user==x nomatch', false, NOW)('text', field)).toBe(false);
    expect(asked).toEqual([]);
  });
});

describe('quick filter: time range', () => {
  it('takes top-level since:/until: out as a range for the time index', () => {
    const r = compileQueryWithTimeRange('since:-3h until:-1h since:-2h', false, NOW);
    expect(r).toEqual({ predicate: null, from: NOW - 2 * 3_600_000, to: NOW - 3_600_000 });
    const m = compileQueryWithTimeRange('error since:-1h', false, NOW);
    expect(m.from).toBe(NOW - 3_600_000);
    expect(m.predicate!('an error', () => '')).toBe(true); // the time is no longer tested per entry
    // Inside OR or NOT the clause stays in the predicate.
    const o = compileQueryWithTimeRange('since:-1h or level:error', false, NOW);
    expect([o.from, o.to]).toEqual([null, null]);
    expect(o.predicate!('', (k) => (k === TIME_FIELD ? String(NOW - 60_000) : ''))).toBe(true);
    expect(compileQueryWithTimeRange('', false, NOW)).toEqual({ predicate: null, from: null, to: null });
  });
});

describe('quick filter: autocomplete and time shortcuts', () => {
  it('splits tokens with operators', () => {
    expect(splitToken('status>=5')).toEqual(['', 'status', '>=', '5']);
    expect(splitToken('(user?=')).toEqual(['(', 'user', '?=', '']);
    expect(splitToken('-(level:er')).toEqual(['-(', 'level', ':', 'er']);
    expect(splitToken('a->b')).toEqual(['', null, '', 'a->b']);
    expect(fieldToken('status', '500', '>=')).toBe('status>=500');
    expect(fieldToken('user', 'a b', '==')).toBe('user=="a b"');
  });

  it('counts time shortcuts back from the last entry unless live', () => {
    expect(shortcutReference(1000, false, 5000)).toBe(1000);
    expect(shortcutReference(1000, true, 5000)).toBe(5000);
    expect(shortcutReference(null, false, 5000)).toBe(5000);
    expect(TIME_SHORTCUTS.map(([l]) => l)).toEqual(['Now', '-15m', '-1h', '-24h']);
    expect(TIME_SHORTCUTS[2][1]).toBe(3_600_000);
  });
});
