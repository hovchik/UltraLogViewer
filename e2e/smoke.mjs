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
const launchLog = path.join(tmp, 'launched.txt');
const notes = path.join(tmp, 'notes.txt');
fs.writeFileSync(notes, 'Release notes for build 4.2\n\nInstalled packages:\n  - libfoo 1.2\nWARNING: disk almost full\nerror: could not copy config.ini\ndone\n');

const app = await electron.launch({
  // a fresh profile so settings from earlier runs (theme, wrap, dock…) don't leak in
  args: ['--no-sandbox', `--user-data-dir=${path.join(tmp, 'profile')}`, '.', log],
  // source links write what they would open to launchLog instead of starting an editor
  env: { ...process.env, ELECTRON_ENABLE_LOGGING: '1', ULV_SOURCE_LAUNCH_LOG: launchLog },
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

await step('filters by a cell value from the row menu', async () => {
  const cell = win.locator('.grid-rows .r .c[data-col="logger"]').first();
  const logger = (await cell.innerText()).trim();
  await cell.click({ button: 'right' });
  const item = win.locator('.ctxmenu .item', { hasText: `Only Logger = “` });
  await item.waitFor();
  await shot('08-row-menu');
  await item.click();
  await win.waitForFunction(() => /shown/.test(document.querySelector('#status')?.textContent ?? ''), null, { timeout: 20000 });
  const loggers = await win.locator('.grid-rows .r .c[data-col="logger"]').allInnerTexts();
  assert.ok(loggers.length > 0 && loggers.every((l) => l.trim() === logger), `loggers: ${loggers.slice(0, 5)}`);
  assert.ok((await win.locator('#sidebar .rule .txt').allInnerTexts()).includes(`Logger = ${logger}`));
});

await step('shows every entry in context and goes back', async () => {
  const shown = await status();
  await win.focus('#grid');
  await win.keyboard.press('c');
  await win.waitForSelector('#ctx-banner:not(.hidden)');
  assert.match(await win.locator('#ctx-banner').innerText(), /Showing every entry around line \d+\. Rows your filters hide are dimmed\./);
  await win.waitForFunction(() => /filters ignored/.test(document.querySelector('#status')?.textContent ?? ''));
  await win.waitForSelector('.grid-rows .r.ctx');
  await shot('09-context');
  await win.keyboard.press('Escape');
  await win.waitForSelector('#ctx-banner.hidden', { state: 'attached' });
  await win.waitForFunction(() => /shown/.test(document.querySelector('#status')?.textContent ?? ''));
  assert.equal((await status()).match(/[\d,]+ shown/)?.[0], shown.match(/[\d,]+ shown/)?.[0]);
});

await step('lists parsed fields with their top values', async () => {
  const shown = (await status()).match(/([\d,]+) shown/)[1];
  // Stats follow the filtered view: one logger left.
  await win.waitForFunction((n) => (document.querySelector('#sidebar .fields-sec .empty-hint')?.textContent ?? '').includes(`of ${n} entries`), shown, { timeout: 20000 });
  const logger = win.locator('#sidebar .fields-sec .fld', { hasText: 'logger' });
  assert.match(await logger.innerText(), /\b1 value\b/);
  await win.locator('#sidebar .fields-sec .fld', { hasText: 'thread' }).first().click();
  await win.waitForSelector('#sidebar .fields-sec .fv');
});

await step('suggests field values in the quick filter', async () => {
  await win.click('#quick');
  await win.keyboard.type('logger:');
  await win.waitForSelector('#quick-ac:not(.hidden) .ac-item', { timeout: 10000 });
  assert.ok((await win.locator('#quick-ac .ac-item').count()) > 0);
  await shot('10-autocomplete');
  await win.keyboard.press('Escape'); // closes the dropdown
  await win.keyboard.press('Escape'); // clears the box
  await win.locator('#sidebar .rule', { hasText: 'Logger =' }).locator('.mini.danger').click();
  await win.waitForFunction(() => !/shown/.test(document.querySelector('#status')?.textContent ?? ''), null, { timeout: 20000 });
});

await step('searches with highlights and navigation', async () => {
  await win.fill('#search', 'ConnectionPool');
  await win.waitForFunction(() => /\d+ \/ [\d,]+/.test(document.querySelector('#search-count')?.textContent ?? ''));
  assert.ok((await win.locator('.grid-rows mark').count()) > 0);
  await win.waitForFunction(() => /^Searched [\d,]+ entries in \d+\.\d\d s$/.test(document.querySelector('#status .scan-cost')?.textContent ?? ''));
  await win.press('#search', 'Enter');
  await shot('02-search');
});

await step('typed queries filter, bad ones are reported, time shortcuts set From', async () => {
  await win.fill('#quick', 'level>=error and not level:fatal');
  await win.waitForFunction(() => /shown/.test(document.querySelector('#status')?.textContent ?? ''), null, { timeout: 20000 });
  const levels = await win.locator('.grid-rows .r .lvtag').allInnerTexts();
  assert.ok(levels.length > 0 && levels.every((l) => l === 'ERROR'), `levels: ${levels.slice(0, 5)}`);
  const shown = (await status()).match(/[\d,]+ shown/)?.[0];
  await win.fill('#quick', 'level>=loud');
  await win.waitForSelector('#toast.err:not(.hidden)');
  assert.match(await win.locator('#toast').innerText(), /^Quick filter: level>=loud: "loud" is not a level/);
  assert.equal((await status()).match(/[\d,]+ shown/)?.[0], shown); // the last good filter stays
  await win.fill('#quick', '');
  await win.waitForFunction(() => !/shown/.test(document.querySelector('#status')?.textContent ?? ''), null, { timeout: 20000 });
  // Not following, so -15m counts back from the last entry (08:39:15.250 in the generated file), not from the clock.
  const following = (await win.locator('#btn-follow.on').count()) > 0;
  if (following) await win.click('#btn-follow');
  const quarter = win.locator('#sidebar .shortcuts .mini', { hasText: '-15m' });
  assert.match(await quarter.getAttribute('title'), /last entry in the file/);
  await quarter.click();
  await win.waitForFunction(() => /shown/.test(document.querySelector('#status')?.textContent ?? ''), null, { timeout: 20000 });
  assert.equal(await win.locator('#sidebar input[type="datetime-local"]').first().inputValue(), '2024-05-01T08:24:15');
  assert.notEqual((await status()).match(/([\d,]+) shown/)?.[1], '0');
  await shot('11-time-shortcut');
  await win.locator('#sidebar .sec', { hasText: 'Time range' }).locator('h3 .mini', { hasText: 'Clear' }).click();
  await win.waitForFunction(() => !/shown/.test(document.querySelector('#status')?.textContent ?? ''), null, { timeout: 20000 });
  // Leave follow and the selection as they were: the focused row survives the filter change.
  if ((await win.locator('#btn-follow.on').count() > 0) !== following) await win.click('#btn-follow');
  await win.focus('#grid');
  await win.keyboard.press('Home');
});

await step('quick filter narrows the view', async () => {
  await win.fill('#quick', 'level:error OR level:fatal');
  await win.waitForFunction(() => /shown/.test(document.querySelector('#status')?.textContent ?? ''), null, { timeout: 20000 });
  const levels = await win.locator('.grid-rows .r .lvtag').allInnerTexts();
  assert.ok(levels.length > 0 && levels.every((l) => l === 'ERROR' || l === 'FATAL'), `levels: ${levels.slice(0, 5)}`);
  // The search that runs again over the new view keeps the filter's cost on show.
  await win.waitForFunction(() => /^Filtered [\d,]+ entries in \d+\.\d\d s$/.test(document.querySelector('#status .scan-cost')?.textContent ?? ''));
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

await step('colours tokens in messages', async () => {
  await win.waitForSelector('.grid-rows .r:not(.sel) .c.msg [class^="tk-"]');
  const kinds = await win.$$eval('.grid-rows .r:not(.sel) .c.msg [class^="tk-"]', (els) => [...new Set(els.map((e) => e.className))]);
  assert.ok(kinds.includes('tk-number'), `token kinds: ${kinds}`);
  assert.notEqual(await win.$eval('.grid-rows .r:not(.sel) .c.msg .tk-number', (e) => getComputedStyle(e).color), await win.$eval('.grid-rows .r:not(.sel) .c.msg', (e) => getComputedStyle(e).color));
  // the level stripe replaces the row tint by default
  assert.equal(await win.locator('#grid.tint').count(), 0);
  assert.equal(await win.$eval('.grid-rows .r.l5:not(.sel)', (e) => getComputedStyle(e, '::after').width), '3px');
  await shot('11-tokens');
});

await step('wraps long messages with Alt+Z', async () => {
  // narrow the message column so messages wrap
  await win.setViewportSize({ width: 1000, height: 900 });
  await win.focus('#grid');
  await win.keyboard.press('Alt+z');
  await win.waitForSelector('#grid.wrap .grid-rows .r.var');
  const tallest = await win.$$eval('.grid-rows .r.var', (els) => Math.max(...els.map((e) => e.offsetHeight)));
  assert.ok(tallest > 30, `tallest wrapped row: ${tallest}px`);
  await shot('12-wrap');
  await win.keyboard.press('Alt+z');
  await win.waitForSelector('#grid:not(.wrap)');
  assert.equal(await win.locator('.grid-rows .r.var').count(), 0);
  await win.setViewportSize({ width: 1440, height: 900 });
});

await step('expands a multi-line entry inline with the arrow keys', async () => {
  await win.locator('.grid-rows .r', { has: win.locator('.more') }).first().locator('.c.msg').click();
  await win.focus('#grid');
  await win.keyboard.press('ArrowRight');
  const open = win.locator('.grid-rows .r.cur.open');
  await open.waitFor();
  assert.match(await open.locator('.rx').innerText(), /at com\.acme/);
  assert.ok((await open.evaluate((e) => e.offsetHeight)) > 60);
  await shot('13-expand');
  await win.keyboard.press('ArrowLeft');
  await win.waitForSelector('.grid-rows .r.open', { state: 'detached' });
});

await step('shows the time column as deltas from the header menu', async () => {
  await win.locator('.grid-header .h[data-key="time"]').click({ button: 'right' });
  await win.locator('.ctxmenu .item', { hasText: 'Delta from previous row' }).click();
  await win.waitForFunction(() => [...document.querySelectorAll('.grid-rows .r .c[data-col="time"]')].slice(1).every((c) => /^\+[\d.]+ m?s|^\+\d+m |^0 ms$/.test(c.textContent ?? '')));
  await shot('14-time-delta');
  await win.locator('.grid-header .h[data-key="time"]').click({ button: 'right' });
  await win.locator('.ctxmenu .item', { hasText: 'Time as written' }).click();
  await win.waitForFunction(() => /^\d{4}-\d{2}-\d{2} /.test(document.querySelector('.grid-rows .r .c[data-col="time"]')?.textContent ?? ''));
});

await step('docks the details pane on the right', async () => {
  await win.locator('#detail .dock-btn').click();
  await win.waitForSelector('#work.dock-right');
  const g = await win.locator('#grid').boundingBox();
  const d = await win.locator('#detail').boundingBox();
  assert.ok(d.x >= g.x + g.width - 1 && d.height > g.height - 2, `grid ${JSON.stringify(g)} detail ${JSON.stringify(d)}`);
  await shot('15-detail-right');
  await win.locator('#detail .dock-btn').click();
  await win.waitForSelector('#work:not(.dock-right)');
});

await step('shows a timeline tooltip on hover', async () => {
  const box = await win.locator('#histogram canvas').boundingBox();
  await win.mouse.move(box.x + box.width / 2, box.y + box.height - 10);
  await win.waitForSelector('#histogram .hist-tip', { state: 'visible' });
  assert.match(await win.locator('#histogram .hist-tip').innerText(), /^\d{2}:\d{2}:\d{2} – \d{2}:\d{2}:\d{2}\n[\d,]+ entries/);
  await shot('16-hist-tip');
  await win.mouse.move(box.x + box.width / 2, box.y + box.height + 200);
});

await step('folds framework frames in an expanded entry and the details pane', async () => {
  // the error filter is still on: pick an entry with a stack trace
  await win.locator('.grid-rows .r', { has: win.locator('.more') }).first().locator('.c.msg').click();
  await win.focus('#grid');
  await win.keyboard.press('ArrowRight');
  const open = win.locator('.grid-rows .r.cur.open');
  await open.waitFor();
  assert.match(await open.locator('.rx .fold').innerText(), /^… 3 framework frames$/);
  assert.match(await open.locator('.rx').innerText(), /at com\.acme\.api\.OrderController/);
  const fold = win.locator('#detail pre .fold');
  await fold.waitFor();
  assert.equal(await fold.innerText(), '… 3 framework frames');
  assert.equal(await fold.evaluate((e) => getComputedStyle(e).fontStyle), 'italic');
  await shot('17-fold');
  // clicking the placeholder unfolds this entry only
  await fold.click();
  await win.waitForSelector('#detail pre .fold', { state: 'detached' });
  assert.match(await win.locator('#detail pre').innerText(), /ThreadPoolExecutor\.runWorker/);
  await win.waitForSelector('.grid-rows .r.cur.open .rx .fold', { state: 'detached' });
  await win.focus('#grid');
  await win.keyboard.press('ArrowLeft');
  await win.waitForSelector('.grid-rows .r.open', { state: 'detached' });
});

await step('Ctrl+click on a token searches for it', async () => {
  const tok = win.locator('.grid-rows .r:not(.sel) .c.msg .tk-number').first();
  const text = (await tok.innerText()).trim();
  const row = Number(await tok.evaluate((e) => e.closest('.r').dataset.row));
  await tok.click({ modifiers: ['Control'] });
  await win.waitForFunction((t) => document.querySelector('#search').value === t, text);
  await win.waitForFunction(() => /^\d[\d,]* \/ [\d,]+/.test(document.querySelector('#search-count')?.textContent ?? ''));
  const focus = await win.evaluate(() => window.__ulv.state().active.focus);
  assert.ok(focus >= row, `selected hit ${focus} should be at or after the clicked row ${row}`);
  assert.ok((await win.locator('.grid-rows mark').count()) > 0);
  await win.press('#search', 'Escape');
  await win.waitForFunction(() => document.querySelector('#search-count')?.textContent === '');
});

await step('opens the command palette and runs a menu action', async () => {
  await win.fill('#quick', '');
  await win.waitForFunction(() => !/shown/.test(document.querySelector('#status')?.textContent ?? ''), null, { timeout: 20000 });
  await win.focus('#grid');
  await win.keyboard.press('Control+Shift+P');
  await win.waitForSelector('.modal .palette .pal-item');
  const all = await win.locator('.palette .pal-item').count();
  assert.ok(all > 30, `palette items: ${all}`);
  await win.keyboard.type('identical');
  await win.waitForFunction(() => document.querySelectorAll('.palette .pal-item').length === 1);
  assert.match(await win.locator('.palette .pal-item .lbl').innerText(), /^View › Group repeats › Identical entries$/);
  await shot('18-palette');
  await win.keyboard.press('Enter');
  await win.waitForSelector('#modal.hidden', { state: 'attached' });
});

await step('groups repeated entries with a ×N badge', async () => {
  await win.waitForFunction(() => /grouped/.test(document.querySelector('#status')?.textContent ?? ''), null, { timeout: 20000 });
  const badge = win.locator('.grid-rows .r .c.msg .rep').first();
  await badge.waitFor();
  assert.match(await badge.innerText(), /^×\d+$/);
  assert.ok(/Heartbeat ok/.test(await badge.locator('xpath=..').innerText()));
  await shot('19-group');
  // the palette shows the radio state, then turns grouping off again
  await win.keyboard.press('F1');
  await win.waitForSelector('.modal .palette');
  await win.keyboard.type('group repeats');
  assert.equal(await win.locator('.palette .pal-item', { hasText: 'Identical entries' }).locator('.chk').innerText(), '✓');
  await win.keyboard.type(' off');
  await win.keyboard.press('Enter');
  await win.waitForFunction(() => !/grouped|shown/.test(document.querySelector('#status')?.textContent ?? ''), null, { timeout: 20000 });
  assert.equal(await win.locator('.grid-rows .rep').count(), 0);
});

await step('lists patterns and filters to one with a double-click', async () => {
  await win.click('#btn-patterns');
  await win.waitForSelector('#patterns:not(.hidden) .pt-row', { timeout: 20000 });
  assert.ok(await win.locator('#grid').evaluate((e) => e.classList.contains('hidden')));
  assert.match(await win.locator('#patterns .pt-sum').innerText(), /^[\d,]+ patterns in [\d,]+ of [\d,]+ entries/);
  assert.ok((await win.locator('#patterns .pt-tpl .ph').count()) > 0);
  assert.match(await win.locator('#detail').innerText(), /Pattern/);
  await shot('20-patterns');
  const heartbeat = win.locator('#patterns .pt-row', { hasText: 'Heartbeat ok' });
  await heartbeat.dblclick();
  await win.waitForSelector('#patterns.hidden', { state: 'attached' });
  await win.waitForFunction(() => /shown/.test(document.querySelector('#status')?.textContent ?? ''), null, { timeout: 20000 });
  assert.ok((await win.locator('#sidebar .rule .txt').allInnerTexts()).includes('Pattern ≈ Heartbeat ok (queue empty)'));
  const msgs = await win.locator('.grid-rows .r .c.msg').allInnerTexts();
  assert.ok(msgs.length > 0 && msgs.every((m) => m === 'Heartbeat ok (queue empty)'), `messages: ${msgs.slice(0, 3)}`);
  await win.locator('#sidebar .rule', { hasText: 'Pattern ≈' }).locator('.mini.danger').click();
  await win.waitForFunction(() => !/shown/.test(document.querySelector('#status')?.textContent ?? ''), null, { timeout: 20000 });
});

await step('runs SQL over the entries and jumps to a result row', async () => {
  await win.focus('#grid');
  await win.keyboard.press('Control+Shift+Q');
  await win.waitForSelector('#sql:not(.hidden) .sql-editor');
  assert.ok(await win.evaluate(() => document.activeElement?.classList.contains('sql-editor')));
  await win.fill('#sql .sql-editor', "SELECT line, level, logger, message\nFROM view\nWHERE level = 'ERROR'\nORDER BY line DESC\nLIMIT 20");
  await win.keyboard.press('Control+Enter');
  await win.waitForSelector('#sql .sql-table tbody tr', { timeout: 30000 });
  assert.deepEqual(await win.locator('#sql .sql-table th').allInnerTexts(), ['line', 'level', 'logger', 'message']);
  assert.match(await win.locator('#sql .sql-status').innerText(), /^\d+ rows? in \d+ ms/);
  assert.ok((await win.locator('#sql .sql-table td:nth-child(2)').allInnerTexts()).every((l) => l === 'ERROR'));
  const target = win.locator('#sql .sql-table tbody tr').nth(2);
  const line = (await target.locator('td').first().innerText()).trim();
  await target.click();
  await win.waitForFunction((ln) => (document.querySelector('#grid .grid-rows .r.sel .g')?.textContent ?? '').endsWith(ln), line, { timeout: 10000 });
  assert.ok(await target.evaluate((e) => e.classList.contains('sel')));
  // a write is refused inline
  await win.fill('#sql .sql-editor', 'DELETE FROM logs');
  await win.click('#sql .sql-head .btn.primary');
  await win.waitForSelector('#sql .sql-error:not(.hidden)');
  assert.match(await win.locator('#sql .sql-error').innerText(), /read-only/);
  // an example with a chart
  await win.locator('#sql .sql-head select').selectOption({ label: 'Errors per minute' });
  await win.waitForFunction(() => !document.querySelector('#sql .sql-head .tog-btn')?.disabled, null, { timeout: 30000 });
  await win.click('#sql .sql-head .tog-btn');
  await win.waitForSelector('#sql .sql-chart:not(.hidden)');
  await shot('26-sql-chart');
  await win.click('#sql .sql-head .tog-btn');
  await win.fill('#sql .sql-editor', "SELECT line, level, logger, message\nFROM view\nWHERE level = 'ERROR'\nORDER BY line DESC\nLIMIT 20");
  await win.keyboard.press('Control+Enter');
  await win.waitForSelector('#sql .sql-table tbody tr');
  await shot('26-sql');
  await win.keyboard.press('Control+Shift+Q');
  await win.waitForSelector('#sql.hidden', { state: 'attached' });
});

await step('shows a new-lines pill while not following and follows on click', async () => {
  await win.focus('#grid');
  await win.keyboard.press('Home');
  await win.waitForFunction(() => window.__ulv.state().active.follow === false);
  fs.appendFileSync(log, '2030-01-01 00:00:01,000 [main] INFO  com.acme.Tail - pill line one\n2030-01-01 00:00:02,000 [main] INFO  com.acme.Tail - pill line two\n');
  await win.waitForSelector('#newpill:not(.hidden)', { timeout: 10000 });
  assert.match(await win.locator('#newpill').innerText(), /^↓ [\d,]+ new$/);
  await shot('21-new-lines');
  await win.click('#newpill');
  await win.waitForSelector('#newpill.hidden', { state: 'attached' });
  await win.waitForSelector('.grid-rows .r:has-text("pill line two")');
  assert.ok(await win.locator('#btn-follow').evaluate((e) => e.classList.contains('on')));
  await win.click('#btn-follow'); // back off for the steps below
});

await step('Clear hides every entry so far', async () => {
  await win.focus('#grid');
  await win.keyboard.press('Control+k');
  await win.waitForFunction(() => /[\d,]+ cleared · Show/.test(document.querySelector('#status')?.textContent ?? ''), null, { timeout: 20000 });
  await win.waitForFunction(() => document.querySelectorAll('.grid-rows .r .c').length === 0);
  fs.appendFileSync(log, '2030-01-01 00:00:03,000 [main] WARN  com.acme.Tail - after clear\n');
  await win.waitForSelector('.grid-rows .r:has-text("after clear")', { timeout: 10000 });
  assert.equal(await win.locator('.grid-rows .r .c[data-col="message"]').count(), 1);
  await win.locator('#status .show-cleared').click();
  await win.waitForFunction(() => !/cleared/.test(document.querySelector('#status')?.textContent ?? '') && document.querySelectorAll('.grid-rows .r .c').length > 20, null, { timeout: 20000 });
});

await step('pins rows to a strip above the grid that stays while scrolling', async () => {
  await win.focus('#grid');
  await win.keyboard.press('Home');
  await win.keyboard.press('ArrowDown');
  await win.keyboard.press('ArrowDown');
  await win.keyboard.press('Control+p');
  await win.waitForSelector('#pins:not(.hidden) .pin');
  // the row menu pins too
  const row = win.locator('.grid-rows .r').nth(8);
  const rowIndex = Number(await row.getAttribute('data-row'));
  await row.locator('.c.msg').click({ button: 'right' });
  await win.locator('.ctxmenu .item', { hasText: 'Pin row' }).click();
  await win.waitForFunction(() => document.querySelectorAll('#pins .pin').length === 2);
  assert.match(await win.locator('#pins .pin').first().innerText(), /^📌\s*\d+\s/);
  // scrolled far away, the strip is still there and a click jumps back
  for (let k = 0; k < 6; k++) await win.keyboard.press('PageDown');
  await win.waitForFunction(() => window.__ulv.state().active.topRow > 60);
  assert.equal(await win.locator('#pins .pin').count(), 2);
  await shot('pinned-rows');
  await win.locator('#pins .pin').nth(1).click();
  await win.waitForFunction((r) => window.__ulv.state().active.focus === r, rowIndex);
  // saved per file
  await win.waitForFunction(async () => JSON.stringify(Object.values((await window.ulv.getSettings()).pins ?? {}).map((l) => l.length)) === '[2]');
});

await step('groups the timeline by logger and filters from the legend', async () => {
  // a tab of its own: the main log has lines from 2030 appended by now
  const groupLog = path.join(tmp, 'group.log');
  execFileSync(process.execPath, ['scripts/gen-sample.mjs', groupLog, '2']);
  await win.evaluate((p) => window.__ulv.openPaths([p], false), groupLog);
  await win.waitForFunction(() => document.querySelectorAll('.tab').length === 2);
  await win.waitForFunction(() => !/Indexing/.test(document.querySelector('#status')?.textContent ?? '') && document.querySelectorAll('.grid-rows .r .c').length > 0, null, { timeout: 30000 });
  await win.click('#histogram .hist-group');
  await win.locator('.ctxmenu .item', { hasText: 'Logger' }).click();
  await win.waitForFunction(() => window.__ulv.timelineGroups()?.keys.length === 5, null, { timeout: 20000 });
  assert.match(await win.locator('#histogram .hist-group').innerText(), /^Group by: Logger/);
  assert.equal(await win.locator('#histogram .hist-legend .lg').count(), 5);
  await shot('timeline-grouped');
  const key = await win.evaluate(() => window.__ulv.timelineGroups().keys[1]);
  assert.equal(await win.locator('#histogram .hist-legend .lg .k').nth(1).innerText(), key.split('.').pop()); // short names
  await win.locator('#histogram .hist-legend .lg').nth(1).click();
  await win.waitForFunction(() => /shown/.test(document.querySelector('#status')?.textContent ?? ''), null, { timeout: 20000 });
  assert.equal(await win.inputValue('#quick'), `logger==${key}`);
  const loggers = await win.locator('.grid-rows .r .c[data-col="logger"]').allInnerTexts();
  assert.ok(loggers.length > 0 && loggers.every((l) => l.trim() === key), `loggers: ${loggers.slice(0, 3)}`);
  // the grouping follows the filter: one logger left
  await win.waitForFunction(() => window.__ulv.timelineGroups()?.keys.length === 1, null, { timeout: 20000 });
  await win.fill('#quick', '');
  await win.waitForFunction(() => !/shown/.test(document.querySelector('#status')?.textContent ?? ''), null, { timeout: 20000 });
  await win.evaluate(() => window.__ulv.setHistogramGroup('level'));
  await win.waitForFunction(() => window.__ulv.timelineGroups() === null);
  await win.locator('.tab.active .x').click();
  await win.waitForFunction(() => document.querySelectorAll('.tab').length === 1);
  await win.waitForFunction(() => document.querySelectorAll('#pins .pin').length === 2);
});

await step('picks several loggers with Ctrl+click and a whole package', async () => {
  const fld = win.locator('#sidebar .fields-sec .fld', { hasText: 'logger' });
  if (!(await fld.evaluate((e) => e.classList.contains('open')))) await fld.click();
  const values = win.locator('#sidebar .fields-sec .fv:not(.pkg)', { hasText: 'com.acme.' });
  await values.first().waitFor();
  const a = (await values.nth(0).locator('.v').innerText()).trim();
  const b = (await values.nth(1).locator('.v').innerText()).trim();
  await values.nth(0).click({ modifiers: ['Control'] });
  await win.waitForFunction(() => /shown/.test(document.querySelector('#status')?.textContent ?? ''), null, { timeout: 20000 });
  await win.locator('#sidebar .fields-sec .fv:not(.pkg)', { hasText: b }).click({ modifiers: ['Control'] });
  await win.waitForFunction((q) => document.querySelector('#quick').value === q, `logger in (${a},${b})`);
  await win.waitForFunction(() => document.querySelectorAll('#sidebar .fields-sec .fv.picked').length === 2, null, { timeout: 20000 });
  const loggers = await win.locator('.grid-rows .r .c[data-col="logger"]').allInnerTexts();
  assert.ok(loggers.every((l) => l.trim() === a || l.trim() === b), `loggers: ${loggers.slice(0, 3)}`);
  await shot('field-picks');
  // the package node: everything under com.acme
  await win.locator('#sidebar .fields-sec .fv.pkg', { hasText: 'com.acme.*' }).click();
  await win.waitForFunction(() => document.querySelector('#quick').value === 'logger:com.acme.*');
  await win.waitForFunction(() => !/shown/.test(document.querySelector('#status')?.textContent ?? ''), null, { timeout: 20000 });
  await win.fill('#quick', '');
  await win.press('#quick', 'Enter');
});

await step('highlights a double-clicked word and a selection in the details on screen', async () => {
  const cell = win.locator('.grid-rows .r .c[data-col="thread"]', { hasText: 'main' }).first();
  await cell.dblclick({ position: { x: 12, y: 8 } });
  await win.waitForFunction(() => window.__ulv.autoHighlight() === 'main');
  const marked = await win.evaluate(() => CSS.highlights.get('auto-hl')?.size ?? 0);
  assert.ok(marked > 1, `marked ${marked}`);
  assert.match(await win.locator('#sidebar').innerText(), /Bookmarks \(1\)/i); // a word, not a bookmark
  await shot('auto-highlight');
  // a single click clears it
  await win.locator('.grid-rows .r .c.msg').nth(2).click();
  await win.waitForFunction(() => window.__ulv.autoHighlight() === null);
  // selecting text in the details pane highlights it until the selection clears
  await win.evaluate(() => {
    const pre = document.querySelector('#detail pre');
    const text = pre.firstChild;
    const r = document.createRange();
    const i = text.data.indexOf('[') + 1;
    r.setStart(text, i);
    r.setEnd(text, text.data.indexOf(']'));
    document.getSelection().removeAllRanges();
    document.getSelection().addRange(r);
  });
  await win.waitForFunction(() => !!window.__ulv.autoHighlight());
  await win.evaluate(() => document.getSelection().removeAllRanges());
  await win.waitForFunction(() => window.__ulv.autoHighlight() === null);
});

await step('opens source locations under the source roots in the editor', async () => {
  await win.fill('#quick', 'level:error');
  await win.waitForFunction(() => /shown/.test(document.querySelector('#status')?.textContent ?? ''), null, { timeout: 20000 });
  await win.locator('.grid-rows .r', { has: win.locator('.more') }).first().locator('.c.msg').click();
  const link = win.locator('#detail pre .src-link', { hasText: 'OrderController.java:42' });
  await link.waitFor();
  // no source roots yet: a toast says how to set them
  await link.click();
  await win.waitForFunction(() => /Editor and source roots/.test(document.querySelector('#toast')?.textContent ?? ''));
  assert.ok(!fs.existsSync(launchLog));
  const root = path.join(tmp, 'src', 'main', 'java');
  fs.mkdirSync(path.join(root, 'com', 'acme', 'api'), { recursive: true });
  fs.writeFileSync(path.join(root, 'com', 'acme', 'api', 'OrderController.java'), 'class OrderController {}\n');
  await win.focus('#grid');
  await win.keyboard.press('F1');
  await win.keyboard.type('source roots');
  await win.keyboard.press('Enter');
  await win.locator('.modal .src-roots').fill(path.join(tmp, 'missing') + '\n' + root);
  await win.locator('.modal .btn.primary', { hasText: 'Save' }).click();
  await win.waitForSelector('#modal.hidden', { state: 'attached' });
  await link.click();
  await win.waitForFunction(() => /Opening OrderController\.java:42/.test(document.querySelector('#toast')?.textContent ?? ''));
  const file = path.join(root, 'com', 'acme', 'api', 'OrderController.java');
  assert.deepEqual(fs.readFileSync(launchLog, 'utf-8').trim().split('\n'), [`vscode://file/${file.replace(/^\//, '')}:42`]);
  // and from an expanded entry
  await win.focus('#grid');
  await win.keyboard.press('ArrowRight');
  const inline = win.locator('.grid-rows .r.cur.open .rx .src-link', { hasText: 'OrderController.java:42' });
  await inline.waitFor();
  await shot('source-links');
  await inline.click();
  for (let k = 0; k < 50 && fs.readFileSync(launchLog, 'utf-8').trim().split('\n').length < 2; k++) await new Promise((r) => setTimeout(r, 100));
  await win.keyboard.press('ArrowLeft');
  await win.waitForSelector('.grid-rows .r.open', { state: 'detached' });
  assert.equal(fs.readFileSync(launchLog, 'utf-8').trim().split('\n').length, 2);
  await win.fill('#quick', '');
  await win.waitForFunction(() => !/shown/.test(document.querySelector('#status')?.textContent ?? ''), null, { timeout: 20000 });
});

await step('saves a workspace and opens it again', async () => {
  const wsFile = path.join(tmp, 'round trip.ulvw');
  await win.fill('#quick', 'ConnectionPool');
  await win.waitForFunction(() => /shown/.test(document.querySelector('#status')?.textContent ?? ''), null, { timeout: 20000 });
  await win.fill('#search', 'Acquired');
  await win.waitForFunction(() => /\d+ \/ [\d,]+/.test(document.querySelector('#search-count')?.textContent ?? ''));
  await win.evaluate((f) => window.__ulv.saveWorkspace(f), wsFile);
  const ws = JSON.parse(fs.readFileSync(wsFile, 'utf-8'));
  assert.equal(ws.version, 1);
  assert.equal(ws.tabs.length, 1);
  assert.deepEqual(ws.tabs[0].paths, [log]);
  assert.deepEqual(ws.tabs[0].relPaths, ['app.log']);
  assert.equal(ws.tabs[0].filter.query, 'ConnectionPool');
  assert.equal(ws.tabs[0].search.text, 'Acquired');
  assert.equal(ws.tabs[0].bookmarks.length, 1);
  assert.equal(ws.tabs[0].pins.length, 2);
  assert.equal(await win.title(), 'round trip.ulvw — UltraLogViewer');
  // close the tab and reset the boxes, then reopen
  await win.locator('.tab .x').click();
  await win.waitForFunction(() => document.querySelectorAll('.tab').length === 0);
  await win.fill('#quick', '');
  await win.fill('#search', '');
  await win.evaluate((f) => window.__ulv.openWorkspace(f), wsFile);
  await win.waitForFunction(() => document.querySelectorAll('.tab').length === 1);
  await win.waitForFunction(() => /shown/.test(document.querySelector('#status')?.textContent ?? ''), null, { timeout: 30000 });
  assert.equal(await win.inputValue('#quick'), 'ConnectionPool');
  assert.equal(await win.inputValue('#search'), 'Acquired');
  await win.waitForFunction(() => /Bookmarks \(1\)/i.test(document.querySelector('#sidebar')?.textContent ?? ''), null, { timeout: 20000 });
  await win.waitForFunction(() => document.querySelectorAll('#pins .pin').length === 2, null, { timeout: 20000 });
  await shot('22-workspace');
  await win.fill('#search', '');
  await win.press('#search', 'Escape');
});

await step('clears the filter and follows appended lines', async () => {
  await win.fill('#quick', '');
  await win.waitForFunction(() => !/shown/.test(document.querySelector('#status')?.textContent ?? ''), null, { timeout: 20000 });
  await win.click('#btn-follow');
  fs.appendFileSync(log, '2030-01-01 00:00:00,000 [main] ERROR com.acme.Tail - appended while following\n');
  await win.waitForSelector('.grid-rows .r:has-text("appended while following")', { timeout: 10000 });
  await shot('05-follow');
});

await step('duplicates a tab over the same file, filters it differently and splits a tab', async () => {
  const splitLog = path.join(tmp, 'split.log');
  const lines = Array.from({ length: 300 }, (_, i) => `2024-05-01 09:${String(Math.floor(i / 60)).padStart(2, '0')}:${String(i % 60).padStart(2, '0')},000 [main] ${i % 3 === 0 ? 'ERROR' : 'INFO '} com.acme.Split - entry ${i}`);
  fs.writeFileSync(splitLog, lines.join('\n') + '\n');
  const before = await win.locator('.tab').count();
  await win.evaluate((p) => window.__ulv.openPaths([p], false), splitLog);
  await win.waitForFunction(() => /300 entries/.test(document.querySelector('#status')?.textContent ?? '') && !/Indexing/.test(document.querySelector('#status')?.textContent ?? ''), null, { timeout: 20000 });
  const counts = () => win.evaluate(() => window.__ulv.state().tabs.filter((t) => t.paths[0].endsWith('split.log')).map((t) => [t.info.viewCount, t.info.total]));

  // Duplicate: opens at once over the same index, then gets its own filter.
  await win.focus('#grid');
  await win.keyboard.press('Control+Shift+T');
  await win.waitForFunction((n) => document.querySelectorAll('.tab').length === n + 2, before);
  assert.equal(await win.locator('.tab.active .name').innerText(), 'split.log (2)');
  await win.fill('#quick', 'level:error');
  await win.waitForFunction(() => /100 shown/.test(document.querySelector('#status')?.textContent ?? ''), null, { timeout: 20000 });
  assert.deepEqual(await counts(), [[300, 300], [100, 300]]);
  // The tail reads the file once and both tabs see the new entry.
  fs.appendFileSync(splitLog, '2024-05-01 09:05:00,000 [main] ERROR com.acme.Split - appended to both\n');
  await win.waitForFunction(() => window.__ulv.state().tabs.filter((t) => t.paths[0].endsWith('split.log')).every((t) => t.info.total === 301), null, { timeout: 10000 });
  await win.waitForFunction(() => /101 shown/.test(document.querySelector('#status')?.textContent ?? ''), null, { timeout: 10000 });
  assert.deepEqual(await counts(), [[301, 301], [101, 301]]);
  await win.locator('.tab', { hasText: 'split.log (2)' }).locator('.x').click();
  await win.waitForFunction((n) => document.querySelectorAll('.tab').length === n + 1, before);
  assert.match(await status(), /301 entries/);

  // Split right: a second grid with its own quick filter; selecting in one pane moves the other to the same entry.
  await win.focus('#grid');
  await win.keyboard.press('Control+Backslash');
  await win.waitForSelector('#pane-split:not(.hidden) #grid2 .grid-rows .r .c', { timeout: 10000 });
  await win.fill('#head-main .pane-quick', 'level:error');
  await win.waitForFunction(() => document.querySelector('#head-main .pane-count')?.textContent === '101 of 301', null, { timeout: 10000 });
  assert.equal(await win.locator('#head-split .pane-count').innerText(), '301');
  await win.locator('#grid .grid-rows .r', { hasText: 'entry 30' }).first().click();
  await win.waitForFunction(() => window.__ulv.state().tabs.find((t) => t.split)?.split.focus === 30, null, { timeout: 10000 });
  await win.waitForSelector('#grid2 .grid-rows .r.sel', { timeout: 5000 });
  const picked = await win.evaluate(() => {
    const [main] = window.__ulv.state().tabs.filter((t) => t.split);
    return [main.focus, main.split.focus, main.info.viewCount, main.split.info.viewCount];
  });
  assert.equal(picked[1], 30, `right pane focus ${picked}`);
  await shot('26-split-view');
  await win.click('#head-split .pane-close');
  await win.waitForSelector('#pane-split.hidden', { state: 'attached' });
  await win.locator('.tab.active .x').click();
  await win.waitForFunction((n) => document.querySelectorAll('.tab').length === n, before);
});

await step('merges two files into one timeline', async () => {
  await win.evaluate(([a, b]) => window.__ulv.openPaths([a, b], true), [log, other]);
  await win.waitForFunction(() => document.querySelectorAll('.tab').length === 2);
  await win.waitForFunction(() => [...document.querySelectorAll('.grid-header .h')].some((h) => h.textContent === 'Source'), null, { timeout: 30000 });
  await win.waitForSelector('.grid-rows .r:has-text("from the other file")', { timeout: 30000 });
  await shot('06-merged');
});

await step('names the detected format and opens plain text without timestamps', async () => {
  assert.match(await win.locator('#status .fmt').innerText(), /Timestamped text\s*auto/);
  await win.evaluate((p) => window.__ulv.openPaths([p], false), notes);
  await win.waitForFunction(() => document.querySelectorAll('.tab').length === 3);
  await win.waitForSelector('.grid-rows .r:has-text("could not copy")', { timeout: 20000 });
  await win.waitForFunction(() => /7 entries/.test(document.querySelector('#status')?.textContent ?? ''), null, { timeout: 20000 });
  assert.match(await win.locator('#status .fmt').innerText(), /Plain text\s*auto/);
  assert.match(await win.locator('#status .fmt').getAttribute('title'), /no known log layout, so each line is one entry/);
  assert.equal(await win.locator('#format option[value=""]').innerText(), 'Auto: Plain text');
  assert.deepEqual(await win.locator('.grid-header .h').allInnerTexts(), ['Level', 'Message']);
  assert.equal((await win.locator('.grid-rows .r:has-text("could not copy") .c[data-col="level"]').innerText()).trim(), 'ERROR');
  await win.waitForSelector('#histogram.hidden', { state: 'attached' });
  assert.ok(await win.locator('#btn-hist').isDisabled());
  await win.focus('#grid');
  await win.keyboard.press('Control+Shift+H');
  await win.waitForTimeout(200);
  assert.ok(await win.locator('#btn-hist').evaluate((b) => b.classList.contains('on')), 'Ctrl+Shift+H must not turn the timeline preference off');
  assert.match(await win.locator('#sidebar').innerText(), /No timestamps in this file/);
  await shot('24-plain-text');
  await win.selectOption('#format', 'timestamped');
  await win.waitForFunction(() => !/auto/.test(document.querySelector('#status .fmt')?.textContent ?? ''), null, { timeout: 20000 });
  assert.match(await win.locator('#status .fmt').getAttribute('title'), /chosen by you; 0 of 6 sampled lines start an entry/);
  await win.selectOption('#format', '');
  await win.waitForFunction(() => /Plain text\s*auto/.test(document.querySelector('#status .fmt')?.textContent ?? ''), null, { timeout: 20000 });
});

await step('opens only a time window, says so, and loads the whole file', async () => {
  const labels = (await win.evaluate(() => window.ulv.menuItems())).map((m) => m.label);
  for (const l of ['File › Open last…', 'File › Open time range…', 'File › Use index cache', 'File › Clear index cache']) assert.ok(labels.includes(l), `menu item ${l}`);
  const windowLog = path.join(tmp, 'window.log');
  execFileSync(process.execPath, ['scripts/gen-sample.mjs', windowLog, '3']);
  await win.evaluate((p) => window.__ulv.openPaths([p], false, { last: 600_000 }), windowLog);
  await win.waitForFunction(() => document.querySelectorAll('.tab').length === 4);
  await win.waitForSelector('#partial-banner:not(.hidden)', { timeout: 30000 });
  assert.match(await win.locator('#partial-banner .msg').innerText(), /^Showing \d{4}-\d{2}-\d{2} \d{2}:\d{2}–\d{2}:\d{2} · partial file · following new lines$/);
  assert.match(await win.locator('#sidebar .sources').innerText(), /window\.log[\s\S]*partial/);
  const partial = Number((await status()).match(/([\d,]+) entries/)[1].replace(/,/g, ''));
  await shot('25-partial');
  await win.click('#partial-whole');
  await win.waitForSelector('#partial-banner.hidden', { state: 'attached', timeout: 30000 });
  await win.waitForFunction((n) => {
    const m = document.querySelector('#status')?.textContent?.match(/([\d,]+) entries/);
    return !!m && Number(m[1].replace(/,/g, '')) > n && !/Indexing/.test(document.querySelector('#status')?.textContent ?? '');
  }, partial, { timeout: 30000 });
});

await step('dark theme renders', async () => {
  await win.click('#btn-theme');
  await shot('07-dark');
});

await step('empty state fills the window with the status bar at the bottom', async () => {
  while ((await win.locator('.tab .x').count()) > 0) await win.locator('.tab .x').first().click();
  await win.waitForSelector('#empty:not(.hidden)');
  const { bottom, height, main } = await win.evaluate(() => ({
    bottom: document.querySelector('#status').getBoundingClientRect().bottom,
    height: window.innerHeight,
    main: document.querySelector('.main').getBoundingClientRect().height,
  }));
  assert.ok(Math.abs(bottom - height) <= 1, `status bar bottom ${bottom} != window height ${height}`);
  assert.ok(main > height * 0.7, `main area only ${main}px of ${height}px`);
  await shot('23-empty');
});

await app.close();
console.log(`Screenshots in ${out}`);
