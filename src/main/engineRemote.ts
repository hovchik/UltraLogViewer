// The engine's side of remote files: turning ssh:// URLs (and user@host:/path, and patterns) into local copies that
// core/remote.ts keeps up to date, and listing remote folders.

import * as os from 'node:os';
import * as path from 'node:path';
import { expandRemote, listRemote, RemoteFile } from '../core/remote';
import { looksRemote, parseRemote, remoteDisplay } from '../core/remoteTarget';

/** Next to the index cache (in the app's user-data folder), else a folder of this process in the temp folder. */
export const remoteCacheDir = (indexCacheDir?: string) =>
  indexCacheDir ? path.join(path.dirname(indexCacheDir), 'remote-cache') : path.join(os.tmpdir(), `ulv-remote-${process.pid}`);

/** Replaces remote paths by local copies, connecting to all hosts at once; what failed is in `errors`. */
export async function localCopies(paths: string[], opts: { ssh?: string | null; cacheDir: string }): Promise<{ paths: string[]; errors: string[] }> {
  const errors: string[] = [];
  const slots = await Promise.all(
    paths.map(async (p): Promise<string[]> => {
      if (!looksRemote(p)) return [p];
      try {
        const targets = await expandRemote(parseRemote(p), opts);
        const opened = await Promise.all(
          targets.map(async (t) => {
            try {
              return (await RemoteFile.open(t, opts)).localPath;
            } catch (e) {
              errors.push((e as Error).message || remoteDisplay(t));
              return null;
            }
          }),
        );
        return opened.filter((x): x is string => !!x);
      } catch (e) {
        errors.push(looksRemote(p) && /^Bad |^The remote|^Write|^Add/.test((e as Error).message) ? `${p}: ${(e as Error).message}` : (e as Error).message);
        return [];
      }
    }),
  );
  return { paths: [...new Set(slots.flat())], errors };
}

export function listRemoteFolder(url: string, ssh?: string | null): Promise<string[]> {
  return listRemote(parseRemote(url), { ssh });
}
