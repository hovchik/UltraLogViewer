// electron-builder needs its winCodeSign bundle (rcedit, signtool) to put the
// app icon and version info into UltraLogViewer.exe. The bundle's archive also
// holds macOS symlinks, and on Windows 7-Zip can only create those with admin
// rights or Developer Mode, so electron-builder's own extraction fails with
// "Cannot create symbolic link: A required privilege is not held by the client".
// This pre-extracts the bundle into electron-builder's cache without the macOS
// part, so electron-builder finds it and skips its own download.
//
//   node scripts/prepare-wincodesign.mjs            # no-op except on Windows
//   node scripts/prepare-wincodesign.mjs --force    # any OS (for testing)
import { createRequire } from 'node:module';
import { chmodSync, existsSync, mkdirSync, renameSync, rmSync, writeFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import os from 'node:os';
import path from 'node:path';

const NAME = 'winCodeSign-2.6.0';
const force = process.argv.includes('--force');
if (process.platform !== 'win32' && !force) process.exit(0);

if (process.env.ELECTRON_BUILDER_CACHE) {
  // electron-builder keys its cache differently then; leave it to electron-builder.
  console.log('prepare-wincodesign: ELECTRON_BUILDER_CACHE is set, skipping');
  process.exit(0);
}

function cacheRoot() {
  if (process.platform === 'win32') {
    return path.join(process.env.LOCALAPPDATA ?? path.join(os.homedir(), 'AppData', 'Local'), 'electron-builder', 'Cache');
  }
  if (process.platform === 'darwin') return path.join(os.homedir(), 'Library', 'Caches', 'electron-builder');
  return path.join(process.env.XDG_CACHE_HOME ?? path.join(os.homedir(), '.cache'), 'electron-builder');
}

const target = path.join(cacheRoot(), 'winCodeSign', NAME);
if (existsSync(path.join(target, 'rcedit-x64.exe'))) process.exit(0);

const base =
  process.env.ELECTRON_BUILDER_BINARIES_MIRROR ?? 'https://github.com/electron-userland/electron-builder-binaries/releases/download/';
const url = `${base}${NAME}/${NAME}.7z`;
const work = path.join(cacheRoot(), 'winCodeSign', `${NAME}.tmp-${process.pid}`);
const archive = `${work}.7z`;

try {
  mkdirSync(work, { recursive: true });
  console.log(`prepare-wincodesign: downloading ${url}`);
  const res = await fetch(url);
  if (!res.ok) throw new Error(`download failed: HTTP ${res.status}`);
  writeFileSync(archive, Buffer.from(await res.arrayBuffer()));

  const { path7za } = createRequire(import.meta.url)('7zip-bin');
  if (process.platform !== 'win32') chmodSync(path7za, 0o755); // npm drops the exec bit
  // -xr!darwin: skip the macOS tools, which are the only symlinks in the archive.
  execFileSync(path7za, ['x', '-bd', '-y', `-o${work}`, '-xr!darwin', archive], { stdio: ['ignore', 'ignore', 'inherit'] });
  if (!existsSync(path.join(work, 'rcedit-x64.exe'))) throw new Error('rcedit-x64.exe missing after extraction');

  rmSync(target, { recursive: true, force: true });
  renameSync(work, target);
  console.log(`prepare-wincodesign: extracted to ${target}`);
} finally {
  rmSync(work, { recursive: true, force: true });
  rmSync(archive, { force: true });
}
