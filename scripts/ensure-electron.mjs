// Electron's own postinstall unzips its binary with extract-zip, which on some
// Node versions (seen on Node 26) exits early without error, leaving dist/
// half-empty and no path.txt. `npm start` then fails with "Electron failed to
// install correctly". This re-runs the extraction with the system tar/unzip.
import { createRequire } from 'node:module';
import { existsSync, mkdirSync, renameSync, rmSync, writeFileSync, readFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import path from 'node:path';

const require = createRequire(import.meta.url);

let electronDir;
try {
  electronDir = path.dirname(require.resolve('electron/package.json'));
} catch {
  process.exit(0); // electron not installed (e.g. production install)
}

const platformPath = {
  darwin: 'Electron.app/Contents/MacOS/Electron',
  win32: 'electron.exe',
}[process.platform] ?? 'electron';

const version = JSON.parse(readFileSync(path.join(electronDir, 'package.json'), 'utf8')).version;
const distDir = path.join(electronDir, 'dist');
const pathTxt = path.join(electronDir, 'path.txt');

function installed() {
  try {
    return (
      readFileSync(path.join(distDir, 'version'), 'utf8').replace(/^v/, '').trim() === version &&
      readFileSync(pathTxt, 'utf8') === platformPath &&
      existsSync(path.join(distDir, platformPath))
    );
  } catch {
    return false;
  }
}

if (installed() || process.env.ELECTRON_SKIP_BINARY_DOWNLOAD) process.exit(0);

console.log(`[ensure-electron] Electron ${version} binary missing, extracting it...`);

const { downloadArtifact } = require(require.resolve('@electron/get', { paths: [electronDir] }));
const zipPath = await downloadArtifact({
  version,
  artifactName: 'electron',
  platform: process.platform,
  arch: process.env.npm_config_arch || process.arch,
  checksums: require(path.join(electronDir, 'checksums.json')),
});

rmSync(distDir, { recursive: true, force: true });
mkdirSync(distDir, { recursive: true });
if (process.platform === 'linux') execFileSync('unzip', ['-q', zipPath, '-d', distDir], { stdio: 'inherit' });
else execFileSync('tar', ['-xf', zipPath, '-C', distDir], { stdio: 'inherit' });

const typeDefs = path.join(distDir, 'electron.d.ts');
if (existsSync(typeDefs)) renameSync(typeDefs, path.join(electronDir, 'electron.d.ts'));
writeFileSync(pathTxt, platformPath);

if (!installed()) {
  console.error('[ensure-electron] extraction finished but Electron still looks incomplete');
  process.exit(1);
}
console.log('[ensure-electron] done');
