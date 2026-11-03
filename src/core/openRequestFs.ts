// The file side of an open request (main process only): resolving relative paths, globs and zip members.

import * as fs from 'node:fs';
import * as path from 'node:path';
import { looksRemote } from './remoteTarget';
import { MAX_FILES, OpenRequest } from './openRequest';

const isFile = (p: string) => {
  try {
    return fs.statSync(p).isFile();
  } catch {
    return false;
  }
};

const globRe = (name: string) =>
  new RegExp('^' + name.replace(/[.+^${}()|[\]\\]/g, '\\$&').replace(/\*/g, '.*').replace(/\?/g, '.') + '$', process.platform === 'win32' ? 'i' : '');

/**
 * The files to open: relative paths resolved against `cwd`, globs (* and ? in the file name) expanded in name order,
 * zip members ("a.zip::logs/app.log") and "-" kept. Missing files and globs that match nothing go to `r.errors`.
 */
export function expandPaths(r: OpenRequest, cwd: string): string[] {
  const out: string[] = [];
  for (const raw of r.paths) {
    // "-" is stdin; ssh://host/path and host:/path are remote files, opened over ssh as they are
    if (raw === '-' || looksRemote(raw)) {
      out.push(raw);
      continue;
    }
    const sep = raw.indexOf('::');
    const p = path.resolve(cwd, sep > 0 ? raw.slice(0, sep) : raw);
    const name = path.basename(p);
    if (sep < 0 && /[*?]/.test(name)) {
      const dir = path.dirname(p);
      let matches: string[] = [];
      try {
        const re = globRe(name);
        matches = fs.readdirSync(dir).filter((f) => re.test(f)).sort().map((f) => path.join(dir, f)).filter(isFile).slice(0, MAX_FILES + 1);
      } catch {
        // no such folder
      }
      if (!matches.length) r.errors.push(`No files match ${raw}`);
      out.push(...matches);
      continue;
    }
    if (!isFile(p)) r.errors.push(`File not found: ${raw}`);
    else out.push(sep > 0 ? p + raw.slice(sep) : p);
  }
  if (out.length > MAX_FILES) r.errors.push(`Too many files (${out.length}; at most ${MAX_FILES})`);
  return [...new Set(out)].slice(0, MAX_FILES);
}
