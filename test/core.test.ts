import { describe, expect, it } from 'vitest';
import * as fs from 'node:fs';
import * as zlib from 'node:zlib';
import { matchTimestamp, parseTimestamp } from '../src/core/timestamp';
import { detectLevel, parseLevel } from '../src/core/levels';
import { detectFormat, detectFormatInfo, formatLabel } from '../src/core/formats';
import { compileQuery, fieldToken, parseQuery, splitToken, tokenAt } from '../src/core/query';
import { Level } from '../src/core/types';
import { FileSource } from '../src/core/fileSource';
import { LOG4J, tmpFile } from './helpers';

describe('format identification', () => {
  it('reports how the format was chosen and how well the sample fits', () => {
    const json = detectFormatInfo(['{"level":"info","msg":"a"}', '{"level":"warn","msg":"b"}', 'not json']);
    expect(json).toMatchObject({ auto: true, matched: 2, sampled: 3 });
    expect(json.format.id).toBe('json');
    expect(formatLabel(json.format.id, json.format.name)).toBe('JSON lines');
    const forced = detectFormatInfo(['hello', '{"level":"info"}'], { force: 'json' });
    expect(forced).toMatchObject({ auto: false, matched: 1, sampled: 2 });
    expect(detectFormatInfo(['', '  '])).toMatchObject({ auto: true, matched: 0, sampled: 0 });
    expect(formatLabel('custom:Mine', 'Mine')).toBe('Custom: Mine');
  });
  it('opens text files without timestamps as one entry per line', async () => {
    const p = tmpFile('notes.txt', 'Release notes\n==========\n\n  - libfoo 1.2\nerror: could not copy config.ini\ndone\n');
    const src = new FileSource(1, p);
    await src.open();
    const info = src.info();
    expect(info).toMatchObject({ formatId: 'plain', formatLabel: 'Plain text', formatAuto: true, lines: 6, entries: 6 });
    expect([...Array(src.entryCount)].map((_, i) => src.entryLevel.get(i))).toEqual([0, 0, 0, 0, Level.Error, 0]);
    expect(Number.isNaN(src.entryTime.get(0))).toBe(true);
    src.close();
  });
});

describe('timestamps', () => {
  it('parses ISO with timezone and fractions', () => {
    expect(matchTimestamp('2024-05-01T10:00:00.123Z rest')!.ms).toBe(Date.UTC(2024, 4, 1, 10, 0, 0, 123));
    expect(matchTimestamp('2024-05-01 10:00:00,5+02:00 x')!.ms).toBe(Date.UTC(2024, 4, 1, 8, 0, 0, 500));
    expect(matchTimestamp('[2024-05-01 10:00:00] local.ERROR')!.length).toBe(21);
  });
  it('parses apache, syslog, day-first and epoch', () => {
    expect(matchTimestamp('10/Oct/2023:13:55:36 -0700')!.ms).toBe(Date.UTC(2023, 9, 10, 20, 55, 36));
    expect(matchTimestamp('Oct  3 04:05:06 host', 2022)!.ms).toBe(new Date(2022, 9, 3, 4, 5, 6).getTime());
    expect(matchTimestamp('31.12.2024 23:59:58 x')!.ms).toBe(new Date(2024, 11, 31, 23, 59, 58).getTime());
    expect(parseTimestamp(1700000000)).toBe(1700000000000);
    expect(parseTimestamp('1700000000123')).toBe(1700000000123);
    expect(matchTimestamp('hello')).toBeNull();
  });
});

describe('levels', () => {
  it('maps words and numbers', () => {
    expect(parseLevel('warning')).toBe(Level.Warn);
    expect(parseLevel(50)).toBe(Level.Error);
    expect(parseLevel('E')).toBe(Level.Error);
    expect(detectLevel('12:00 [error] something')).toBe(Level.Error);
    expect(detectLevel('nothing here')).toBe(Level.None);
    // plain text often starts the line with the level
    expect(detectLevel('error: could not copy config.ini')).toBe(Level.Error);
    expect(detectLevel('Warning: disk almost full')).toBe(Level.Warn);
    expect(detectLevel('Errors found: 0')).toBe(Level.None);
  });
});

describe('format detection', () => {
  it('detects log4j-style text and parses columns', () => {
    const f = detectFormat(LOG4J.split('\n'));
    expect(f.id).toBe('timestamped');
    const p = f.parse('2024-05-01 10:00:03,004 [worker-2] ERROR com.acme.Db - Query failed\njava.sql.SQLException: timeout');
    expect(p).toMatchObject({ time: '2024-05-01 10:00:03,004', thread: 'worker-2', level: 'ERROR', logger: 'com.acme.Db' });
    expect(p.message).toBe('Query failed\njava.sql.SQLException: timeout');
    expect(f.isEntryStart('\tat x')).toBe(false);
  });
  it('parses NLog pipes and Spring Boot layouts', () => {
    const f = detectFormat(['2024-05-01 10:00:00.1234|WARN|Acme.Service|Low disk']);
    expect(f.parse('2024-05-01 10:00:00.1234|WARN|Acme.Service|Low disk')).toMatchObject({ level: 'WARN', logger: 'Acme.Service', message: 'Low disk' });
    const s = '2024-05-01T10:00:00.123Z ERROR 1234 --- [worker-1] c.a.Service : boom';
    expect(f.parse(s)).toMatchObject({ level: 'ERROR', thread: 'worker-1', logger: 'c.a.Service', message: 'boom' });
  });
  it('detects JSON lines', () => {
    const f = detectFormat(['{"@timestamp":"2024-05-01T10:00:00Z","level":"error","message":"x","logger":"a"}']);
    expect(f.id).toBe('json');
    expect(f.quick('{"time":1700000000000,"level":50,"msg":"m"}')).toEqual({ level: Level.Error, time: 1700000000000 });
    expect(f.parse('{"level":"info","msg":"hello","name":"svc"}')).toMatchObject({ level: 'INFO', message: 'hello', logger: 'svc' });
  });
  it('detects access logs, syslog, rfc5424, logcat, w3c', () => {
    expect(detectFormat(['127.0.0.1 - frank [10/Oct/2023:13:55:36 -0700] "GET /a.gif HTTP/1.0" 500 2326 "-" "curl"']).id).toBe('access');
    expect(detectFormat(['Oct  3 04:05:06 myhost sshd[123]: Accepted password']).id).toBe('syslog');
    const f5 = detectFormat(['<165>1 2003-10-11T22:14:15.003Z mymachine evntslog - ID47 [exampleSDID@32473 iut="3"] An application event']);
    expect(f5.id).toBe('syslog5424');
    expect(f5.parse('<165>1 2003-10-11T22:14:15.003Z mymachine evntslog - ID47 - hi')).toMatchObject({ host: 'mymachine', level: 'INFO', message: 'hi' });
    expect(detectFormat(['10-14 12:00:00.123  1234  5678 E ActivityManager: crash']).id).toBe('logcat');
    const w = detectFormat(['#Software: IIS', '#Fields: date time cs-method cs-uri-stem sc-status', '2024-05-01 10:00:00 GET /x 404']);
    expect(w.id).toBe('w3c');
    expect(w.quick('2024-05-01 10:00:00 GET /x 404').level).toBe(Level.Warn);
  });
  it('falls back to plain and supports custom regex formats', () => {
    expect(detectFormat(['hello', 'world']).id).toBe('plain');
    const f = detectFormat(['A|2024-05-01 10:00:00|E|boom'], {
      customFormats: [{ name: 'Mine', pattern: '^(?<app>\\w)\\|(?<timestamp>[^|]+)\\|(?<level>\\w)\\|(?<message>.*)$' }],
    });
    expect(f.id).toBe('custom:Mine');
    expect(f.quick('A|2024-05-01 10:00:00|E|boom').level).toBe(Level.Error);
    expect(f.parse('A|2024-05-01 10:00:00|E|boom')).toMatchObject({ app: 'A', time: '2024-05-01 10:00:00', message: 'boom' });
  });
});

describe('query language', () => {
  it('handles AND, OR, NOT, phrases, regex and fields', () => {
    expect(parseQuery('a OR b -c').length).toBe(2);
    const q = compileQuery('timeout OR refused -retry logger:Db');
    const fields = (l: string) => (k: string) => (k === 'logger' ? l : '');
    expect(q('query timeout', fields('com.acme.Db'))).toBe(true);
    expect(q('connection refused', fields('com.acme.Db'))).toBe(true);
    expect(q('timeout retry', fields('com.acme.Db'))).toBe(false);
    expect(q('timeout', fields('com.acme.App'))).toBe(false);
    expect(compileQuery('/err(or)?\\s+\\d+/')('ERROR 42', () => '')).toBe(true);
    expect(compileQuery('"exact phrase"')('an exact phrase here', () => '')).toBe(true);
  });

  it('parses quoted field values and autocomplete tokens', () => {
    const t = parseQuery('logger:"com.acme Db" -thread:"worker 1" http.status:5');
    expect(t).toHaveLength(3);
    expect([t[0].any[0].field, t[0].any[0].text, t[0].negate]).toEqual(['logger', 'com.acme Db', false]);
    expect([t[1].any[0].field, t[1].any[0].text, t[1].negate]).toEqual(['thread', 'worker 1', true]);
    expect(t[2].any[0].field).toBe('http.status');
    expect(parseQuery('-/re/')[0]).toMatchObject({ negate: true, any: [{ text: 're', regex: true }] });
    expect(parseQuery('error logger:')).toHaveLength(1); // a field name being typed is ignored
    expect(parseQuery('logger:/Db$/')[0].any[0]).toMatchObject({ field: 'logger', text: 'Db$', regex: true });
    expect(parseQuery('level:"err')[0].any[0]).toMatchObject({ field: 'level', text: 'err' });
    expect(parseQuery('logger:"" ""')).toHaveLength(0);

    expect(tokenAt('error logger:Db', 15)).toEqual([6, 9]);
    expect(tokenAt('error level:"a b', 16)).toEqual([6, 10]);
    expect(tokenAt('abc  ', 5)).toEqual([5, 0]);
    expect(splitToken('-logger:Db')).toEqual(['-', 'logger', 'Db']);
    expect(splitToken('plain')).toEqual(['', null, 'plain']);
    expect(splitToken('level:"a b')).toEqual(['', 'level', 'a b']);
    expect(fieldToken('logger', 'com.acme')).toBe('logger:com.acme');
    expect(fieldToken('logger', 'a b')).toBe('logger:"a b"');
    expect(fieldToken('logger', 'say "hi"')).toBe('logger:"say hi"');
  });
});

describe('FileSource', () => {
  it('indexes multi-line entries with levels and times', async () => {
    const s = new FileSource(0, tmpFile('a.log', LOG4J), { chunkSize: 64 });
    await s.open();
    expect(s.lineCount).toBe(9);
    expect(s.entryCount).toBe(6);
    expect([...s.entryLevel.view()]).toEqual([Level.Info, Level.Debug, Level.Warn, Level.Error, Level.Info, Level.Fatal]);
    expect(s.entryLines(3)).toEqual([3, 6]);
    expect(s.readEntries(3, 4)[0].split('\n')).toHaveLength(4);
    expect(s.entryForLine(5)).toBe(3);
    s.close();
  });

  it('handles CRLF, BOM and an unterminated last line', async () => {
    const s = new FileSource(0, tmpFile('b.log', '﻿one\r\ntwo\r\nthree'));
    await s.open();
    expect(s.readEntries(0, 3)).toEqual(['one', 'two', 'three']);
    s.close();
  });

  it('follows appends, continuation lines and truncation', async () => {
    const p = tmpFile('c.log', '2024-01-01 00:00:00 INFO a\n2024-01-01 00:00:01 ERROR b\n');
    const s = new FileSource(0, p);
    await s.open();
    expect(s.entryCount).toBe(2);
    fs.appendFileSync(p, '  at stack\n2024-01-01 00:00:02 WARN c\npartial');
    let r = await s.refresh();
    expect(r).toMatchObject({ changed: true, reset: false, firstChangedEntry: 1 });
    expect(s.entryCount).toBe(3);
    expect(s.readEntries(1, 2)[0]).toBe('2024-01-01 00:00:01 ERROR b\n  at stack');
    expect(s.readEntries(2, 3)[0]).toBe('2024-01-01 00:00:02 WARN c\npartial');
    fs.appendFileSync(p, ' line done\n');
    await s.refresh();
    expect(s.readEntries(2, 3)[0]).toBe('2024-01-01 00:00:02 WARN c\npartial line done');
    fs.writeFileSync(p, '2024-01-01 00:00:09 INFO new\n');
    r = await s.refresh();
    expect(r.reset).toBe(true);
    expect(s.readEntries(0, 5)).toEqual(['2024-01-01 00:00:09 INFO new']);
    s.close();
  });

  it('opens gzip and UTF-16 files', async () => {
    const gz = new FileSource(0, tmpFile('d.log.gz', zlib.gzipSync(Buffer.from(LOG4J))));
    await gz.open();
    expect(gz.entryCount).toBe(6);
    expect(gz.compressed).toBe(true);
    expect(gz.live).toBe(false);
    gz.close();
    const u16 = new FileSource(0, tmpFile('e.log', Buffer.concat([Buffer.from([0xff, 0xfe]), Buffer.from('héllo\nwörld\n', 'utf16le')])));
    await u16.open();
    expect(u16.encoding).toBe('utf-16le');
    expect(u16.readEntries(0, 2)).toEqual(['héllo', 'wörld']);
    u16.close();
  });

  it('decodes legacy Windows-1252 text', async () => {
    const s = new FileSource(0, tmpFile('f.log', Buffer.from([0x63, 0x61, 0x66, 0xe9, 0x0a])));
    await s.open();
    expect(s.encoding).toBe('windows-1252');
    expect(s.readEntries(0, 1)).toEqual(['café']);
    s.close();
  });
});
