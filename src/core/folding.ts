// Stack-trace folding: runs of framework frames (java.*, System.*, node:internal, site-packages/…) collapse into
// one "… N framework frames" placeholder so the application frames stand out.

export interface FoldLine {
  /** The line text, or the placeholder label. */
  line: string;
  /** 0 for a kept line; N for a placeholder replacing N lines. */
  folded: number;
}

export const DEFAULT_FRAMEWORK_PACKAGES = [
  'System.', 'Microsoft.', 'java.', 'javax.', 'jdk.', 'sun.', 'com.sun.', 'org.springframework.', 'org.apache.',
  'org.hibernate.', 'kotlin.', 'kotlinx.', 'scala.', 'akka.', 'io.netty.', 'reactor.', 'node:internal', 'node_modules/',
  'internal/', '<frozen', 'site-packages/', 'asyncio/', 'django/', 'werkzeug/',
];

type FrameKind = 'at' | 'py' | null;

function frameKind(line: string): FrameKind {
  const t = line.trimStart();
  if (t.startsWith('at ')) return 'at';
  if (t.startsWith('File "')) return 'py';
  return null;
}

function isFramework(line: string, kind: FrameKind, packages: string[]): boolean {
  const t = line.trim();
  const body = kind === 'at' ? t.slice(3).trimStart() : t;
  for (const p of packages) {
    if (!p) continue;
    if (body.startsWith(p) || body.includes('(' + p)) return true;
    if ((p.includes('/') || p.includes(':') || p.startsWith('<')) && body.includes(p)) return true;
  }
  return false;
}

/** Folds runs of two or more consecutive framework frames. */
export function fold(lines: string[], packages: string[]): FoldLine[] {
  const out: FoldLine[] = [];
  let i = 0;
  while (i < lines.length) {
    const kind = frameKind(lines[i]);
    if (!kind || !isFramework(lines[i], kind, packages)) {
      out.push({ line: lines[i], folded: 0 });
      i++;
      continue;
    }
    // collect the run of framework frames (with the source line under a Python frame)
    let j = i;
    let frames = 0;
    let other = 0;
    while (j < lines.length) {
      const k = frameKind(lines[j]);
      if (!k || !isFramework(lines[j], k, packages)) break;
      frames++;
      j++;
      if (k === 'py' && j < lines.length && !frameKind(lines[j]) && /^\s/.test(lines[j])) {
        other++;
        j++;
      }
    }
    if (frames >= 2) {
      const n = frames + other;
      out.push({ line: `… ${n} framework ${other ? 'lines' : 'frames'}`, folded: n });
    } else {
      for (let k = i; k < j; k++) out.push({ line: lines[k], folded: 0 });
    }
    i = j;
  }
  return out;
}
