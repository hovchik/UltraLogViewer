import * as dgram from 'node:dgram';
import * as fs from 'node:fs';
import * as net from 'node:net';
import * as os from 'node:os';
import * as path from 'node:path';
import { describe, expect, it } from 'vitest';
import { CaptureFile } from '../src/core/captureFile';
import { FileSource } from '../src/core/fileSource';
import { detectFormat } from '../src/core/formats';
import { log4jToJson, Log4jXmlSplitter } from '../src/core/log4jXml';
import { isLoopback, LogReceiver, parseBind } from '../src/core/receivers';
import { cleanLine, isoLocal, parseSyslog, StreamFramer, syslogToLine } from '../src/core/syslog';
import { Level } from '../src/core/types';
import { queryArgs, timeQuery, validChannel, Wevtutil, WinEventLog, WinEventSplitter, winEventToJson } from '../src/core/winEvent';

const tmpDir = () => fs.mkdtempSync(path.join(os.tmpdir(), 'ulv-rcv-'));
const until = async (cond: () => boolean, ms = 3000) => {
  const end = Date.now() + ms;
  while (!cond() && Date.now() < end) await new Promise((r) => setTimeout(r, 10));
};

describe('syslog parsing', () => {
  it('parses RFC 5424 with structured data escapes and a BOM', () => {
    const m = parseSyslog('<165>1 2003-10-11T22:14:15.003Z mymachine.example.com evntslog - ID47 [exampleSDID@32473 iut="3" eventSource="Appli\\"cation\\]"][examplePriority@32473 class="high"] ﻿An application event');
    expect(m).toMatchObject({ rfc: 5424, pri: 165, time: '2003-10-11T22:14:15.003Z', host: 'mymachine.example.com', app: 'evntslog', pid: '', msgid: 'ID47', message: 'An application event' });
    expect(m.params.map(([k, v]) => `${k}=${v}`)).toEqual(['exampleSDID@32473.iut=3', 'exampleSDID@32473.eventSource=Appli"cation]', 'examplePriority@32473.class=high']);
    expect(m.structuredData.startsWith('[exampleSDID@32473 ')).toBe(true);
    expect(parseSyslog('<34>1 - - - - - -')).toMatchObject({ rfc: 5424, time: '', host: '', message: '' });
    expect(parseSyslog('<14>1 2024-05-01T10:00:00Z h app 1 - [broken message')).toMatchObject({ rfc: 5424, structuredData: '', message: '[broken message' });
  });

  it('parses RFC 3164 and what devices really send', () => {
    expect(parseSyslog("<34>Oct 11 22:14:15 mymachine su[230]: 'su root' failed for lonvick on /dev/pts/8")).toMatchObject({
      rfc: 3164, pri: 34, time: 'Oct 11 22:14:15', host: 'mymachine', app: 'su', pid: '230', message: "'su root' failed for lonvick on /dev/pts/8",
    });
    expect(parseSyslog('<13>Feb  5 17:32:18 sshd: Accepted publickey')).toMatchObject({ host: '', app: 'sshd', message: 'Accepted publickey' });
    expect(parseSyslog('<13>Feb  5 17:32:18 10.0.0.9 Use the BFG!')).toMatchObject({ host: '10.0.0.9', app: '', message: 'Use the BFG!' });
    expect(parseSyslog('<30>2024-05-01T10:00:00.123+02:00 web nginx[77]: started')).toMatchObject({ rfc: 3164, time: '2024-05-01T10:00:00.123+02:00', host: 'web', app: 'nginx' });
    expect(parseSyslog('<189>123: *Mar  1 00:00:00.000: %SYS-5-CONFIG_I: Configured')).toMatchObject({ rfc: 0, pri: 189, message: '123: *Mar  1 00:00:00.000: %SYS-5-CONFIG_I: Configured' });
    expect(parseSyslog('<192>Oct 11 22:14:15 h a: x').pri).toBe(-1);
    expect(parseSyslog('<013>x').pri).toBe(-1);
    expect(parseSyslog('no pri at all')).toMatchObject({ pri: -1, message: 'no pri at all' });
    for (const junk of ['', '<', '<1', '<>', '<1>', '<1>1', '<1>1 ', '<1>1 a b c d e [x', '<1>1 a b c d e [x y="', '\0\0\0']) parseSyslog(junk);
  });

  it('normalises every message to one RFC 5424 line', () => {
    const at = new Date(Date.UTC(2024, 4, 1, 12));
    expect(syslogToLine('<165>1 2003-10-11T22:14:15.003Z mymachine evntslog - ID47 [a@1 b="c"] hi', at, '10.0.0.1')).toBe('<165>1 2003-10-11T22:14:15.003Z mymachine evntslog - ID47 [a@1 b="c"] hi');
    const bsd = syslogToLine('<34>Oct 11 22:14:15 mymachine su[230]: failed\n', at, '10.0.0.1');
    expect(bsd).toMatch(/^<34>1 2023-10-11T22:14:15\.000[+-]\d{2}:\d{2} mymachine su 230 - - failed $/); // October is in the future in May: last year
    expect(syslogToLine('just text\twith a tab\0and a NUL', at, '10.0.0.1')).toBe(`<13>1 ${isoLocal(at)} 10.0.0.1 - - - - just text\twith a taband a NUL`);
    expect(syslogToLine('<14>hello', at, '10.0.0.2')).toMatch(/^<14>1 \S+ 10\.0\.0\.2 - - - - hello$/);
    const f = detectFormat([bsd]);
    expect(f.id).toBe('syslog5424');
    expect(f.parse(bsd)).toMatchObject({ host: 'mymachine', app: 'su', pid: '230', level: 'FATAL', facility: 'auth', severity: 'crit' });
  });

  it('cleans control characters but keeps tabs', () => {
    expect(cleanLine('a\r\nb\tc\u0007\u001b\u009b\nd')).toBe('a b\tc d');
    expect(cleanLine('plain')).toBe('plain');
  });

  it('detects syslog with a PRI and shows facility, severity and structured data', () => {
    const f = detectFormat(['<38>Oct  3 04:05:06 myhost sshd[123]: Accepted password', '<35>Oct  3 04:05:07 myhost sshd[123]: Failed password'], { year: 2024 });
    expect(f.id).toBe('syslog');
    expect(f.quick('<35>Oct  3 04:05:07 myhost sshd[123]: Failed password').level).toBe(Level.Error);
    expect(f.parse('<35>Oct  3 04:05:07 myhost sshd[123]: Failed password')).toMatchObject({ host: 'myhost', app: 'sshd', facility: 'auth', severity: 'err', message: 'Failed password' });
    const line = '<165>1 2003-10-11T22:14:15.003Z mymachine evntslog - ID47 [exampleSDID@32473 iut="3"] An application event';
    expect(detectFormat([line]).parse(line)).toMatchObject({ facility: 'local4', severity: 'notice', 'exampleSDID@32473.iut': '3', message: 'An application event', msgid: 'ID47' });
  });
});

describe('TCP framing', () => {
  const frame = (syslog: boolean, max: number, ...chunks: Buffer[]) => {
    const f = new StreamFramer(syslog, max);
    const out: string[] = [];
    for (const c of chunks) f.feed(c, (m) => out.push(m));
    f.end((m) => out.push(m));
    return out;
  };

  it('splits octet counting and newline frames, even byte by byte', () => {
    const stream = Buffer.from('18 <13>1 - - - - - hi12 <13>oct\nmore<14>newline framed\r\n<15>another\n\n');
    const expected = ['<13>1 - - - - - hi', '<13>oct\nmore', '<14>newline framed', '<15>another'];
    expect(frame(true, 1024, stream)).toEqual(expected);
    expect(frame(true, 1024, ...[...stream].map((b) => Buffer.from([b])))).toEqual(expected);
  });

  it('cuts long messages and recovers from bad counts', () => {
    const f = new StreamFramer(true, 256);
    const out: string[] = [];
    f.feed(Buffer.from('300 ' + 'a'.repeat(300) + '5 <1>ok'), (m) => out.push(m));
    expect(out).toEqual(['a'.repeat(256), '<1>ok']);
    expect(f.truncated).toBe(1);
    expect(frame(true, 256, Buffer.from('12abc\n<1>x\n'))).toEqual(['12abc', '<1>x']);
    expect(frame(true, 256, Buffer.from('0123456789x\n'))).toEqual(['0123456789x']);
    expect(frame(false, 256, Buffer.from('b'.repeat(600) + '\nshort\n  indented\n'))).toEqual(['b'.repeat(256), 'short', '  indented']);
    expect(frame(false, 256, Buffer.from('unterminated'))).toEqual(['unterminated']);
  });

  it('grows its buffer only as far as messages need, up to the maximum', () => {
    const big = 'c'.repeat(10_000);
    expect(frame(false, 20_000, Buffer.from(big + '\nd\n'))).toEqual([big, 'd']);
    expect(frame(true, 20_000, Buffer.from(`10000 ${big}`))).toEqual([big]);
    expect(frame(false, 6000, Buffer.from(big + '\n'))).toEqual(['c'.repeat(6000)]);
    const f = new StreamFramer(false, 16 << 20);
    expect((f as unknown as { buf: Buffer }).buf.length).toBeLessThanOrEqual(4096);
  });
});

const LOG4J =
  '<log4j:event logger="com.acme.Db" timestamp="1714557600123" level="ERROR" thread="worker-1">' +
  '<log4j:message><![CDATA[Query failed: <timeout>]]></log4j:message>' +
  '<log4j:NDC><![CDATA[req-7]]></log4j:NDC>' +
  '<log4j:throwable><![CDATA[java.sql.SQLException: timeout\n\tat com.acme.Db.query(Db.java:42)\n]]></log4j:throwable>' +
  '<log4j:locationInfo class="com.acme.Db" method="query" file="Db.java" line="42"/>' +
  '<log4j:properties><log4j:data name="user" value="bob"/><log4j:data name="tenant" value="t1"/></log4j:properties>' +
  '</log4j:event>\r\n';
const LOG4NET =
  '<log4net:event logger="Shop.Checkout" timestamp="2024-05-01T10:00:01.5+02:00" level="WARN" thread="7" domain="Shop.exe" username="CORP\\bob">' +
  '<log4net:message>Slow payment &amp; retry</log4net:message>' +
  '<log4net:properties><log4net:data name="log4net:HostName" value="web1" /></log4net:properties>' +
  '<log4net:exception>System.TimeoutException: late</log4net:exception></log4net:event>';

describe('log4j XML', () => {
  it('converts log4j and log4net events to JSON lines', () => {
    const j = JSON.parse(log4jToJson(LOG4J.trim())!);
    expect(j).toMatchObject({ level: 'ERROR', logger: 'com.acme.Db', thread: 'worker-1', message: 'Query failed: <timeout>', ndc: 'req-7', method: 'query', line: '42', properties: { user: 'bob' } });
    expect(Date.parse(j.time)).toBe(1714557600123);
    expect(j.exception).toBe('java.sql.SQLException: timeout\n\tat com.acme.Db.query(Db.java:42)');
    const n = JSON.parse(log4jToJson(LOG4NET)!);
    expect(n).toMatchObject({ level: 'WARN', message: 'Slow payment & retry', exception: 'System.TimeoutException: late', domain: 'Shop.exe', properties: { 'log4net:HostName': 'web1' }, time: '2024-05-01T10:00:01.5+02:00' });
    const line = log4jToJson(LOG4J.trim())!;
    const f = detectFormat([line]);
    expect(f.id).toBe('json');
    expect(f.quick(line).level).toBe(Level.Error);
    expect(f.parse(line)['properties.user']).toBe('bob');
    expect(log4jToJson('<log4j:event level="INFO"><log4j:message>unclosed</log4j:event>')).toBeNull();
    expect(log4jToJson('<!DOCTYPE x [<!ENTITY e SYSTEM "file:///etc/passwd">]><log4j:event>&e;</log4j:event>')).toBeNull();
  });

  it('splits a stream of events across chunks', () => {
    const all = LOG4J + '  \n' + LOG4NET + LOG4J;
    const whole = new Log4jXmlSplitter(1 << 20).feed(all);
    expect(whole).toHaveLength(3);
    const split = new Log4jXmlSplitter(1 << 20);
    const parts: string[] = [];
    for (let i = 0; i < all.length; i += 7) parts.push(...split.feed(all.slice(i, i + 7)));
    expect(parts).toEqual(whole);
    const small = new Log4jXmlSplitter(600);
    expect(small.feed('<log4j:event level="INFO">' + 'x'.repeat(700) + LOG4NET)).toHaveLength(1);
    expect(small.truncated).toBe(1);
  });
});

const FIXTURE = fs.readFileSync(path.join(__dirname, 'fixtures', 'winevents.xml'), 'utf8');

describe('Windows event log', () => {
  const convert = (xml: string) => new WinEventSplitter().feed(xml).map((e) => JSON.parse(winEventToJson(e)!));

  it('converts event XML to JSON lines', () => {
    const ev = convert(FIXTURE);
    expect(ev).toHaveLength(5);
    expect(ev[0]).toMatchObject({ time: '2024-05-01T10:00:00.1234567Z', level: 'Error', provider: 'Application Error', eventId: '1000', channel: 'Application', computer: 'WS-042.corp.example', recordId: '48211', task: 'Application Crashing Events' });
    expect(ev[0].message.startsWith('Faulting application name: shop.exe')).toBe(true);
    expect(ev[0].data).toMatchObject({ Data1: 'shop.exe', Data5: '0xe0434352' });
    expect(ev[1]).toMatchObject({ level: 'Information', data: { param1: 'Windows Update', Binary: '770075006100750073007600' } });
    expect(ev[2]).toMatchObject({ level: 'Warning', keywords: 'Audit Failure', task: 'Logon', data: { TargetUserName: 'administrator' } });
    expect(ev[3]).toMatchObject({ message: '', user: 'S-1-5-21-1004336348-1177238915-682003330-512', data: { SubjectUserName: 'jdoe', Channel: 'Application' } });
    expect(ev[4]).toMatchObject({ level: 'Verbose', channel: 'Microsoft-Windows-PowerShell/Operational', data: { ScriptBlockText: 'if ($a -lt 3 && $b) { Write-Host "<ok>" }' } });
    for (const e of new WinEventSplitter().feed(FIXTURE)) expect(winEventToJson(e)).not.toContain('\n');
  });

  it('splits across chunks and skips broken events', () => {
    const sp = new WinEventSplitter();
    const got: string[] = [];
    for (let i = 0; i < FIXTURE.length; i += 100) got.push(...sp.feed(FIXTURE.slice(i, i + 100)));
    expect(got).toEqual(new WinEventSplitter().feed(FIXTURE));
    expect(winEventToJson('<Event><System><Level>2</System></Event>')).toBeNull();
    expect(winEventToJson('<NotAnEvent/>')).toBeNull();
  });

  it('reads the export with the Windows event format', () => {
    const lines = new WinEventSplitter().feed(FIXTURE).map((e) => winEventToJson(e)!);
    const f = detectFormat(lines);
    expect(f.id).toBe('winevent');
    expect(lines.map((l) => f.quick(l).level)).toEqual([Level.Error, Level.Info, Level.Warn, Level.Info, Level.Trace]);
    expect(f.quick(lines[0]).time).toBe(Date.parse('2024-05-01T10:00:00.123Z'));
    const p = f.parse(lines[3]);
    expect(p).toMatchObject({ provider: 'Microsoft-Windows-Eventlog', eventId: '104', 'data.SubjectUserName': 'jdoe' });
    expect(p.message).toContain('SubjectUserName=jdoe');
  });

  const fake = (output: (n: number) => string, code = 0) => {
    const calls: string[][] = [];
    const w: Wevtutil = {
      async run(args, onOutput, signal) {
        if (signal.aborted) throw new Error('aborted');
        calls.push(args);
        const text = output(calls.length);
        for (let i = 0; i < text.length; i += 50) onOutput(text.slice(i, i + 50));
        return { code, error: code ? 'Access is denied.' : '' };
      },
    };
    return { w, calls };
  };

  it('exports with an argument array and follows by record id', async () => {
    const events = new WinEventSplitter().feed(FIXTURE);
    const { w, calls } = fake((n) => (n === 1 ? events[0] + events[1] : n === 2 ? events[3] : ''));
    const log = new WinEventLog(w, 'System', Date.parse('2024-05-01T09:00:00Z'), null, true, tmpDir(), 1024 ** 3, 20);
    expect(await log.firstExport).toBeNull();
    expect(calls[0]).toEqual(['qe', 'System', '/f:RenderedXml', '/rd:false', "/q:*[System[TimeCreated[@SystemTime>='2024-05-01T09:00:00.000Z']]]"]);
    await until(() => calls.length >= 3);
    expect(calls[1][4]).toBe('/q:*[System[EventRecordID>91702]]');
    expect(calls[2][4]).toBe('/q:*[System[EventRecordID>91703]]');
    await log.capture.flush();
    const lines = fs.readFileSync(log.path, 'utf8').trim().split('\n');
    expect(lines).toHaveLength(3);
    expect(lines[2]).toContain('"eventId":"104"');
    expect(log.status).toContain('following new events');
    log.stop();
    expect(log.running).toBe(false);
  });

  it('reports errors and rejects channels that look like options', async () => {
    const { w, calls } = fake(() => '', 5);
    const log = new WinEventLog(w, 'Security', null, null, true, tmpDir());
    expect(await log.firstExport).toBe('Access is denied.');
    expect(log.running).toBe(false);
    expect(calls[0]).toEqual(['qe', 'Security', '/f:RenderedXml', '/rd:false', '/q:*']);
    expect(validChannel('Microsoft-Windows-PowerShell/Operational')).toBe(true);
    expect(validChannel('Windows PowerShell')).toBe(true);
    for (const bad of ['/f:text', '-x', 'a"b']) expect(validChannel(bad)).toBe(false);
    expect(() => new WinEventLog(w, '/q:*', null, null, false, tmpDir())).toThrow();
    expect(timeQuery(1000, 2000)).toBe("*[System[TimeCreated[@SystemTime>='1970-01-01T00:00:01.000Z' and @SystemTime<='1970-01-01T00:00:02.000Z']]]");
    expect(queryArgs('Setup', '*')).toEqual(['qe', 'Setup', '/f:RenderedXml', '/rd:false', '/q:*']);
  });
});

describe('receivers', () => {
  const entries = async (r: LogReceiver, expected: number, format: string) => {
    await until(() => r.messages >= expected);
    await r.capture.flush();
    const s = new FileSource(0, r.path, { format });
    await s.open();
    expect(s.info().formatId).toBe(format);
    const out = s.readEntries(0, s.entryCount);
    s.close();
    return out;
  };

  const portFree = (port: number, udp = false) =>
    new Promise<boolean>((resolve) => {
      if (udp) {
        const s = dgram.createSocket('udp4');
        s.once('error', () => resolve(false));
        s.bind(port, '127.0.0.1', () => s.close(() => resolve(true)));
      } else {
        const s = net.createServer();
        s.once('error', () => resolve(false));
        s.listen(port, '127.0.0.1', () => s.close(() => resolve(true)));
      }
    });

  it('receives syslog over UDP and frees the port', async () => {
    const r = await LogReceiver.start({ protocol: 'syslog-udp', port: 0, maxMessage: 256 }, tmpDir());
    expect(r.endpoint).toMatch(/^udp:\/\/127\.0\.0\.1:\d+$/);
    expect(await portFree(r.port, true)).toBe(false);
    const c = dgram.createSocket('udp4');
    const send = (b: Buffer | string) => new Promise<void>((res) => c.send(b, r.port, '127.0.0.1', () => res()));
    await send("<34>Oct 11 22:14:15 mymachine su[230]: 'su root' failed\n");
    await send('<165>1 2024-05-01T10:00:00.000Z web app 1 ID1 [x@1 k="v"] five-four-two-four');
    await send('no header at all\u0007');
    await send('<14>' + 'z'.repeat(1000));
    await send(Buffer.from([0xff, 0xfe, 0, 1, 2]));
    c.close();
    const e = await entries(r, 5, 'syslog5424');
    expect(e).toHaveLength(5);
    expect(e[0]).toContain("mymachine su 230 - - 'su root' failed");
    expect(e[1].endsWith('web app 1 ID1 [x@1 k="v"] five-four-two-four')).toBe(true);
    expect(e[2]).toMatch(/^<13>1 \S+ 127\.0\.0\.1 - - - - no header at all$/);
    expect(e[3].length).toBeLessThan(400);
    expect(r.truncated).toBe(1);
    expect(r.status).toMatch(/^Listening on udp:\/\/127\.0\.0\.1:\d+\n5 messages · 1 sender · 1 cut to 256 bytes$/);
    const port = r.port;
    await r.stop();
    expect(await portFree(port, true)).toBe(true);
    const again = await LogReceiver.start({ protocol: 'syslog-udp', port }, tmpDir());
    expect(again.port).toBe(port);
    await again.stop();
  });

  it('receives syslog over TCP with both framings and rebinds after stop', async () => {
    const r = await LogReceiver.start({ protocol: 'syslog-tcp', port: 0 }, tmpDir());
    const framed = '<165>1 2024-05-01T10:00:00.000Z h1 app - - - octet counted';
    const c = net.connect(r.port, '127.0.0.1');
    await new Promise((res) => c.once('connect', res));
    c.write(`${Buffer.byteLength(framed)} ${framed}<13>Oct 11 22:14:15 h2 cron[9]: newline framed\n`);
    await until(() => r.clientCount === 1);
    expect(r.clientCount).toBe(1);
    c.end();
    const e = await entries(r, 2, 'syslog5424');
    expect(e).toHaveLength(2);
    expect(e[0].endsWith('h1 app - - - octet counted')).toBe(true);
    expect(e[1]).toContain(' h2 cron 9 - - newline framed');
    const port = r.port;
    // a client still connected when the tab closes is disconnected, and the port can be bound again at once
    const hanging = net.connect(port, '127.0.0.1');
    hanging.on('error', () => {});
    await new Promise((res) => hanging.once('connect', res));
    await r.stop();
    expect(r.running).toBe(false);
    const again = await LogReceiver.start({ protocol: 'syslog-tcp', port }, tmpDir());
    expect(again.port).toBe(port);
    await again.stop();
    hanging.destroy();
  });

  it('receives text lines and log4j XML over TCP', async () => {
    const lines = await LogReceiver.start({ protocol: 'tcp-lines', port: 0 }, tmpDir());
    const c = net.connect(lines.port, '127.0.0.1');
    c.end('{"@t":"2024-05-01T10:00:00Z","@l":"Warning","@m":"from serilog"}\r\n\n{"@t":"2024-05-01T10:00:01Z","@mt":"second"}\n');
    const e = await entries(lines, 2, 'json');
    expect(e).toHaveLength(2);
    expect(e[0]).toContain('from serilog');
    await lines.stop();

    const xml = await LogReceiver.start({ protocol: 'log4j-xml', port: 0 }, tmpDir());
    const x = net.connect(xml.port, '127.0.0.1');
    const ev = '<log4j:event logger="a.B" timestamp="1714557600000" level="ERROR" thread="main"><log4j:message>boom</log4j:message></log4j:event>';
    const bytes = Buffer.from(ev + ev.replace('boom', 'again'));
    x.write(bytes.subarray(0, 50));
    await new Promise((res) => setTimeout(res, 30));
    x.end(bytes.subarray(50));
    const got = await entries(xml, 2, 'json');
    expect(got).toHaveLength(2);
    expect(JSON.parse(got[1]).message).toBe('again');
    await xml.stop();
  });

  it('refuses bad binds and taken ports', async () => {
    expect(isLoopback('127.0.0.1')).toBe(true);
    expect(isLoopback('::1')).toBe(true);
    expect(isLoopback('localhost')).toBe(true);
    expect(isLoopback('0.0.0.0')).toBe(false);
    expect(isLoopback('example.com')).toBe(false);
    expect(parseBind('example.com')).toBeNull();
    await expect(LogReceiver.start({ protocol: 'tcp-lines', bind: 'example.com' }, tmpDir())).rejects.toThrow(/Not an IP address/);
    const a = await LogReceiver.start({ protocol: 'tcp-lines', port: 0 }, tmpDir());
    await expect(LogReceiver.start({ protocol: 'tcp-lines', port: a.port }, tmpDir())).rejects.toMatchObject({ code: 'EADDRINUSE' });
    await a.stop();
  });

  it('keeps capture files owner-only and rotates them at the cap', async () => {
    const dir = path.join(tmpDir(), 'receivers');
    const c = CaptureFile.create(dir, 't', 64 * 1024);
    if (process.platform !== 'win32') {
      expect(fs.statSync(dir).mode & 0o777).toBe(0o700);
      expect(fs.statSync(c.path).mode & 0o777).toBe(0o600);
    }
    for (let i = 0; i < 100; i++) c.writeLine('x'.repeat(999));
    await c.flush();
    expect(c.rotations).toBe(1);
    const n = (p: string) => fs.readFileSync(p, 'utf8').trim().split('\n').length;
    expect(n(c.path) + n(c.rotatedPath)).toBe(100);
    expect(fs.statSync(c.path).size).toBeLessThanOrEqual(64 * 1024);
    if (process.platform !== 'win32') expect(fs.statSync(c.path).mode & 0o777).toBe(0o600);
    await c.close();
  });
});
