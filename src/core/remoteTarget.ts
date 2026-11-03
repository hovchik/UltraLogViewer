// Remote files over ssh, the part without Node (the renderer uses it too): parsing and checking targets, and quoting
// for the POSIX shell that sshd runs remote commands with. Sessions are in remote.ts.

/** A file on another machine: ssh://[user@]host[:port]/path or the scp form [user@]host:/path. */
export interface RemoteTarget {
  user: string | null;
  host: string;
  port: number | null;
  path: string;
}

const HOST = /^[A-Za-z0-9_][A-Za-z0-9_.-]*$/;
const USER = /^[A-Za-z0-9_][A-Za-z0-9_.+-]*$/;
const IPV6 = /^\[[0-9A-Fa-f:.%]+\]$/;
const SCP = /^(?:([^@\s/]+)@)?([A-Za-z0-9_][A-Za-z0-9_.-]*|\[[0-9A-Fa-f:.%]+\]):(\/.*)$/s;

/** True for text meant as a remote file (it may still be invalid: see parseRemote). */
export function looksRemote(s: string | null | undefined): boolean {
  if (!s) return false;
  if (/^ssh:\/\//i.test(s)) return true;
  const m = SCP.exec(s);
  // "C:/logs/app.log" is a Windows path, not host "C".
  return !!m && (m[1] !== undefined || m[2].length > 1);
}

/** Parses and checks a remote file; throws an Error whose message says what is wrong. */
export function parseRemote(text: string): RemoteTarget {
  const s = text.trim();
  let t: RemoteTarget;
  if (/^ssh:\/\//i.test(s)) {
    const rest = s.slice(6);
    const slash = rest.indexOf('/');
    if (slash < 0) throw new Error('Add the path of the file after the host, e.g. ssh://host/var/log/app.log');
    const auth = rest.slice(0, slash);
    let path: string;
    try {
      path = decodeURIComponent(rest.slice(slash));
    } catch {
      throw new Error('The path has a bad %-escape');
    }
    const at = auth.lastIndexOf('@');
    const user = at >= 0 ? decodeURIComponent(auth.slice(0, at)) : null;
    let hp = at >= 0 ? auth.slice(at + 1) : auth;
    let port: number | null = null;
    const colon = hp.lastIndexOf(':');
    if (colon > hp.lastIndexOf(']')) {
      const p = hp.slice(colon + 1);
      if (!/^\d{1,5}$/.test(p) || +p < 1 || +p > 65535) throw new Error(`Bad port "${p}"`);
      port = +p;
      hp = hp.slice(0, colon);
    }
    t = { user, host: hp, port, path };
  } else {
    const m = SCP.exec(s);
    if (!m) throw new Error('Write a remote file as ssh://user@host/path or user@host:/path');
    t = { user: m[1] ?? null, host: m[2], port: null, path: m[3] };
  }
  validateRemote(t);
  return t;
}

/** The target, or null when the text is not a (valid) remote file. */
export function tryParseRemote(s: string | null | undefined): RemoteTarget | null {
  if (!s || !looksRemote(s)) return null;
  try {
    return parseRemote(s);
  } catch {
    return null;
  }
}

/** Rejects what could be read as an ssh option or break the remote command. */
export function validateRemote(t: RemoteTarget) {
  if (!HOST.test(t.host) && !IPV6.test(t.host)) throw new Error(`Bad host name "${t.host}"`);
  // Plain names only: older OpenSSH clients pass the user to a shell unquoted (%r in ProxyCommand, Match exec).
  if (t.user !== null && !USER.test(t.user)) throw new Error(`Bad user name "${t.user}"`);
  if (t.port !== null && !(Number.isInteger(t.port) && t.port >= 1 && t.port <= 65535)) throw new Error(`Bad port ${t.port}`);
  if (!t.path.startsWith('/')) throw new Error('The remote path must be absolute (start with /)');
  if (t.path.length > 4096) throw new Error('The remote path is too long');
  checkShellWord(t.path);
}

/** The ssh destination: [user@]host (an IPv6 address without its brackets). */
export const destination = (t: RemoteTarget) => (t.user === null ? '' : `${t.user}@`) + t.host.replace(/^\[|\]$/g, '');

/** "host:path", the name shown for the file. */
export const remoteDisplay = (t: RemoteTarget) => `${t.host}:${t.path}`;

/** The canonical ssh:// URL (kept in recent files and workspaces); only what the parser needs is escaped. */
export const remoteUrl = (t: RemoteTarget) =>
  `ssh://${t.user === null ? '' : `${encodeURIComponent(t.user)}@`}${t.host}${t.port === null ? '' : `:${t.port}`}${t.path.replace(/%/g, '%25').replace(/ /g, '%20')}`;

/** "web1, ops@web2:2222 web3" with one path: a target per host (user and port optional). Throws on a bad host. */
export function forHosts(hosts: string, path: string): RemoteTarget[] {
  const out: RemoteTarget[] = [];
  for (const h of hosts.split(/[,\s;]+/).filter(Boolean)) {
    const at = h.lastIndexOf('@');
    const user = at >= 0 ? h.slice(0, at) : null;
    let hp = at >= 0 ? h.slice(at + 1) : h;
    let port: number | null = null;
    const colon = hp.lastIndexOf(':');
    if (colon > hp.lastIndexOf(']')) {
      const p = hp.slice(colon + 1);
      if (!/^\d+$/.test(p)) throw new Error(`Bad port in "${h}"`);
      port = +p;
      hp = hp.slice(0, colon);
    }
    const t = { user, host: hp, port, path };
    validateRemote(t);
    if (!out.some((o) => remoteUrl(o) === remoteUrl(t))) out.push(t);
  }
  if (!out.length) throw new Error('Enter at least one host');
  return out;
}

/** A folder and an entry name ('..': the parent folder). */
export function joinRemote(dir: string, name: string): string {
  if (name === '..') {
    const t = dir.replace(/\/+$/, '');
    const i = t.lastIndexOf('/');
    return i <= 0 ? '/' : t.slice(0, i);
  }
  return (dir.endsWith('/') ? dir : `${dir}/`) + name.replace(/\/+$/, '');
}

// ------------------------------------------------------------------- quoting

/** Control characters (a newline would end the command) are refused, never escaped. */
export function checkShellWord(s: string) {
  if (/[\x00-\x1f\x7f]/.test(s)) throw new Error('The remote path has a control character (such as a newline)');
}

/** Single-quotes a word: inside '…' nothing is special except the quote itself, written as '\''. */
export function shellQuote(s: string): string {
  checkShellWord(s);
  return `'${s.replace(/'/g, `'\\''`)}'`;
}

export const hasGlob = (s: string) => /[*?]/.test(s);

/** Quotes everything except * and ?, so the remote shell expands only those. */
export function shellQuoteGlob(s: string): string {
  checkShellWord(s);
  return s
    .split(/([*?])/)
    .map((part) => (part === '*' || part === '?' ? part : part ? shellQuote(part) : ''))
    .join('');
}

/** Size in bytes: GNU stat, BSD stat, then wc. */
export function sizeCommand(path: string) {
  const q = shellQuote(path);
  return `stat -L -c %s -- ${q} 2>/dev/null || stat -L -f %z -- ${q} 2>/dev/null || wc -c < ${q}`;
}
/** The file from byte `offset` on, then every byte appended, across rotation (-F). */
export const tailCommand = (path: string, offset: number) => `tail -c +${offset + 1} -F -- ${shellQuote(path)}`;
/** The files a pattern such as /var/log/app/*.log matches, one per line. */
export const globCommand = (pattern: string) => `ls -1d -- ${shellQuoteGlob(pattern)}`;
/** A folder's entries, one per line, folders ending in "/". */
export const listCommand = (dir: string) => `ls -1Ap -- ${shellQuote(dir)}`;

/**
 * The arguments for one remote command. BatchMode: never prompt (there is no terminal), fail instead; keepalives so a
 * dead connection is noticed within a minute; "--" so nothing after it is read as an option.
 */
export function sshArgs(t: RemoteTarget, command: string): string[] {
  validateRemote(t);
  return [
    '-n',
    '-T',
    '-o',
    'BatchMode=yes',
    '-o',
    'ConnectTimeout=10',
    '-o',
    'ServerAliveInterval=15',
    '-o',
    'ServerAliveCountMax=3',
    ...(t.port !== null ? ['-p', String(t.port)] : []),
    '--',
    destination(t),
    command,
  ];
}

/** A message for a failed command: what went wrong, and what to do about a login that needs a person. */
export function explainSsh(t: RemoteTarget, exitCode: number, stderr: string): string {
  const text = stderr
    .split('\n')
    .map((l) => l.trim())
    .filter((l) => l && !l.startsWith('Warning: Permanently added'))
    .join(' ');
  if (/Permission denied|passphrase|Too many authentication failures/i.test(text))
    return `${t.host}: ssh needs a password or passphrase, which it cannot ask for here. Load your key into ssh-agent (ssh-add) or set up key login for ${destination(t)}, then try again.`;
  if (/Host key verification failed/i.test(text) || text.includes('REMOTE HOST IDENTIFICATION HAS CHANGED'))
    return `${t.host}: the host key is unknown or has changed. Connect once in a terminal (ssh ${destination(t)}) to check and accept it.`;
  if (!text) return exitCode === 255 ? `${t.host}: the connection failed` : `${t.host}: the command failed (exit code ${exitCode})`;
  return `${t.host}: ${text.length > 300 ? `${text.slice(0, 300)}…` : text}`;
}

/** GNU and busybox tail -F: the followed name now has another (or a shorter) file. */
export const isRotation = (line: string) => line.startsWith('tail:') && /truncated|has been replaced|has appeared/.test(line);

export type RemoteState = 'connecting' | 'copying' | 'live' | 'reconnecting' | 'failed' | 'closed';

/** What the Sources list shows for a remote file. */
export interface RemoteStatus {
  url: string;
  display: string;
  state: RemoteState;
  error: string;
  received: number;
  expected: number;
  attempt: number;
}

/** "remote · live", "remote · copying 45%", "remote · reconnecting (3): …". */
export function remoteStatusText(s: RemoteStatus): string {
  const err = s.error ? `: ${s.error}` : '';
  const what =
    s.state === 'copying'
      ? s.expected > 0
        ? `copying ${Math.min(100, Math.floor((s.received * 100) / s.expected))}%`
        : 'copying'
      : s.state === 'reconnecting'
        ? `reconnecting (${s.attempt})${err}`
        : s.state === 'failed'
          ? `failed${err}`
          : s.state;
  return `remote · ${what}`;
}
