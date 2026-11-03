// A fake `ssh` for the integration tests of remote files (both apps use it). It takes OpenSSH's command line,
// `ssh [options] [--] [user@]host command`, and runs the remote command on local files: host H sees the folder
// $FAKE_SSH_ROOT/H as its "/". It knows the commands the apps send: stat, wc -c, cat, tail -c +N -F, ls -1d and ls -1Ap,
// joined with || and with 2>/dev/null and < redirections, and POSIX single quoting.
//
// Simulations, by marker files next to the host folders:
//   H.down   every connection fails (exit 255, "Connection refused")
//   H.auth   authentication needs a password (exit 255, "Permission denied")
//   H.drop   a running `tail -F` drops its connection once (the marker is deleted, exit 255)
// Each process writes $FAKE_SSH_ROOT/pids/<pid> while it runs (left behind when it is killed), so tests can check that
// none is still running. Every command line is appended to $FAKE_SSH_ROOT/calls.log.
import * as fs from 'node:fs';
import * as path from 'node:path';

const root = process.env.FAKE_SSH_ROOT;
if (!root) {
  process.stderr.write('fake ssh: FAKE_SSH_ROOT is not set\n');
  process.exit(255);
}

// ---------------------------------------------------------------- arguments
const withValue = new Set(['-o', '-p', '-l', '-i', '-F', '-J', '-E', '-b', '-c', '-D', '-L', '-R', '-W', '-S', '-m', '-Q', '-w', '-B', '-e', '-I', '-O']);
const argv = process.argv.slice(2);
let i = 0;
let port = '22';
for (; i < argv.length; i++) {
  const a = argv[i];
  if (a === '--') {
    i++;
    break;
  }
  if (!a.startsWith('-')) break;
  if (withValue.has(a)) {
    const v = argv[++i];
    if (a === '-p') port = v;
  }
}
const dest = argv[i++];
const command = argv.slice(i).join(' ');
fs.mkdirSync(path.join(root, 'pids'), { recursive: true });
fs.appendFileSync(path.join(root, 'calls.log'), JSON.stringify(argv) + '\n');
if (!dest || !command) {
  process.stderr.write('usage: ssh [options] destination command\n');
  process.exit(255);
}
const host = dest.includes('@') ? dest.slice(dest.lastIndexOf('@') + 1) : dest;
const hostDir = path.join(root, host);
const marker = (m) => path.join(root, `${host}.${m}`);
const pidFile = path.join(root, 'pids', String(process.pid));
fs.writeFileSync(pidFile, command);

function exit(code) {
  try {
    fs.rmSync(pidFile, { force: true });
  } catch {
    // ignore
  }
  process.exit(code);
}
const fail = (msg, code = 255) => {
  process.stderr.write(msg + '\n');
  exit(code);
};

if (!/^[A-Za-z0-9_][A-Za-z0-9_.-]*$/.test(host) || !fs.existsSync(hostDir)) fail(`ssh: Could not resolve hostname ${host}: Name or service not known`);
if (fs.existsSync(marker('down'))) fail(`ssh: connect to host ${host} port ${port}: Connection refused`);
if (fs.existsSync(marker('auth'))) fail(`${dest}: Permission denied (publickey,password).`);

// ------------------------------------------------------------ shell words
/** Splits a POSIX command into words; a word remembers whether it has an unquoted * or ? (a glob). */
function words(s) {
  const out = [];
  let cur = null;
  const push = (ch, quoted) => {
    cur ??= { text: '', glob: false };
    cur.text += ch;
    if (!quoted && (ch === '*' || ch === '?')) cur.glob = true;
  };
  for (let k = 0; k < s.length; k++) {
    const c = s[k];
    if (c === "'") {
      const end = s.indexOf("'", k + 1);
      if (end < 0) fail('sh: unterminated quote', 2);
      cur ??= { text: '', glob: false };
      for (const ch of s.slice(k + 1, end)) push(ch, true);
      k = end;
    } else if (c === '\\') {
      push(s[++k], true);
    } else if (c === ' ' || c === '\t') {
      if (cur) out.push(cur);
      cur = null;
    } else if (c === '|' && s[k + 1] === '|') {
      if (cur) out.push(cur);
      cur = null;
      out.push({ op: '||' });
      k++;
    } else if (';&|`$\n"(){}'.includes(c)) {
      // The apps never send these unquoted: a test that sees this message found an injection.
      fail(`fake ssh: unquoted shell syntax ${JSON.stringify(c)} in ${JSON.stringify(s)}`, 2);
    } else push(c, false);
  }
  if (cur) out.push(cur);
  return out;
}

const local = (p) => path.join(hostDir, p);

function globExpand(w) {
  if (!w.glob) return [w.text];
  const dir = path.posix.dirname(w.text);
  const base = path.posix.basename(w.text);
  if (/[*?]/.test(dir)) fail('fake ssh: globs only in the last path segment', 2);
  const re = new RegExp('^' + base.replace(/[.+^${}()|[\]\\]/g, '\\$&').replace(/\*/g, '.*').replace(/\?/g, '.') + '$');
  let names = [];
  try {
    names = fs.readdirSync(local(dir)).filter((n) => re.test(n)).sort();
  } catch {
    // no such folder: the pattern stays as it is, like in sh
  }
  return names.length ? names.map((n) => path.posix.join(dir, n)) : [w.text];
}

// --------------------------------------------------------------- commands
const out = (s) => process.stdout.write(s);
const err = (s) => process.stderr.write(s);
const operands = (args) => {
  const k = args.indexOf('--');
  return k >= 0 ? args.slice(k + 1) : args.filter((a) => !a.startsWith('-'));
};

function stat(args) {
  if (!args.includes('-c')) return 1; // the BSD form: not on this "Linux" host
  const f = operands(args)[0];
  try {
    out(`${fs.statSync(local(f)).size}\n`);
    return 0;
  } catch {
    err(`stat: cannot statx '${f}': No such file or directory\n`);
    return 1;
  }
}

function wc(stdinFile) {
  try {
    out(`${fs.statSync(local(stdinFile)).size}\n`);
    return 0;
  } catch {
    err(`sh: 1: cannot open ${stdinFile}: No such file\n`);
    return 2;
  }
}

function cat(args) {
  const f = operands(args)[0];
  try {
    out(fs.readFileSync(local(f)));
    return 0;
  } catch {
    err(`cat: ${f}: No such file or directory\n`);
    return 1;
  }
}

function ls(args) {
  const k = args.indexOf('--');
  const opts = (k >= 0 ? args.slice(0, k) : args.filter((a) => a.startsWith('-'))).join('');
  let code = 0;
  for (const f of operands(args)) {
    let st;
    try {
      st = fs.statSync(local(f));
    } catch {
      err(`ls: cannot access '${f}': No such file or directory\n`);
      code = 2;
      continue;
    }
    if (st.isDirectory() && !opts.includes('d')) {
      for (const n of fs.readdirSync(local(f)).sort()) out(n + (opts.includes('p') && fs.statSync(local(path.posix.join(f, n))).isDirectory() ? '/' : '') + '\n');
    } else out(f + '\n');
  }
  return code;
}

/** tail -c +N -F file: from byte N-1, then follows the name, starting over on truncation or replacement like GNU tail. */
function tail(args) {
  const start = Math.max(0, parseInt(args[args.indexOf('-c') + 1].replace('+', ''), 10) - 1);
  const f = operands(args)[0];
  const file = local(f);
  let pos = start;
  let ino = null;
  let missing = false;
  const tick = () => {
    if (fs.existsSync(marker('drop'))) {
      fs.rmSync(marker('drop'), { force: true });
      err(`Connection to ${host} closed by remote host.\n`);
      exit(255);
    }
    let st;
    try {
      st = fs.statSync(file);
    } catch {
      if (!missing) err(`tail: '${f}' has become inaccessible: No such file or directory\n`);
      missing = true;
      return;
    }
    if (ino !== null && st.ino !== ino) {
      err(missing ? `tail: '${f}' has appeared;  following new file\n` : `tail: '${f}' has been replaced;  following new file\n`);
      pos = 0;
    } else if (ino !== null && st.size < pos) {
      err(`tail: ${f}: file truncated\n`);
      pos = 0;
    }
    missing = false;
    ino = st.ino;
    if (st.size > pos) {
      const fd = fs.openSync(file, 'r');
      const buf = Buffer.alloc(Math.min(st.size - pos, 1 << 20));
      const n = fs.readSync(fd, buf, 0, buf.length, pos);
      fs.closeSync(fd);
      pos += n;
      process.stdout.write(buf.subarray(0, n));
    }
  };
  tick();
  setInterval(tick, 50);
  return null; // runs until it is killed
}

process.stdout.on('error', () => exit(141));
process.on('SIGTERM', () => exit(143));

const alternatives = [[]];
for (const w of words(command)) {
  if (w.op === '||') alternatives.push([]);
  else alternatives[alternatives.length - 1].push(w);
}
let code = 1;
for (const alt of alternatives) {
  const args = [];
  let stdinFile = null;
  let quiet = false;
  for (let k = 0; k < alt.length; k++) {
    const t = alt[k].text;
    if (t === '2>/dev/null') quiet = true;
    else if (t === '<') stdinFile = alt[++k].text;
    else args.push(...globExpand(alt[k]));
  }
  const [cmd, ...rest] = args;
  const realErr = process.stderr.write.bind(process.stderr);
  if (quiet) process.stderr.write = () => true;
  if (cmd === 'stat') code = stat(rest);
  else if (cmd === 'wc') code = wc(stdinFile);
  else if (cmd === 'cat') code = cat(rest);
  else if (cmd === 'ls') code = ls(rest);
  else if (cmd === 'tail') code = tail(rest);
  else {
    err(`sh: 1: ${cmd}: not found\n`);
    code = 127;
  }
  if (quiet) process.stderr.write = realErr;
  if (code === null || code === 0) break;
}
if (code !== null) process.stdout.write('', () => exit(code));
