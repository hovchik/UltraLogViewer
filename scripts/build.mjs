// Bundles main, preload, engine worker and renderer with esbuild.
import * as esbuild from 'esbuild';
import * as fs from 'node:fs';

const watch = process.argv.includes('--watch');
const common = { bundle: true, sourcemap: true, logLevel: 'info', target: 'es2022' };

fs.rmSync('dist', { recursive: true, force: true });
fs.mkdirSync('dist/renderer', { recursive: true });
for (const f of ['index.html', 'styles.css']) fs.copyFileSync(`src/renderer/${f}`, `dist/renderer/${f}`);

const builds = [
  { ...common, platform: 'node', format: 'cjs', external: ['electron'], entryPoints: ['src/main/main.ts'], outfile: 'dist/main/main.js' },
  { ...common, platform: 'node', format: 'cjs', external: ['electron'], entryPoints: ['src/main/engineWorker.ts'], outfile: 'dist/main/engineWorker.js' },
  { ...common, platform: 'node', format: 'cjs', external: ['electron'], entryPoints: ['src/preload/preload.ts'], outfile: 'dist/preload/preload.js' },
  { ...common, platform: 'browser', format: 'iife', entryPoints: ['src/renderer/app.ts'], outfile: 'dist/renderer/app.js' },
];

if (watch) {
  for (const b of builds) await (await esbuild.context(b)).watch();
} else {
  await Promise.all(builds.map((b) => esbuild.build(b)));
}
