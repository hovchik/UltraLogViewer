// Source locations mentioned in entries — `Foo.java:42`, `File.cs:line 42`, `at x (file.ts:10:5)`, Python's
// `File "x.py", line 3` — found in text, resolved against the configured source roots, and turned into an editor URL
// or command line. Pure, so the main process (which checks the disk) and the renderer (which draws the links) agree.

export interface SourceRef {
  start: number;
  length: number;
  path: string;
  line: number;
  /** 0 when the location has no column. */
  col: number;
  /** Java/Kotlin package of a frame (`at com.acme.Foo.bar(Foo.java:42)` gives "com.acme"): a bare file name then becomes a path under a source root. */
  pkg?: string;
}

const EXTS = 'java|kt|kts|scala|groovy|cs|fs|vb|ts|tsx|js|jsx|mjs|cjs|py|go|rb|php|c|cc|cpp|cxx|h|hpp|m|mm|swift|rs|dart|ex|exs|erl|clj|lua|pl|vue|svelte';

const PYTHON = /File "(?<path>[^"\r\n]+)", line (?<line>\d+)/g;
/** .NET: "in C:\My Projects\App\File.cs:line 42" (spaces allowed after " in "). */
const DOTNET_SPACED = /(?<= in )(?<path>(?:[A-Za-z]:[\\/]|\/)[^:\r\n]*?\.\w{1,6}):line (?<line>\d+)/g;
/** .NET without spaces in the path, or a bare "File.cs:line 42". */
const DOTNET = /(?<![\w.\\/-])(?<path>(?:[A-Za-z]:[\\/])?[^\s:()"'<>|]*\.\w{1,6}):line (?<line>\d+)/g;
const GENERIC = new RegExp(
  `(?<![\\w.\\\\/$-])(?:file:///?)?(?<path>(?:[A-Za-z]:[\\\\/])?[\\w.$@~+/\\\\-]*?[\\w$-]\\.(?:${EXTS})):(?<line>\\d+)(?::(?<col>\\d+))?(?!\\d)`,
  'g',
);
/** The "at com.acme.Foo.bar(" before a Java-style frame's "(Foo.java:42)". */
const JAVA_FRAME = /([\w$]+(?:\.[\w$]+)+)\.[\w$<>-]+\($/;

/** Every source location in the text, in order and without overlaps. */
export function findSourceRefs(text: string): SourceRef[] {
  const found: SourceRef[] = [];
  if (!text) return found;
  for (const re of [PYTHON, DOTNET_SPACED, DOTNET, GENERIC]) {
    re.lastIndex = 0;
    for (let m = re.exec(text); m; m = re.exec(text)) {
      const g = m.groups!;
      const line = Number(g.line);
      if (!(line > 0)) continue;
      const start = m.index;
      const length = m[0].length;
      if (found.some((f) => start < f.start + f.length && f.start < start + length)) continue;
      const pkg = packageOf(text, start, g.path);
      found.push({ start, length, path: g.path, line, col: g.col ? Number(g.col) : 0, ...(pkg ? { pkg } : {}) });
    }
  }
  return found.sort((a, b) => a.start - b.start);
}

function packageOf(text: string, start: number, path: string): string | undefined {
  if (path.includes('/') || path.includes('\\') || start === 0 || text[start - 1] !== '(') return undefined;
  const lineStart = text.lastIndexOf('\n', start - 1) + 1;
  const m = JAVA_FRAME.exec(text.slice(lineStart, start));
  if (!m) return undefined;
  const dot = m[1].lastIndexOf('.');
  return dot > 0 ? m[1].slice(0, dot) : undefined;
}

/** Paths to try under a source root, best first: the package path of a Java-style frame, then the path as written. */
export function sourceCandidates(r: SourceRef): string[] {
  const list = [r.path.replace(/\\/g, '/')];
  if (r.pkg) list.unshift(`${r.pkg.replace(/\./g, '/')}/${r.path}`);
  return list;
}

const SOURCE_EXTS = new Set(EXTS.split('|'));

/** The path functions resolveSource needs: node:path in the main process, path.posix or path.win32 in tests. */
export interface PathOps {
  join(...parts: string[]): string;
  resolve(...parts: string[]): string;
  sep: string;
}

/** Whether `p`, normalised, is inside `root` (a second check after the segment rules). */
function inside(p: string, root: string, ops: PathOps): boolean {
  const full = ops.resolve(p);
  let dir = ops.resolve(root);
  if (!dir.endsWith(ops.sep)) dir += ops.sep;
  const fold = ops.sep === '\\' || (typeof process !== 'undefined' && process.platform === 'darwin');
  return fold ? full.toLowerCase().startsWith(dir.toLowerCase()) : full.startsWith(dir);
}

/**
 * The file on disk a location refers to, or null. The text comes from the log, so it is not trusted: only an existing
 * source file (one of the known source extensions) inside a configured root is ever returned, and no path segment may
 * step out of the root ("..", a drive or a stream after a colon). The roots are tried in order, and under each root the
 * candidate paths, longest tail first, so an absolute path under a root opens as written and a path from the build
 * machine, /build/agent/src/app/File.cs, still finds app/File.cs under a local root.
 */
export function resolveSource(r: SourceRef, roots: string[], exists: (p: string) => boolean, ops: PathOps): string | null {
  const ext = /\.([^./\\]+)$/.exec(r.path)?.[1]?.toLowerCase();
  if (!ext || !SOURCE_EXTS.has(ext)) return null;
  for (const raw of roots) {
    const root = raw.trim();
    if (!root) continue;
    for (const cand of sourceCandidates(r)) {
      const segs = cand.split('/').filter(Boolean);
      // a drive letter is never a folder under a root
      let first = segs.length && segs[0].endsWith(':') ? 1 : 0;
      // the longest tail that stays inside the root: nothing before the last unsafe segment can be used
      // ("...", ".. " too: Windows trims those to "..")
      for (let i = first; i < segs.length; i++) if (!segs[i].replace(/[ .]+$/, '') || segs[i].includes(':')) first = i + 1;
      for (let k = first; k < segs.length; k++) {
        const p = ops.join(root, ...segs.slice(k));
        if (inside(p, root, ops) && exists(p)) return p;
      }
    }
  }
  return null;
}

/** Editor presets: id, label and the URL or command line template. */
export const EDITORS: { id: string; label: string; template: string }[] = [
  { id: 'vscode', label: 'VS Code', template: 'vscode://file/{path}:{line}' },
  { id: 'rider', label: 'Rider', template: 'rider://open?file={path}&line={line}' },
  { id: 'intellij', label: 'IntelliJ IDEA', template: 'idea://open?file={path}&line={line}' },
  { id: 'custom', label: 'Custom command', template: '' },
];

/** The template of an editor preset, or `custom` for "custom". */
export function editorTemplate(editor: string | undefined, custom: string | undefined): string {
  if (editor === 'custom') return custom ?? '';
  return (EDITORS.find((e) => e.id === editor) ?? EDITORS[0]).template;
}

/** A template is a URL ("scheme://…") rather than a command line. */
export const isUrlTemplate = (t: string) => /^[A-Za-z][\w+.-]*:\/\//.test(t.trimStart());

/**
 * A URL template that would hand the file itself to the system (file://{path}): that opens a source file with its
 * default program, which for a script runs it, so it is refused.
 */
export const isUnsafeUrlTemplate = (t: string) => /^file:/i.test(t.trimStart());

const fill = (s: string, path: string, line: number, col: number) =>
  s.split('{path}').join(path).split('{line}').join(String(line)).split('{col}').join(String(Math.max(1, col)));

/** A URL template with its placeholders filled in (forward slashes; spaces and specials escaped). */
export function expandUrl(template: string, path: string, line: number, col: number): string {
  let p = path.replace(/\\/g, '/');
  // vscode://file/{path} already has the slash before the path: vscode://file/home/me/x.cs, vscode://file/C:/x.cs
  if (template.includes('://file/')) p = p.replace(/^\/+/, '');
  p = p.replace(/[ #%?&]/g, (c) => '%' + c.charCodeAt(0).toString(16).toUpperCase().padStart(2, '0'));
  return fill(template.trim(), p, line, col);
}

/** A command line template split into the program and its arguments, placeholders filled in per argument. */
export function expandCommand(template: string, path: string, line: number, col: number): { program: string; args: string[] } {
  const parts: string[] = [];
  let cur = '';
  let quoted = false;
  let any = false;
  for (const c of template.trim()) {
    if (c === '"') {
      quoted = !quoted;
      any = true;
    } else if (/\s/.test(c) && !quoted) {
      if (any || cur) parts.push(cur);
      cur = '';
      any = false;
    } else cur += c;
  }
  if (any || cur) parts.push(cur);
  const filled = parts.map((a) => fill(a, path, line, col));
  return { program: filled[0] ?? '', args: filled.slice(1) };
}

export const NO_MATCH_HINT = 'Add the folder that holds your source code under Edit › Editor and source roots…';
