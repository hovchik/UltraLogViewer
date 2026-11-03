import { describe, expect, it } from 'vitest';
import * as fs from 'node:fs';
import * as net from 'node:net';
import * as os from 'node:os';
import * as path from 'node:path';
import { LogDocument } from '../src/core/document';
import { ApiResult, ApiTab, hostAllowed, LocalApiHost, newApiToken, startLocalApi } from '../src/core/localApi';
import {
  curlExample,
  looksLikeLog,
  MAX_FILES,
  OpenRequest,
  parseGoTo,
  requestFromCommandLine,
  requestFromJson,
  requestFromUrl,
  requestToJson,
  requestToUrl,
  sameRequest,
} from '../src/core/openRequest';
import { expandPaths } from '../src/core/openRequestFs';
import { parseCommandLine } from '../src/core/timeWindow';
import { LOG4J, tmpFile } from './helpers';

const local = (y: number, mo: number, d: number, h = 0, mi = 0, s = 0) => new Date(y, mo - 1, d, h, mi, s).getTime();
const NOW = local(2024, 5, 1, 12);
const utc = (y: number, mo: number, d: number, h = 0, mi = 0, s = 0, ms = 0) => Date.UTC(y, mo - 1, d, h, mi, s, ms);
const cli = (...args: string[]) => requestFromCommandLine(parseCommandLine(args, NOW), NOW);

describe('open request', () => {
  it('parses the new command-line switches', () => {
    const f = tmpFile('cli.log', LOG4J);
    const c = parseCommandLine([f, '--filter', 'level:error db', '--search=timeout', '--goto', '10:00:03', '--levels', 'error,warn', '--format', 'timestamped', '--since', '-1h', '--merge', '--new-tab'], NOW);
    expect(c.errors).toEqual([]);
    expect(c.files).toEqual([f]); // the switch values are not taken for files
    const r = requestFromCommandLine(c, NOW);
    expect(r.errors).toEqual([]);
    expect(r).toMatchObject({ source: 'cli', filter: 'level:error db', search: 'timeout', format: 'timestamped', merge: true, newTab: true });
    expect(r.goto).toEqual({ time: local(2024, 5, 1, 10, 0, 3) });
    expect(r.levels).toEqual([false, false, false, false, true, true, false]);
    expect(r.window?.since).toBe(NOW - 3_600_000);
    expect(cli('--levels', 'loud', '--goto', 'soon', '--filter').errors).toHaveLength(3);
    expect(cli('-').paths).toEqual(['-']);
  });

  it('parses "go to" targets', () => {
    expect(parseGoTo('1234', NOW)).toEqual({ line: 1234 });
    expect(parseGoTo('2:1234', NOW)).toEqual({ line: 1234, file: '2' });
    expect(parseGoTo('line:2:30', NOW)).toEqual({ line: 30, file: '2' }); // "2:30" alone is a time
    expect(parseGoTo('2:30', NOW)).toEqual({ time: local(2024, 5, 1, 2, 30) });
    expect(parseGoTo('C:\\logs\\app.log:12', NOW)).toEqual({ line: 12, file: 'C:\\logs\\app.log' });
    expect(parseGoTo('app.log:7', NOW)).toEqual({ line: 7, file: 'app.log' });
    expect(parseGoTo('-5m', NOW)).toEqual({ time: NOW - 300_000 });
    expect(parseGoTo('2024-05-01T10:00:03.004Z', NOW)).toEqual({ time: utc(2024, 5, 1, 10, 0, 3, 4) });
    expect(parseGoTo('0', NOW)).toBeNull();
    expect(parseGoTo('later', NOW)).toBeNull();
  });

  it('parses deep links and refuses unsafe ones', () => {
    const r = requestFromUrl(
      'ultralogviewer://open?path=%2Fvar%2Flog%2Fapp.log&path=/var/log/b.log&merge=1&since=2024-05-01T10:00:00Z&last=15m&filter=level%3Aerror+db&levels=error&goto=line:2:30&newtab=1&unknown=x',
      NOW,
    );
    expect(r.errors).toEqual([]);
    expect(r.source).toBe('url');
    expect(r.paths).toEqual(['/var/log/app.log', '/var/log/b.log']);
    expect(r.window).toEqual({ since: utc(2024, 5, 1, 10), until: null, last: null });
    expect(r.filter).toBe('level:error db');
    expect(r.goto).toEqual({ line: 30, file: '2' });
    expect(r.merge && r.newTab).toBe(true);
    expect(requestFromUrl('ULTRALOGVIEWER:open?path=C:%5Clogs%5Ca.log', NOW).errors).toEqual([]);

    // Untrusted input: links come from any web page.
    for (const bad of [
      'ultralogviewer://open?path=/var/log/a%0A.log',
      'ultralogviewer://open?path=/var/log/a%07.log',
      'ultralogviewer://open?path=-',
      'ultralogviewer://open?path=app.log',
      'ultralogviewer://open?path=%5C%5Cevil%5Cshare%5Ca.log',
      'ultralogviewer://open?path=//evil/share/a.log',
      'ultralogviewer://open?path=/%5Cevil%5Cshare%5Ca.log', // Windows reads "/\\evil\\share" as a network path too
      'ultralogviewer://open?path=%5C/evil/share/a.log',
      // never a remote file: that would run ssh
      'ultralogviewer://open?path=ssh%3A%2F%2Fweb1%2Fvar%2Flog%2Fa.log',
      'ultralogviewer://open?path=web1:/var/log/a.log',
      'ultralogviewer://open?path=ops@web1:/var/log/a.log',
      'ultralogviewer://run?cmd=calc',
      'ultralogviewer://open?path=/a.log&filter=a%0Db',
      'ultralogviewer://open?path=/a.log&format=../../x',
      'ultralogviewer://open?path=/a.log&path=%zz',
      'ultralogviewer://open?' + Array.from({ length: MAX_FILES + 1 }, (_, i) => `path=/l/${i}.log`).join('&'),
    ])
      expect(requestFromUrl(bad, NOW).errors.length, bad).toBeGreaterThan(0);
    for (const remote of ['ssh://web1/var/log/a.log', 'web1:/var/log/a.log', 'ops@web1:/var/log/*.log'])
      expect(requestFromJson(JSON.stringify({ path: remote }), NOW).errors.length, remote).toBeGreaterThan(0);

    // a link on the command line (how Windows starts the app for a clicked link) is a URL request
    const c = cli('ultralogviewer://open?path=/var/log/app.log');
    expect(c.source).toBe('url');
    expect(c.paths).toEqual(['/var/log/app.log']);
  });

  it('parses JSON', () => {
    let r = requestFromJson('{"paths":["/var/log/a.log"],"filter":"x","extra":{"x":1}}', NOW);
    expect(r.errors).toEqual(['"extra": unexpected value']);
    r = requestFromJson('{"path":"/var/log/a.log","last":"30m","search":"timeout","goto":1714557600000,"merge":false,"format":null,"levels":["error","fatal"]}', NOW);
    expect(r.errors).toEqual([]);
    expect(r).toMatchObject({ source: 'api', paths: ['/var/log/a.log'], search: 'timeout', goto: { time: 1714557600000 } });
    expect(r.window?.last).toBe(1_800_000);
    expect(r.levels).toEqual([false, false, false, false, false, true, true]);
    expect(requestFromJson('{nope', NOW).errors.length).toBe(1);
    expect(requestFromJson('[1]', NOW).errors.length).toBe(1);
    expect(requestFromJson('{"path":"/a\\u0000.log"}', NOW).errors.length).toBe(1);
    expect(requestFromJson('{"path":"relative.log"}', NOW).errors).toEqual(['A full path is needed: relative.log']);
  });

  it('round-trips links and JSON', () => {
    const r: OpenRequest = {
      source: 'cli',
      errors: [],
      paths: ['/var/log/app one.log', 'C:\\logs\\b&c.log', '/x/bundle.zip::logs/app.log'],
      merge: true,
      format: 'json',
      window: { since: utc(2024, 5, 1, 10), until: utc(2024, 5, 1, 11), last: null },
      filter: 'level:error and "a&b=c" or #tag ?x',
      search: '100% done',
      goto: { time: utc(2024, 5, 1, 10, 30, 0, 123) },
      levels: [false, false, false, false, true, true, true],
      newTab: true,
    };
    const url = requestToUrl(r);
    expect(url.startsWith('ultralogviewer://open?path=')).toBe(true);
    expect(url).not.toContain(' ');
    const back = requestFromUrl(url, NOW);
    expect(back.errors).toEqual([]);
    expect(sameRequest(r, back)).toBe(true);
    expect(sameRequest(r, requestFromJson(requestToJson(r), NOW))).toBe(true);
    const line: OpenRequest = { source: 'cli', errors: [], paths: ['/a.log'], goto: { line: 42, file: '2' }, window: { last: 5_400_000 } };
    expect(sameRequest(line, requestFromUrl(requestToUrl(line), NOW))).toBe(true);
    expect(requestToUrl(line)).toContain('last=90m');
    // the .NET app builds the same link
    expect(requestToUrl({ source: 'cli', errors: [], paths: ['/var/log/a b.log'], filter: 'level:error', goto: { time: utc(2024, 3, 1, 9, 0, 2) } })).toBe(
      'ultralogviewer://open?path=%2Fvar%2Flog%2Fa%20b.log&filter=level%3Aerror&goto=2024-03-01T09%3A00%3A02.000Z',
    );
  });

  it('tells logs from other files', () => {
    for (const p of ['/var/log/app.log', '/var/log/app.log.3', '/var/log/syslog', 'C:\\x\\a.JSONL', '/b.zip::x/y.txt', '/a.log.2024-05-01', '/x.gz']) expect(looksLikeLog(p), p).toBe(true);
    for (const p of ['/home/u/.ssh/id_rsa.pub', '/home/u/.bashrc', 'C:\\Windows\\calc.exe', '/x/report.docx', '/etc/shadow.bak']) expect(looksLikeLog(p), p).toBe(false);
  });

  it('expands globs and zip members', () => {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'ulv-glob-'));
    for (const n of ['b.log', 'a.log', 'c.txt', 'bundle.zip']) fs.writeFileSync(path.join(dir, n), 'x\n');
    const r = cli('*.log', path.join(dir, 'c.txt'), 'bundle.zip::app.log', '-', 'missing.log', '*.nope');
    expect(expandPaths(r, dir)).toEqual([path.join(dir, 'a.log'), path.join(dir, 'b.log'), path.join(dir, 'c.txt'), path.join(dir, 'bundle.zip') + '::app.log', '-']);
    expect(r.errors).toEqual(['File not found: missing.log', 'No files match *.nope']);
  });
});

// ------------------------------------------------------------------------ HTTP API

/** A host over real documents: "open" indexes the files, rows come from the view. */
class DocHost implements LocalApiHost {
  tabs_: { id: number; paths: string[]; doc: LogDocument; filter: string }[] = [];
  async open(r: OpenRequest): Promise<ApiResult> {
    const paths = expandPaths(r, '/');
    if (r.errors.length) return { status: 404, error: r.errors.join('; ') };
    const doc = new LogDocument(this.tabs_.length + 1, 't');
    await doc.addSources(paths, () => ({}));
    const id = this.tabs_.length + 1;
    this.tabs_.push({ id, paths, doc, filter: '' });
    return this.update(id, r);
  }
  async tabs(): Promise<ApiTab[]> {
    return this.tabs_.map((t) => ({ id: t.id, title: path.basename(t.paths[0]), paths: t.paths, total: t.doc.info().total, shown: t.doc.viewCount(), filter: t.filter, ready: true, active: true }));
  }
  async update(id: number, r: OpenRequest): Promise<ApiResult> {
    const t = this.tabs_.find((x) => x.id === id);
    if (!t) return { status: 404, error: 'no tab' };
    if (r.filter != null || r.levels) {
      t.filter = r.filter ?? '';
      await t.doc.applyFilter({ levels: r.levels ?? [true, true, true, true, true, true, true], rules: [], query: t.filter });
    }
    return { status: 200, tab: id };
  }
  async rows(id: number, from: number, count: number) {
    const t = this.tabs_.find((x) => x.id === id);
    return t ? t.doc.getRows(from, Math.min(count, Math.max(0, t.doc.viewCount() - from))) : null;
  }
}

const TOKEN = '0123456789abcdef0123456789abcdef';

describe('local HTTP API', () => {
  it('serves on loopback with the token only', async () => {
    const host = new DocHost();
    const api = await startLocalApi(host, TOKEN, 0, () => NOW);
    const base = `http://127.0.0.1:${api.port}`;
    const req = (p: string, init: RequestInit & { token?: string | null } = {}) =>
      fetch(base + p, { ...init, headers: { ...(init.token !== null ? { Authorization: `Bearer ${init.token ?? TOKEN}` } : {}), ...(init.headers as Record<string, string>) } });
    try {
      // auth required, wrong token, token in the query
      expect((await req('/health', { token: null })).status).toBe(401);
      expect((await req('/health', { token: 'wrong-token-wrong-token' })).status).toBe(401);
      expect((await req(`/health?token=${TOKEN}`, { token: null })).status).toBe(200);
      const health = await req('/health');
      expect(health.status).toBe(200);
      expect(health.headers.get('access-control-allow-origin')).toBeNull();
      // DNS rebinding and web pages (fetch cannot set Host: use a raw request)
      expect(await raw(api.port, `GET /tabs HTTP/1.1\r\nHost: evil.example:${api.port}\r\nAuthorization: Bearer ${TOKEN}\r\n\r\n`)).toMatch(/^HTTP\/1\.1 403/);
      expect(await raw(api.port, `GET /tabs HTTP/1.1\r\nHost: localhost:${api.port}\r\nAuthorization: Bearer ${TOKEN}\r\nConnection: close\r\n\r\n`)).toMatch(/^HTTP\/1\.1 200/);
      expect(await raw(api.port, `GET /tabs HTTP/1.1\r\nHost: 127.0.0.1\r\nHost: evil.example\r\nAuthorization: Bearer ${TOKEN}\r\n\r\n`)).toMatch(/^HTTP\/1\.1 400/);
      expect((await req('/open', { method: 'POST', body: '{"path":"/x.log"}', headers: { Origin: 'https://evil.example' } })).status).toBe(403);
      expect((await req('/health', { headers: { Origin: `http://127.0.0.1:${api.port}` } })).status).toBe(200);
      expect((await req('/open', { method: 'OPTIONS' })).status).toBe(405);
      expect((await req('/nope')).status).toBe(404);

      // open, then tabs, then rows
      const f = tmpFile('api.log', LOG4J);
      const open = await req('/open', { method: 'POST', body: JSON.stringify({ paths: [f], filter: 'level:error' }) });
      expect(open.status).toBe(200);
      expect((await open.json()).tab).toBe(1);
      const tabs = await (await req('/tabs')).json();
      expect(tabs).toHaveLength(1);
      expect(tabs[0]).toMatchObject({ paths: [f], total: 6, shown: 1, filter: 'level:error' });
      const rows = await req('/tabs/1/rows?from=0&count=10');
      expect(rows.headers.get('content-type')).toMatch(/^application\/x-ndjson/);
      const lines = (await rows.text()).split('\n').filter(Boolean).map((l) => JSON.parse(l));
      expect(lines).toHaveLength(1);
      expect(lines[0]).toMatchObject({ level: 'ERROR', line: 4 });
      expect(lines[0].text.startsWith('2024-05-01 10:00:03,004 [worker-2] ERROR com.acme.Db - Query failed')).toBe(true);

      expect((await req('/tabs/1/filter', { method: 'POST', body: '{"levels":"info,error"}' })).status).toBe(200);
      expect(host.tabs_[0].doc.viewCount()).toBe(3);
      expect((await req('/tabs/1/goto', { method: 'POST', body: '{}' })).status).toBe(400);
      expect((await req('/tabs/9/rows')).status).toBe(404);
      expect((await req('/open', { method: 'POST', body: '{nope' })).status).toBe(400);
      expect((await req('/open', { method: 'POST', body: '{"path":"/no/such.log"}' })).status).toBe(404);
    } finally {
      await api.close();
    }
  });

  it('falls back to another port and checks hosts', async () => {
    const a = await startLocalApi(new DocHost(), TOKEN, 0);
    const b = await startLocalApi(new DocHost(), TOKEN, a.port);
    try {
      expect(b.port).not.toBe(a.port); // taken: the next free one
    } finally {
      await a.close();
      await b.close();
    }
    await expect(startLocalApi(new DocHost(), 'short')).rejects.toThrow();
    expect(hostAllowed('127.0.0.1:7700')).toBe(true);
    expect(hostAllowed('LocalHost')).toBe(true);
    expect(hostAllowed('127.0.0.1.evil.example')).toBe(false);
    expect(hostAllowed('localhost.evil.example:7700')).toBe(false);
    expect(hostAllowed(undefined)).toBe(false);
    expect(newApiToken()).toHaveLength(64);
    expect(newApiToken()).not.toBe(newApiToken());
    expect(curlExample(7700, TOKEN, '/var/log/a.log', false)).toContain(`Authorization: Bearer ${TOKEN}`);
  });
});

function raw(port: number, text: string): Promise<string> {
  return new Promise((resolve, reject) => {
    const s = net.connect(port, '127.0.0.1', () => s.write(text));
    let out = '';
    s.on('data', (d) => {
      out += d.toString();
      if (/\r\n\r\n/.test(out)) s.destroy();
    });
    s.on('close', () => resolve(out));
    s.on('error', reject);
  });
}
