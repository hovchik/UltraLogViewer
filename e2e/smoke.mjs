// End-to-end smoke test: launches the real Electron app on generated logs, drives the UI and
// saves screenshots to e2e/out/. Run with `npm run test:e2e` (needs a display; use xvfb-run on CI).
import { _electron as electron } from 'playwright';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { execFileSync } from 'node:child_process';
import assert from 'node:assert/strict';

const out = path.resolve('e2e/out');
fs.mkdirSync(out, { recursive: true });
const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'ulv-e2e-'));
const log = path.join(tmp, 'app.log');
execFileSync(process.execPath, ['scripts/gen-sample.mjs', log, '5']);
const other = path.join(tmp, 'other.log');
fs.writeFileSync(other, '2024-05-01 08:00:00,500 [main] ERROR com.acme.Other - from the other file\n');

const app = await electron.launch({
  args: ['--no-sandbox', '.', log],
  env: { ...process.env, ELECTRON_ENABLE_LOGGING: '1' },
});
const win = await app.firstWindow();
win.on('pageerror', (e) => console.error('pageerror:', e));
win.on('console', (m) => m.type() === 'error' && console.error('console:', m.text()));
await win.setViewportSize?.({ width: 1440, height: 900 });

const step = async (name, fn) => {
  process.stdout.write(`• ${name} … `);
  await fn();
  console.log('ok');
};
const shot = (name) => win.screenshot({ path: path.join(out, `${name}.png`) });
const status = () => win.locator('#status').innerText();

await step('opens the file from the command line and shows rows', async () => {
  await win.waitForSelector('.grid-rows .r .c', { timeout: 20000 });
  await win.waitForFunction(() => !document.querySelector('#status')?.textContent?.includes('Indexing'), null, { timeout: 30000 });
  const s = await status();
  assert.match(s, /Timestamped text/);
  assert.match(s, /entries/);
  await shot('01-open');
});

await step('detects columns and levels', async () => {
  const headers = await win.locator('.grid-header .h').allInnerTexts();
  assert.deepEqual(headers, ['Time', 'Level', 'Thread', 'Logger', 'Message']);
  assert.ok((await win.locator('.lvl').count()) >= 4);
});

await step('searches with highlights and navigation', async () => {
  await win.fill('#search', 'ConnectionPool');
  await win.waitForFunction(() => /\d+ \/ [\d,]+/.test(document.querySelector('#search-count')?.textContent ?? ''));
  assert.ok((await win.locator('.grid-rows mark').count()) > 0);
  await win.press('#search', 'Enter');
  await shot('02-search');
});

await step('quick filter narrows the view', async () => {
  await win.fill('#quick', 'level:error OR level:fatal');
  await win.waitForFunction(() => /shown/.test(document.querySelector('#status')?.textContent ?? ''), null, { timeout: 20000 });
  const levels = await win.locator('.grid-rows .r .lvtag').allInnerTexts();
  assert.ok(levels.length > 0 && levels.every((l) => l === 'ERROR' || l === 'FATAL'), `levels: ${levels.slice(0, 5)}`);
  await shot('03-filter');
});

await step('selecting a multi-line entry shows details', async () => {
  await win.locator('.grid-rows .r', { hasText: '+' }).first().click();
  await win.waitForSelector('#detail pre');
  assert.match(await win.locator('#detail pre').innerText(), /at com\.acme/);
  await shot('04-detail');
});

await step('bookmarks a row', async () => {
  await win.keyboard.press('b');
  await win.waitForSelector('.grid-rows .bmk');
  assert.match(await win.locator('#sidebar').innerText(), /Bookmarks \(1\)/i);
});

await step('clears the filter and follows appended lines', async () => {
  await win.fill('#quick', '');
  await win.waitForFunction(() => !/shown/.test(document.querySelector('#status')?.textContent ?? ''), null, { timeout: 20000 });
  await win.click('#btn-follow');
  fs.appendFileSync(log, '2030-01-01 00:00:00,000 [main] ERROR com.acme.Tail - appended while following\n');
  await win.waitForSelector('.grid-rows .r:has-text("appended while following")', { timeout: 10000 });
  await shot('05-follow');
});

await step('merges two files into one timeline', async () => {
  await win.evaluate(([a, b]) => window.__ulv.openPaths([a, b], true), [log, other]);
  await win.waitForFunction(() => document.querySelectorAll('.tab').length === 2);
  await win.waitForFunction(() => [...document.querySelectorAll('.grid-header .h')].some((h) => h.textContent === 'Source'), null, { timeout: 30000 });
  await win.waitForSelector('.grid-rows .r:has-text("from the other file")', { timeout: 30000 });
  await shot('06-merged');
});

await step('dark theme renders', async () => {
  await win.click('#btn-theme');
  await shot('07-dark');
});

await app.close();
console.log(`Screenshots in ${out}`);
