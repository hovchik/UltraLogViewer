// Remote files over ssh: parsing, quoting and arguments, and sessions run against test/fixtures/fake-ssh (a stand-in
// for OpenSSH that serves $FAKE_SSH_ROOT/<host> and can drop connections), which the .NET tests share.

import { execFileSync } from 'node:child_process';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { displayName } from '../src/core/archives';
import { LogDocument } from '../src/core/document';
import { closeAllRemote, expandRemote, findRemote, listRemote, pruneRemote, RemoteFile, runningSsh, SSH_ENV, sshProgram } from '../src/core/remote';
import {
  explainSsh,
  forHosts,
  globCommand,
  isRotation,
  joinRemote,
  looksRemote,
  parseRemote,
  remoteDisplay,
  remoteStatusText,
  remoteUrl,
  shellQuote,
  shellQuoteGlob,
  sizeCommand,
  sshArgs,
  tailCommand,
  tryParseRemote,
} from '../src/core/remoteTarget';
import { Engine, EngineEvent } from '../src/main/engine';

const FAKE = path.resolve(__dirname, 'fixtures/fake-ssh/ssh');
const posix = process.platform !== 'win32';
const sh = (cmd: string) => execFileSync('/bin/sh', ['-c', cmd], { encoding: 'utf-8' });

describe('remote targets', () => {
  it.each([
    ['ssh://web1/var/log/app.log', null, 'web1', null, '/var/log/app.log'],
    ['ssh://deploy@web1:2222/var/log/app.log', 'deploy', 'web1', 2222, '/var/log/app.log'],
    ['ssh://web1/var/log/my%20app.log', null, 'web1', null, '/var/log/my app.log'],
    ['deploy@web1.example.com:/var/log/app.log', 'deploy', 'web1.example.com', null, '/var/log/app.log'],
    ['web1:/var/log/app.log', null, 'web1', null, '/var/log/app.log'],
    ['ssh://[::1]:22/x.log', null, '[::1]', 22, '/x.log'],
  ])('parses %s', (text, user, host, port, p) => {
    expect(looksRemote(text)).toBe(true);
    const t = parseRemote(text);
    expect(t).toEqual({ user, host, port, path: p });
    expect(parseRemote(remoteUrl(t))).toEqual(t); // the URL saved in recent files reads back the same
  });

  it('names, destinations and URLs', () => {
    const t = parseRemote('ssh://ops@web1:2200/var/log/a b.log');
    expect(remoteDisplay(t)).toBe('web1:/var/log/a b.log');
    expect(remoteUrl(t)).toBe('ssh://ops@web1:2200/var/log/a%20b.log');
    expect(sshArgs(parseRemote('ssh://[::1]/x'), 'true').slice(-2)).toEqual(['::1', 'true']);
    expect(remoteUrl(parseRemote('a1:/var/log/app/*.log'))).toBe('ssh://a1/var/log/app/*.log');
    expect(parseRemote('ssh://h/100%25.log').path).toBe('/100%.log');
  });

  it.each(['C:\\logs\\app.log', 'C:/logs/app.log', '/var/log/app.log', 'relative/app.log', 'archive.zip::member.log'])('%s is not remote', (text) => {
    expect(tryParseRemote(text)).toBeNull();
  });

  it.each([
    'ssh://-oProxyCommand=evil/x',
    'ssh://-oProxyCommand=evil@host/x',
    'ssh://host;rm/x',
    'ssh://host/var/log/a\nrm -rf ~',
    'ssh://host/var/log/a%0Arm',
    'ssh://host/a%00b',
    'ssh://host:0/x',
    'ssh://host:99999/x',
    'ssh://host:abc/x',
    'ssh://host',
    'host:relative/path',
  ])('rejects %j', (text) => {
    expect(() => parseRemote(text)).toThrow();
  });

  it('reads host lists for multi-host tail', () => {
    expect(forHosts('web1, ops@web2:2222  web3;web1', '/l/*.log').map(remoteUrl)).toEqual(['ssh://web1/l/*.log', 'ssh://ops@web2:2222/l/*.log', 'ssh://web3/l/*.log']);
    expect(() => forHosts('  ', '/x')).toThrow('at least one host');
    expect(() => forHosts('-oProxyCommand=x', '/x')).toThrow('Bad host');
    expect(() => forHosts('web1', 'x.log')).toThrow('absolute');
    expect(joinRemote('/var/log', 'app.log')).toBe('/var/log/app.log');
    expect(joinRemote('/var/log/', '..')).toBe('/var');
    expect(joinRemote('/var', '..')).toBe('/');
  });
});

describe('remote shell quoting', () => {
  it.each(['/var/log/app.log', "/var/log/it's here.log", '/tmp/a b/$(reboot)/`id`/x;y|z&w>v<u', "/tmp/'''", '/tmp/"q" \\ *?[x]', '/tmp/$HOME ${x} ~'])(
    'quotes %j so the shell sees it unchanged',
    (word) => {
      const q = shellQuote(word);
      expect(q.startsWith("'")).toBe(true);
      if (posix) expect(sh(`printf %s ${q}`)).toBe(word); // the real POSIX shell decides
    },
  );

  it.each(['/var/log/a\nb', '/var/log/a\rb', '/var/log/a\0b', '/var/log/a\x1bb', '/var/log/a\x7fb'])('refuses control characters in %j', (word) => {
    expect(() => shellQuote(word)).toThrow('control character');
  });

  it('keeps only * and ? of a glob unquoted', () => {
    expect(shellQuoteGlob('/var/log/app/*.log')).toBe("'/var/log/app/'*'.log'");
    expect(shellQuoteGlob("/l/a?b 'c'*")).toBe("'/l/a'?'b '\\''c'\\'''*");
    if (!posix) return;
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'ulv-g $x'));
    for (const n of ['a.log', 'b.log', 'c.txt']) fs.writeFileSync(path.join(dir, n), '');
    expect(sh(globCommand(`${dir}/*.log`))).toBe(`${dir}/a.log\n${dir}/b.log\n`);
  });

  it('builds ssh arguments without a shell', () => {
    const t = parseRemote("ssh://ops@web1:2200/var/log/it's.log");
    expect(sshArgs(t, tailCommand(t.path, 100))).toEqual([
      '-n', '-T', '-o', 'BatchMode=yes', '-o', 'ConnectTimeout=10', '-o', 'ServerAliveInterval=15', '-o', 'ServerAliveCountMax=3', '-p', '2200', '--', 'ops@web1',
      "tail -c +101 -F -- '/var/log/it'\\''s.log'",
    ]);
    expect(sizeCommand('/x')).toBe("stat -L -c %s -- '/x' 2>/dev/null || stat -L -f %z -- '/x' 2>/dev/null || wc -c < '/x'");
    expect(sshArgs(parseRemote('web1:/x'), 'true')).not.toContain('-p');
    expect(() => sshArgs({ ...t, host: '-oProxyCommand=x' }, 'true')).toThrow(); // checked again after parsing
  });

  it('takes the ssh program from the setting, then the environment', () => {
    const old = process.env[SSH_ENV];
    try {
      delete process.env[SSH_ENV];
      expect(sshProgram()).toBe('ssh');
      process.env[SSH_ENV] = '/opt/ssh';
      expect(sshProgram('')).toBe('/opt/ssh');
      expect(sshProgram('/usr/bin/ssh')).toBe('/usr/bin/ssh');
    } finally {
      if (old === undefined) delete process.env[SSH_ENV];
      else process.env[SSH_ENV] = old;
    }
  });

  it('explains logins that need a person, and spots rotation', () => {
    const t = parseRemote('ops@web1:/x');
    expect(explainSsh(t, 255, 'ops@web1: Permission denied (publickey,password).\n')).toContain('ssh-agent');
    expect(explainSsh(t, 255, 'Host key verification failed.\n')).toContain('host key');
    expect(explainSsh(t, 1, "stat: cannot statx '/x': No such file or directory\n")).toBe("web1: stat: cannot statx '/x': No such file or directory");
    expect(isRotation("tail: '/x' has been replaced;  following new file")).toBe(true);
    expect(isRotation('tail: /x: file truncated')).toBe(true);
    expect(isRotation("tail: '/x' has become inaccessible: No such file or directory")).toBe(false);
    expect(remoteStatusText({ url: '', display: '', state: 'copying', error: '', received: 45, expected: 100, attempt: 0 })).toBe('remote · copying 45%');
  });
});

// ------------------------------------------------------------ with the fake ssh

const until = async (cond: () => boolean | Promise<boolean>, ms = 10_000) => {
  const end = Date.now() + ms;
  while (!(await cond())) {
    if (Date.now() > end) throw new Error('condition not met in time');
    await new Promise((r) => setTimeout(r, 25));
  }
};
const messages = (d: LogDocument) => d.getRows(0, 1000).map((r) => r.fields.message);
const line = (s: number, msg: string) => `2024-05-01 10:00:${String(s).padStart(2, '0')},000 [main] INFO  a.B - ${msg}\n`;

describe.skipIf(!posix)('remote files over the fake ssh', () => {
  let root: string;
  let opts: { ssh: string; cacheDir: string; retryMs: number; maxRetryMs: number };
  const hostFile = (host: string, remote: string, text: string) => {
    const p = path.join(root, host) + remote;
    fs.mkdirSync(path.dirname(p), { recursive: true });
    fs.writeFileSync(p, text);
    return p;
  };
  const marker = (name: string) => fs.writeFileSync(path.join(root, name), '');

  beforeEach(() => {
    root = fs.mkdtempSync(path.join(os.tmpdir(), 'ulv-ssh-'));
    process.env.FAKE_SSH_ROOT = root;
    opts = { ssh: FAKE, cacheDir: path.join(root, 'cache'), retryMs: 100, maxRetryMs: 400 };
  });
  afterEach(() => {
    closeAllRemote();
    fs.rmSync(root, { recursive: true, force: true });
  });

  it('copies a remote file and follows it across a drop and a rotation', async () => {
    const file = hostFile('web1', '/var/log/app.log', line(0, 'one') + line(1, 'two'));
    const r = await RemoteFile.open(parseRemote('ops@web1:/var/log/app.log'), opts);
    expect(findRemote(r.localPath)).toBe(r);
    expect(displayName(r.localPath)).toBe('web1:/var/log/app.log');

    const doc = new LogDocument(1, 'remote');
    await doc.addSource(r.localPath, {});
    expect(messages(doc)).toEqual(['one', 'two']);
    const src = doc.info().sources[0];
    expect(src.displayName).toBe('web1:/var/log/app.log');
    expect(remoteStatusText(src.remote!)).toBe('remote · live');

    fs.appendFileSync(file, line(2, 'three'));
    await until(async () => (await doc.refresh(), doc.viewCount() === 3));

    // the connection drops: it reconnects and goes on from the bytes it has
    marker('web1.drop');
    await until(() => r.starts >= 2 && r.state === 'live');
    fs.appendFileSync(file, line(3, 'four'));
    await until(async () => (await doc.refresh(), doc.viewCount() === 4));
    expect(messages(doc)).toEqual(['one', 'two', 'three', 'four']);

    // rotation: a new, shorter file under the same name; the copy starts over
    fs.renameSync(file, `${file}.1`);
    fs.writeFileSync(file, line(9, 'fresh'));
    await until(async () => (await doc.refresh(), messages(doc).join() === 'fresh'));
    expect(fs.statSync(r.localPath).size).toBe(fs.statSync(file).size);
    doc.close();
  });

  it('reconnects with backoff while the host is down and says why', async () => {
    const file = hostFile('web2', '/l/app.log', line(0, 'first'));
    const r = await RemoteFile.open(parseRemote('ssh://web2/l/app.log'), opts);
    await until(() => r.received === fs.statSync(file).size);
    marker('web2.down');
    marker('web2.drop');
    await until(() => r.state === 'reconnecting' && r.attempt >= 2);
    expect(remoteStatusText(r.status())).toMatch(/^remote · reconnecting \(\d+\): .*Connection refused/);
    fs.rmSync(path.join(root, 'web2.down'));
    fs.appendFileSync(file, line(1, 'second'));
    await until(() => r.state === 'live' && r.received === fs.statSync(file).size);
    expect(fs.readFileSync(r.localPath, 'utf-8')).toBe(fs.readFileSync(file, 'utf-8'));
  });

  it('fails clearly when the file is missing or the login needs a password', async () => {
    hostFile('web3', '/l/app.log', 'x\n');
    await expect(RemoteFile.open(parseRemote('web3:/l/nope.log'), opts)).rejects.toThrow('No such file');
    marker('web3.auth');
    await expect(RemoteFile.open(parseRemote('web3:/l/app.log'), opts)).rejects.toThrow('ssh-agent');
    await expect(RemoteFile.open(parseRemote('nohost:/l/app.log'), opts)).rejects.toThrow('Could not resolve hostname');
    expect(runningSsh()).toBe(0);
  });

  it('merges hosts by time and expands globs on each host', async () => {
    hostFile('a1', '/var/log/app/x.log', line(0, 'a1 x first') + line(4, 'a1 x third'));
    hostFile('a1', '/var/log/app/y.log', line(5, 'a1 y fourth'));
    hostFile('a1', '/var/log/app/skip.txt', line(6, 'not a log'));
    hostFile('b2', '/var/log/app/x.log', line(2, 'b2 x second'));
    const targets = (await Promise.all(forHosts('a1 b2', '/var/log/app/*.log').map((t) => expandRemote(t, opts)))).flat();
    expect(targets.map(remoteDisplay)).toEqual(['a1:/var/log/app/x.log', 'a1:/var/log/app/y.log', 'b2:/var/log/app/x.log']);
    const sessions = await Promise.all(targets.map((t) => RemoteFile.open(t, opts)));
    const doc = new LogDocument(1, 'Merged');
    expect(await doc.addSources(sessions.map((s) => s.localPath), () => ({}))).toEqual([]);
    expect(messages(doc)).toEqual(['a1 x first', 'b2 x second', 'a1 x third', 'a1 y fourth']);
    expect(doc.getRows(0, 10).map((r) => doc.info().sources[r.source].displayName)).toEqual([
      'a1:/var/log/app/x.log',
      'b2:/var/log/app/x.log',
      'a1:/var/log/app/x.log',
      'a1:/var/log/app/y.log',
    ]);
    await expect(expandRemote(parseRemote('b2:/var/log/app/*.gz'), opts)).rejects.toThrow('No such file');
    expect(await listRemote(parseRemote('a1:/var/log'), opts)).toEqual(['app/']);
    doc.close();
  });

  it('kills the ssh processes when a document closes and when the app quits', async () => {
    hostFile('c1', '/l/a.log', line(0, 'a'));
    hostFile('c2', '/l/a.log', line(0, 'b'));
    const events: EngineEvent[] = [];
    const engine = new Engine((e) => events.push(e), path.join(root, 'index-cache'));
    try {
      const a = await engine.openRemote(['c1:/l/a.log'], { ssh: FAKE });
      const b = await engine.openRemote(['ssh://c2/l/a.log', 'ssh://nohost/l/a.log'], { ssh: FAKE });
      expect(a.paths[0].startsWith(path.join(root, 'remote-cache'))).toBe(true);
      expect(b.errors).toEqual([expect.stringContaining('nohost')]);
      const da = engine.open(a.paths);
      const db = engine.open(b.paths);
      await until(() => events.some((e) => e.type === 'doc-updated' && e.docId === db && e.reset));
      expect(engine.info(db).title).toBe('c2:/l/a.log');
      expect(runningSsh()).toBe(2);
      const pids = fs.readdirSync(path.join(root, 'pids')).map(Number);
      expect(pids).toHaveLength(2);

      engine.close(da);
      expect(fs.existsSync(a.paths[0])).toBe(false);
      await until(() => runningSsh() === 1);

      // a state change without new data is pushed to the window
      marker('c2.down');
      marker('c2.drop');
      await until(() => events.some((e) => e.type === 'doc-updated' && e.info.sources[0]?.remote?.state === 'reconnecting'));

      engine.closeRemote(); // the app quits
      await until(() => runningSsh() === 0);
      await until(() => pids.every(dead));
      void db;
    } finally {
      engine.dispose();
    }
    pruneRemote([]);
  });
});

/** Gone, or a zombie waiting to be reaped. */
function dead(pid: number) {
  try {
    process.kill(pid, 0);
  } catch {
    return true;
  }
  try {
    const stat = fs.readFileSync(`/proc/${pid}/stat`, 'utf-8');
    return stat.slice(stat.lastIndexOf(')') + 2).startsWith('Z');
  } catch {
    return true;
  }
}
