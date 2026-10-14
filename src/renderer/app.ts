import { validatePattern } from '../core/query';
import { CustomFormat, DocInfo, Level, RowData, TextPattern } from '../core/types';
import { engine, EngineEvent, ulv } from './api';
import { DetailView } from './detail';
import { Grid } from './grid';
import { HistogramView } from './histogram';
import { Sidebar } from './sidebar';
import { Bookmark, DEFAULT_HIGHLIGHTS, defaultFilter, Settings, Tab, uid } from './state';
import { $, ask, debounce, esc, fmtBytes, fmtNum, h, modal, toast } from './util';

let settings: Settings = {};
const tabs: Tab[] = [];
let active: Tab | null = null;

const searchInput = $<HTMLInputElement>('#search');
const quickInput = $<HTMLInputElement>('#quick');
const formatSel = $<HTMLSelectElement>('#format');
const toggles = { case: $('#s-case'), word: $('#s-word'), regex: $('#s-regex') };

const saveSettings = debounce(() => void ulv().setSettings(settings as Record<string, unknown>), 300);

// ---------------------------------------------------------------- components

const grid = new Grid($('#grid'), {
  highlights: () => settings.highlights ?? [],
  columnWidth: (col) => (col.width === 0 ? 0 : settings.columnWidths?.[col.key] ?? col.width),
  hiddenColumns: () => settings.hiddenColumns ?? [],
  onHeaderMenu: () => editColumns(),
  setColumnWidth: (key, w) => {
    (settings.columnWidths ??= {})[key] = w;
    saveSettings();
  },
  onSelectionChange: () => {
    void updateDetail();
    renderStatus();
  },
  onTopRowChange: () => {
    updateHistogramViewport();
    renderStatus();
  },
  onKey: gridKey,
  toggleBookmark: (row) => void toggleBookmark(row),
  copyRows: (a, b) => void copyRows(a, b),
});

const sidebar = new Sidebar($('#sidebar'), {
  settings: () => settings,
  saveSettings,
  applyFilter: () => active && void applyFilter(active),
  highlightsChanged: () => {
    grid.refreshPatterns();
    grid.render();
  },
  jumpToBookmark: (b) => void jumpToBookmark(b),
  removeBookmark: (b) => active && void removeBookmark(active, b.key),
  bookmarkNote: (b) => void editNote(b),
});

const detail = new DetailView($('#detail'), (height) => {
  settings.detailHeight = height;
  saveSettings();
});

const histogram = new HistogramView($('#histogram'), {
  jumpToTime: async (t) => {
    if (!active) return;
    grid.select(await engine.rowForTime(active.id, t), false, 'center');
  },
  setTimeRange: (from, to) => {
    if (!active) return;
    active.filter.timeFrom = from;
    active.filter.timeTo = to;
    void applyFilter(active);
  },
});

// ---------------------------------------------------------------------- tabs

function placeholderInfo(id: number, title: string): DocInfo {
  return { id, title, sources: [], columns: [], total: 0, viewCount: 0, levelCounts: [0, 0, 0, 0, 0, 0, 0], indexing: true, progress: 0, timeMin: null, timeMax: null, merged: false };
}

async function openPaths(paths: string[], merge: boolean) {
  if (!paths.length) return;
  const groups = merge ? [paths] : paths.map((p) => [p]);
  for (const g of groups) {
    const id = await engine.open(g, { customFormats: settings.customFormats });
    const title = g.length === 1 ? g[0].split(/[\\/]/).pop()! : `${g[0].split(/[\\/]/).pop()} + ${g.length - 1}`;
    const tab: Tab = {
      id,
      paths: g,
      info: placeholderInfo(id, title),
      filter: defaultFilter(),
      search: { pattern: currentPattern(), result: null, current: 0 },
      bookmarks: new Map(),
      anchor: 0,
      focus: 0,
      topRow: 0,
      follow: false,
      formatId: '',
    };
    tabs.push(tab);
    activate(tab);
  }
  void ulv().addRecent(paths);
  settings.recent = [...paths, ...(settings.recent ?? []).filter((p) => !paths.includes(p))].slice(0, 15);
}

function activate(tab: Tab | null) {
  active = tab;
  quickInput.value = tab?.filter.query ?? '';
  formatSel.value = tab?.formatId ?? '';
  grid.setTab(tab);
  renderTabs();
  sidebar.render(tab);
  $('#empty').classList.toggle('hidden', !!tab);
  if (tab) {
    void refreshOverview(tab);
    void updateDetail();
  } else {
    detail.render(null, undefined);
    histogram.set(null);
    renderRecent();
  }
  renderStatus();
  renderToolbar();
}

async function closeTab(tab: Tab) {
  const i = tabs.indexOf(tab);
  if (i < 0) return;
  tabs.splice(i, 1);
  await engine.close(tab.id);
  if (active === tab) activate(tabs[Math.min(i, tabs.length - 1)] ?? null);
  else renderTabs();
}

function renderTabs() {
  const el = $('#tabs');
  el.innerHTML = '';
  for (const t of tabs) {
    const live = t.info.sources.some((s) => s.live);
    const tabEl = h(
      'div',
      { class: t === active ? 'tab active' : 'tab', title: t.paths.join('\n'), onclick: () => activate(t) },
      h('span', { class: live ? 'dot' : 'dot off', title: live ? 'Live: new lines appear automatically' : 'Static (compressed or converted file)' }),
      h('span', { class: 'name' }, t.info.title),
      h('button', { class: 'x', title: 'Close (Ctrl+W)', onclick: (e: Event) => { e.stopPropagation(); void closeTab(t); } }, '×'),
    );
    tabEl.addEventListener('auxclick', (e) => e.button === 1 && void closeTab(t));
    el.append(tabEl);
  }
}

function renderRecent() {
  const el = $('#recent');
  el.innerHTML = '';
  const recent = settings.recent ?? [];
  if (!recent.length) return;
  el.append(h('div', { class: 'muted', style: 'margin-bottom:4px' }, 'Recent'));
  for (const p of recent.slice(0, 8)) el.append(h('a', { title: p, onclick: () => void openPaths([p], false) }, p));
}

// --------------------------------------------------------------- engine events

const refreshOverviewSoon = debounce((tab: Tab) => void refreshOverview(tab), 250);
const rerunSearchSoon = debounce((tab: Tab) => void runSearch(tab, false), 800);

window.ulv.onEvent((raw) => {
  const ev = raw as EngineEvent;
  const tab = tabs.find((t) => t.id === ev.docId);
  if (!tab) return;
  if (ev.type === 'progress') {
    tab.progress = ev.fraction >= 1 ? undefined : { phase: ev.phase, fraction: ev.fraction };
    if (tab === active) renderStatus();
    return;
  }
  if (ev.type === 'doc-error') {
    tab.error = ev.message;
    toast(ev.message, true);
    return;
  }
  const wasIndexing = tab.info.indexing;
  tab.info = ev.info;
  if (wasIndexing && !ev.info.indexing) void onDocReady(tab);
  if (tab !== active) {
    renderTabs();
    return;
  }
  if (ev.reset) grid.renderHeader();
  if (tab.follow) tab.topRow = Number.MAX_SAFE_INTEGER;
  grid.invalidate();
  if (tab.follow && tab.info.viewCount) {
    tab.focus = tab.anchor = tab.info.viewCount - 1;
    void updateDetail();
  }
  refreshOverviewSoon(tab);
  if (tab.search.result && !ev.info.indexing) rerunSearchSoon(tab);
  if (!$('#sidebar').contains(document.activeElement)) sidebar.render(tab);
  renderStatus();
  renderTabs();
});

async function onDocReady(tab: Tab) {
  tab.progress = undefined;
  if (tab.paths.length === 1) {
    const saved = settings.bookmarks?.[tab.paths[0]] ?? [];
    for (const s of saved) {
      const key = await engine.keyForLine(tab.id, 0, s.line);
      if (!key) continue;
      const rows = await engine.rows(tab.id, (await engine.rowForKey(tab.id, key)) ?? 0, 1);
      tab.bookmarks.set(key, { key, line: s.line, source: 0, note: s.note, preview: firstLine(rows[0]?.fields.message || rows[0]?.text || '') });
    }
    if (saved.length) await resolveBookmarkRows(tab);
  }
  if (!isDefaultFilter(tab)) await applyFilter(tab);
  if (tab.search.pattern.text) void runSearch(tab, false);
  if (tab === active) {
    sidebar.render(tab);
    void updateDetail();
  }
}

// -------------------------------------------------------------------- filter

const isDefaultFilter = (t: Tab) =>
  t.filter.levels.every(Boolean) && !t.filter.rules.length && !t.filter.query && t.filter.timeFrom == null && t.filter.timeTo == null && !t.filter.bookmarksOnly;

async function applyFilter(tab: Tab) {
  const focusKey = grid.getCached(tab.focus)?.key;
  tab.filter.bookmarks = [...tab.bookmarks.keys()];
  let info: DocInfo | null;
  try {
    info = await engine.setFilter(tab.id, tab.filter);
  } catch (e) {
    toast((e as Error).message, true);
    return;
  }
  if (!info) return; // superseded by a newer filter
  tab.info = info;
  tab.progress = undefined;
  const row = focusKey ? await engine.rowForKey(tab.id, focusKey) : null;
  tab.anchor = tab.focus = row ?? Math.min(tab.focus, Math.max(0, info.viewCount - 1));
  await resolveBookmarkRows(tab);
  if (tab !== active) return;
  grid.invalidate();
  grid.reveal(tab.focus, 'center');
  sidebar.render(tab);
  renderStatus();
  void refreshOverview(tab);
  void updateDetail();
  if (tab.search.pattern.text) void runSearch(tab, false);
}

async function refreshOverview(tab: Tab) {
  if (tab !== active) return;
  const [ov, hist] = await Promise.all([
    engine.overview(tab.id, Math.max(50, Math.floor($('#grid .grid-body').clientHeight))),
    settings.showHistogram !== false ? engine.histogram(tab.id, histogram.bucketsWanted) : Promise.resolve(null),
  ]).catch(() => [[], null] as const);
  if (tab !== active) return;
  grid.setOverview([...ov]);
  histogram.set(hist);
  updateHistogramViewport();
}

function updateHistogramViewport() {
  const t = active;
  if (!t) return;
  const a = grid.getCached(t.topRow)?.time;
  const b = grid.getCached(Math.min(t.info.viewCount - 1, t.topRow + grid.visibleRows()))?.time;
  histogram.setViewport(a != null && b != null ? [a, b] : null);
}

// -------------------------------------------------------------------- search

function currentPattern(): TextPattern {
  return {
    text: searchInput.value,
    caseSensitive: toggles.case.classList.contains('on'),
    wholeWord: toggles.word.classList.contains('on'),
    regex: toggles.regex.classList.contains('on'),
  };
}

async function runSearch(tab: Tab, jump: boolean) {
  const pattern = currentPattern();
  tab.search.pattern = pattern;
  const err = pattern.text ? validatePattern(pattern) : null;
  searchInput.parentElement!.classList.toggle('error', !!err);
  if (!pattern.text || err) {
    tab.search.result = null;
    grid.refreshPatterns();
    grid.render();
    renderSearchCount();
    return;
  }
  const result = await engine.search(tab.id, pattern);
  if (result.cancelled || tab.search.pattern !== pattern) return;
  tab.search.result = result;
  tab.progress = undefined;
  const idx = result.rows.findIndex((r) => r >= tab.focus);
  tab.search.current = idx < 0 ? 0 : idx;
  if (tab !== active) return;
  grid.refreshPatterns();
  if (jump && result.rows.length) grid.select(result.rows[tab.search.current], false, 'center');
  else grid.render();
  renderSearchCount();
  renderStatus();
}

function stepSearch(dir: 1 | -1) {
  const t = active;
  const rows = t?.search.result?.rows;
  if (!t || !rows?.length) {
    if (t && searchInput.value) void runSearch(t, true);
    return;
  }
  // continue from the selected row, not just the last hit
  let i: number;
  if (dir > 0) {
    i = rows.findIndex((r) => r > t.focus);
    if (i < 0) i = 0;
  } else {
    i = -1;
    for (let k = rows.length - 1; k >= 0; k--)
      if (rows[k] < t.focus) {
        i = k;
        break;
      }
    if (i < 0) i = rows.length - 1;
  }
  t.search.current = i;
  grid.select(rows[i], false, 'center');
  renderSearchCount();
}

function renderSearchCount() {
  const r = active?.search.result;
  $('#search-count').textContent = !r ? '' : r.total === 0 ? 'No matches' : `${fmtNum(active!.search.current + 1)} / ${fmtNum(r.total)}${r.truncated ? '+' : ''}`;
}

const searchSoon = debounce(() => active && void runSearch(active, true), 250);
searchInput.addEventListener('input', searchSoon);
searchInput.addEventListener('keydown', (e) => {
  if (e.key === 'Enter') {
    e.preventDefault();
    stepSearch(e.shiftKey ? -1 : 1);
  } else if (e.key === 'Escape') {
    searchInput.value = '';
    if (active) void runSearch(active, false);
    $('#grid').focus();
  }
});
for (const [, el] of Object.entries(toggles))
  el.addEventListener('click', () => {
    el.classList.toggle('on');
    if (active) void runSearch(active, true);
  });
$('#s-next').onclick = () => stepSearch(1);
$('#s-prev').onclick = () => stepSearch(-1);
$('#s-filter').onclick = () => {
  if (!active || !searchInput.value) return;
  const p = currentPattern();
  active.filter.rules.push({ ...p, id: uid(), enabled: true, mode: 'include' });
  void applyFilter(active);
  toast('Search added as an include filter');
};

const quickSoon = debounce(() => {
  if (!active) return;
  active.filter.query = quickInput.value;
  void applyFilter(active);
}, 300);
quickInput.addEventListener('input', quickSoon);
quickInput.addEventListener('keydown', (e) => {
  if (e.key === 'Escape') {
    quickInput.value = '';
    quickSoon();
    $('#grid').focus();
  }
});

// ----------------------------------------------------------------- bookmarks

const firstLine = (s: string) => {
  const i = s.indexOf('\n');
  return (i < 0 ? s : s.slice(0, i)).slice(0, 160);
};

function persistBookmarks(tab: Tab) {
  if (tab.paths.length !== 1) return;
  settings.bookmarks ??= {};
  const list = [...tab.bookmarks.values()].map((b) => ({ line: b.line, note: b.note }));
  if (list.length) settings.bookmarks[tab.paths[0]] = list;
  else delete settings.bookmarks[tab.paths[0]];
  saveSettings();
}

async function resolveBookmarkRows(tab: Tab) {
  await Promise.all([...tab.bookmarks.values()].map(async (b) => (b.row = await engine.rowForKey(tab.id, b.key))));
}

async function toggleBookmark(row: RowData) {
  const t = active;
  if (!t) return;
  if (t.bookmarks.has(row.key)) return removeBookmark(t, row.key);
  t.bookmarks.set(row.key, { key: row.key, line: row.line, source: row.source, preview: firstLine(row.fields.message || row.text), note: '', row: row.row });
  persistBookmarks(t);
  grid.render();
  sidebar.render(t);
}

async function removeBookmark(t: Tab, key: string) {
  t.bookmarks.delete(key);
  persistBookmarks(t);
  if (t.filter.bookmarksOnly) await applyFilter(t);
  grid.render();
  sidebar.render(t);
}

async function editNote(b: Bookmark) {
  const note = await ask('Bookmark note', { value: b.note, placeholder: 'What is interesting about this line?' });
  if (note === null || !active) return;
  b.note = note;
  persistBookmarks(active);
  sidebar.render(active);
  void updateDetail();
}

async function jumpToBookmark(b: Bookmark) {
  const t = active;
  if (!t) return;
  const row = await engine.rowForKey(t.id, b.key);
  if (row === null) return toast('That bookmark is hidden by the current filter');
  grid.select(row, false, 'center');
  $('#grid').focus();
}

function stepBookmark(dir: 1 | -1) {
  const t = active;
  if (!t) return;
  const rows = [...t.bookmarks.values()].map((b) => b.row).filter((r): r is number => r !== null && r !== undefined).sort((a, b) => a - b);
  if (!rows.length) return toast('No bookmarks in this view');
  const next = dir > 0 ? rows.find((r) => r > t.focus) ?? rows[0] : [...rows].reverse().find((r) => r < t.focus) ?? rows[rows.length - 1];
  grid.select(next, false, 'center');
}

// ------------------------------------------------------------------- actions

async function stepLevel(dir: 1 | -1, level: Level) {
  const t = active;
  if (!t) return;
  const r = await engine.findLevel(t.id, t.focus, dir, level);
  if (r === null) return toast(`No more ${level === Level.Warn ? 'warnings' : 'errors'} ${dir > 0 ? 'below' : 'above'}`);
  grid.select(r, false, 'center');
}

async function gotoLine() {
  const t = active;
  if (!t) return;
  const hint = t.info.merged ? `Line number, or source:line (sources: ${t.info.sources.map((s, i) => `${i + 1}=${s.displayName}`).join(', ')})` : undefined;
  const v = await ask('Go to line', { placeholder: 'Line number', hint });
  if (!v) return;
  const m = /^(?:(\d+):)?(\d+)$/.exec(v.trim());
  if (!m) return toast('Enter a line number', true);
  const src = m[1] ? Number(m[1]) - 1 : 0;
  grid.select(await engine.rowForLine(t.id, src, Number(m[2])), false, 'center');
  $('#grid').focus();
}

async function copyRows(a: number, b: number) {
  const t = active;
  if (!t || !t.info.viewCount) return;
  const MAX = 100_000;
  const end = Math.min(b, a + MAX - 1);
  const parts: string[] = [];
  for (let r = a; r <= end; r += 5000) {
    const rows = await engine.rows(t.id, r, Math.min(5000, end - r + 1));
    for (const row of rows) parts.push(row.text);
  }
  await navigator.clipboard.writeText(parts.join('\n'));
  toast(`Copied ${fmtNum(parts.length)} ${parts.length === 1 ? 'entry' : 'entries'}${b > end ? ` (limited to ${fmtNum(MAX)})` : ''}`);
}

async function exportView() {
  const t = active;
  if (!t) return;
  const file = await ulv().saveDialog(`${t.info.title.replace(/\.(log|txt|gz)$/i, '')}-view.log`);
  if (!file) return;
  const fmt = /\.csv$/i.test(file) ? 'csv' : /\.jsonl?$/i.test(file) ? 'jsonl' : 'text';
  try {
    const n = await engine.exportView(t.id, file, fmt);
    t.progress = undefined;
    renderStatus();
    toast(`Exported ${fmtNum(n)} entries`);
  } catch (e) {
    toast((e as Error).message, true);
  }
}

function toggleFollow() {
  const t = active;
  if (!t) return;
  t.follow = !t.follow;
  if (t.follow && t.info.viewCount) {
    grid.select(t.info.viewCount - 1);
    grid.scrollTo(Number.MAX_SAFE_INTEGER);
    t.follow = true;
  }
  renderToolbar();
  renderStatus();
}

function gridKey(e: KeyboardEvent): boolean {
  if (e.ctrlKey || e.metaKey || e.altKey) return false;
  const t = active;
  if (!t) return false;
  switch (e.key) {
    case 'e':
      void stepLevel(1, Level.Error);
      return true;
    case 'E':
      void stepLevel(-1, Level.Error);
      return true;
    case 'w':
      void stepLevel(1, Level.Warn);
      return true;
    case 'W':
      void stepLevel(-1, Level.Warn);
      return true;
    case 'n':
      stepSearch(1);
      return true;
    case 'N':
      stepSearch(-1);
      return true;
    case 'b':
    case 'm': {
      const row = grid.getCached(t.focus);
      if (row) void toggleBookmark(row);
      return true;
    }
    case 'f':
      toggleFollow();
      return true;
    case '/':
      searchInput.focus();
      searchInput.select();
      return true;
    case 'Enter':
      if (settings.showDetail === false) toggleLayout('showDetail');
      return true;
  }
  return false;
}

// ------------------------------------------------------------------- details

let detailToken = 0;
async function updateDetail() {
  const t = active;
  if (settings.showDetail === false) return;
  if (!t || !t.info.viewCount) return detail.render(t, undefined);
  const token = ++detailToken;
  const get = async (r: number) => grid.getCached(r) ?? (await engine.rows(t.id, r, 1))[0];
  const lo = Math.min(t.anchor, t.focus), hi = Math.max(t.anchor, t.focus);
  const [first, last] = await Promise.all([get(lo), lo === hi ? Promise.resolve(undefined) : get(hi)]);
  if (token !== detailToken) return;
  detail.render(t, t.anchor === t.focus ? first : (t.focus === lo ? first : last) ?? first, last);
}

// -------------------------------------------------------------------- status

function renderStatus() {
  const el = $('#status');
  const t = active;
  if (!t) {
    el.innerHTML = '<span>Ready</span><span class="sp"></span><span class="muted">F1 for shortcuts</span>';
    return;
  }
  const i = t.info;
  const size = i.sources.reduce((n, s) => n + s.size, 0);
  const lines = i.sources.reduce((n, s) => n + s.lines, 0);
  const src = i.sources[0];
  const parts: string[] = [];
  if (src) parts.push(`<span title="Detected format">${esc(src.format)}</span>`, `<a class="enc" title="Change encoding">${esc(src.encoding)}</a>${src.compressed ? '<span>gzip</span>' : ''}`);
  parts.push(`<span>${fmtBytes(size)}</span>`, `<span>${fmtNum(lines)} lines · ${fmtNum(i.total)} entries</span>`);
  if (i.viewCount !== i.total) parts.push(`<span><b>${fmtNum(i.viewCount)}</b> shown</span>`);
  const sel = Math.abs(t.focus - t.anchor) + 1;
  if (i.viewCount) parts.push(`<span>Row ${fmtNum(t.focus + 1)}${sel > 1 ? ` · ${fmtNum(sel)} selected` : ''}</span>`);
  parts.push('<span class="sp"></span>');
  const busy = t.progress ?? (i.indexing ? { phase: 'index', fraction: i.progress } : undefined);
  if (busy) {
    const label = { index: 'Indexing', filter: 'Filtering', search: 'Searching', export: 'Exporting' }[busy.phase] ?? busy.phase;
    parts.push(`<span>${label} ${Math.round(busy.fraction * 100)}%</span><span class="prog"><i style="width:${(busy.fraction * 100).toFixed(1)}%"></i></span>`);
  }
  if (i.sources.some((s) => s.live)) parts.push(`<span class="live">● live${t.follow ? ' · following' : ''}</span>`);
  el.innerHTML = parts.join('');
  el.querySelector('.enc')?.addEventListener('click', chooseEncoding);
}

function renderToolbar() {
  $('#btn-follow').classList.toggle('on', !!active?.follow);
  $('#btn-hist').classList.toggle('on', settings.showHistogram !== false);
  $('#btn-detail').classList.toggle('on', settings.showDetail !== false);
}

// -------------------------------------------------------------------- layout

type LayoutKey = 'showHistogram' | 'showDetail' | 'showSidebar';
function applyLayout() {
  $('#histogram').classList.toggle('hidden', settings.showHistogram === false);
  $('#detail').classList.toggle('hidden', settings.showDetail === false);
  $('#sidebar').classList.toggle('hidden', settings.showSidebar === false);
  if (settings.detailHeight) $('#detail').style.height = `${settings.detailHeight}px`;
  renderToolbar();
}

function toggleLayout(key: LayoutKey) {
  settings[key] = settings[key] === false;
  saveSettings();
  applyLayout();
  if (active) {
    void refreshOverview(active);
    void updateDetail();
  }
}

function applyTheme() {
  const dark = settings.theme === 'dark' || (settings.theme !== 'light' && matchMedia('(prefers-color-scheme: dark)').matches);
  document.documentElement.dataset.theme = dark ? 'dark' : 'light';
  grid.render();
  if (active) void refreshOverview(active);
}

// ------------------------------------------------------------------- formats

function renderFormatOptions() {
  const builtins: [string, string][] = [
    ['', 'Format: auto'],
    ['timestamped', 'Timestamped text'],
    ['json', 'JSON lines'],
    ['syslog', 'Syslog RFC 3164'],
    ['syslog5424', 'Syslog RFC 5424'],
    ['access', 'Apache / Nginx'],
    ['logcat', 'Android logcat'],
    ['plain', 'Plain text'],
  ];
  formatSel.innerHTML = '';
  for (const [v, l] of builtins) formatSel.append(h('option', { value: v }, l));
  for (const c of settings.customFormats ?? []) formatSel.append(h('option', { value: `custom:${c.name}` }, `Custom: ${c.name}`));
  formatSel.append(h('option', { value: '__new' }, '+ Custom format…'));
  formatSel.value = active?.formatId ?? '';
}

formatSel.onchange = async () => {
  const t = active;
  if (!t) return;
  if (formatSel.value === '__new') {
    formatSel.value = t.formatId;
    return editCustomFormat();
  }
  t.formatId = formatSel.value;
  await reformat(t);
};

async function reformat(t: Tab) {
  t.info = await engine.reformat(t.id, t.formatId || undefined, settings.customFormats);
  grid.setTab(t);
  sidebar.render(t);
  renderStatus();
  void refreshOverview(t);
}

function editCustomFormat() {
  const sample = active ? grid.getCached(active.focus)?.text.split('\n')[0] ?? '' : '';
  modal((box, close) => {
    const name = h('input', { type: 'text', placeholder: 'Name, e.g. My service' });
    const pattern = h('input', { type: 'text', placeholder: '^(?<timestamp>\\S+ \\S+) (?<level>\\w+) (?<message>.*)$', spellcheck: false });
    const test = h('input', { type: 'text', value: sample, spellcheck: false });
    const out = h('pre', { style: 'font:12px var(--mono);background:var(--bg-2);padding:8px;border-radius:6px;min-height:40px;white-space:pre-wrap' });
    const check = () => {
      try {
        const m = new RegExp(pattern.value).exec(test.value);
        out.textContent = m ? JSON.stringify(m.groups ?? {}, null, 2) : 'No match';
      } catch (e) {
        out.textContent = (e as Error).message;
      }
    };
    pattern.oninput = check;
    test.oninput = check;
    const save = async () => {
      if (!name.value || !pattern.value) return toast('Name and pattern are required', true);
      try {
        new RegExp(pattern.value);
      } catch (e) {
        return toast((e as Error).message, true);
      }
      const def: CustomFormat = { name: name.value, pattern: pattern.value };
      settings.customFormats = [...(settings.customFormats ?? []).filter((c) => c.name !== def.name), def];
      saveSettings();
      renderFormatOptions();
      close();
      if (active) {
        active.formatId = `custom:${def.name}`;
        formatSel.value = active.formatId;
        await reformat(active);
      }
    };
    box.append(
      h('h2', {}, 'Custom log format'),
      h('div', { class: 'hint' }, 'A regular expression with named groups. Use timestamp, level, thread, logger and message for the standard columns; any other group becomes its own column. Lines that don’t match are attached to the previous entry (stack traces).'),
      name,
      h('div', { style: 'height:6px' }),
      pattern,
      h('div', { class: 'hint' }, 'Test line'),
      test,
      out,
      h('div', { class: 'actions' }, h('button', { class: 'btn', onclick: close }, 'Cancel'), h('button', { class: 'btn primary', onclick: save }, 'Save and apply')),
    );
    check();
  });
}

function editColumns() {
  const t = active;
  if (!t) return;
  modal((box, close) => {
    const hidden = new Set(settings.hiddenColumns ?? []);
    const list = h('div', { class: 'form' });
    for (const c of t.info.columns) {
      const cb = h('input', { type: 'checkbox', checked: !hidden.has(c.key) });
      cb.onchange = () => {
        if (cb.checked) hidden.delete(c.key);
        else hidden.add(c.key);
        settings.hiddenColumns = [...hidden];
        saveSettings();
        grid.renderHeader();
        grid.render();
      };
      list.append(h('label', {}, cb, c.title));
    }
    box.append(h('h2', {}, 'Columns'), h('div', { class: 'hint' }, 'Choose which columns to show. Drag a column edge in the header to resize it.'), list, h('div', { class: 'actions' }, h('button', { class: 'btn primary', onclick: close }, 'Done')));
  });
}

function chooseEncoding() {
  const t = active;
  if (!t) return;
  const options: [string, string][] = [
    ['auto', 'Detect automatically'],
    ['utf-8', 'UTF-8'],
    ['windows-1252', 'Western (Windows-1252 / Latin-1)'],
    ['utf-16le', 'UTF-16 LE'],
    ['utf-16be', 'UTF-16 BE'],
  ];
  modal((box, close) => {
    box.append(h('h2', {}, 'Reopen with encoding'));
    const list = h('div', { class: 'form' });
    for (const [v, label] of options)
      list.append(
        h('button', { class: (t.encoding ?? 'auto') === v ? 'btn on' : 'btn', style: 'text-align:left', onclick: () => { close(); void reopen(t, v); } }, label),
      );
    box.append(list, h('div', { class: 'actions' }, h('button', { class: 'btn', onclick: close }, 'Cancel')));
  });
}

/** Re-opens a tab's files with different options (e.g. encoding), keeping its filters and bookmarks. */
async function reopen(t: Tab, encoding: string) {
  t.encoding = encoding;
  await engine.close(t.id);
  t.id = await engine.open(t.paths, {
    encoding: encoding as 'auto',
    format: t.formatId || undefined,
    customFormats: settings.customFormats,
  });
  t.info = placeholderInfo(t.id, t.info.title);
  t.search.result = null;
  if (active === t) activate(t);
}

// ---------------------------------------------------------------------- help

function showHelp() {
  const keys: [string, string][] = [
    ['Ctrl+O / Ctrl+Shift+O', 'Open files / open and merge into one timeline'],
    ['Ctrl+F, /', 'Find'],
    ['Enter, F3, n / Shift+Enter, Shift+F3, N', 'Next / previous match'],
    ['Ctrl+L', 'Quick filter (words, a OR b, -word, level:error, logger:Db, "phrase", /regex/)'],
    ['e / E', 'Next / previous error'],
    ['w / W', 'Next / previous warning'],
    ['Ctrl+B, b, double-click', 'Toggle bookmark'],
    ['F2 / Shift+F2', 'Next / previous bookmark'],
    ['Ctrl+G', 'Go to line'],
    ['Ctrl+T, f, End', 'Follow new lines (tail)'],
    ['Shift+click, Shift+arrows', 'Select a range (status bar shows the time between rows)'],
    ['Ctrl+C', 'Copy selected entries'],
    ['Ctrl+E', 'Export the current view (.log, .csv, .jsonl)'],
    ['Ctrl+W, Ctrl+Tab', 'Close tab, next tab'],
    ['Timeline: click / drag', 'Jump to time / filter to a time range'],
  ];
  modal((box, close) => {
    const table = h('table', { class: 'keys' });
    for (const [k, d] of keys) {
      const tr = document.createElement('tr');
      tr.innerHTML = `<td><kbd>${esc(k)}</kbd></td><td>${esc(d)}</td>`;
      table.append(tr);
    }
    box.append(h('h2', {}, 'Keyboard shortcuts'), table, h('div', { class: 'actions' }, h('button', { class: 'btn primary', onclick: close }, 'Close')));
  });
}

// ------------------------------------------------------------- wiring & init

async function openDialog(merge: boolean) {
  const paths = await ulv().openDialog(merge);
  await openPaths(paths, merge && paths.length > 1);
}

$('#btn-open').onclick = () => void openDialog(false);
$('#btn-merge').onclick = () => void openDialog(true);
$('#btn-follow').onclick = toggleFollow;
$('#btn-hist').onclick = () => toggleLayout('showHistogram');
$('#btn-detail').onclick = () => toggleLayout('showDetail');
$('#btn-help').onclick = showHelp;
$('#btn-theme').onclick = () => {
  settings.theme = document.documentElement.dataset.theme === 'dark' ? 'light' : 'dark';
  saveSettings();
  applyTheme();
};

window.ulv.onMenu((cmd, ...args) => {
  switch (cmd) {
    case 'open':
      return void openDialog(false);
    case 'open-merge':
      return void openDialog(true);
    case 'open-path':
      return void openPaths([String(args[0])], false);
    case 'export':
      return void exportView();
    case 'close-tab':
      return active && void closeTab(active);
    case 'find':
      searchInput.focus();
      return searchInput.select();
    case 'quick-filter':
      quickInput.focus();
      return quickInput.select();
    case 'goto-line':
      return void gotoLine();
    case 'toggle-bookmark': {
      const row = active && grid.getCached(active.focus);
      return row && void toggleBookmark(row);
    }
    case 'toggle-follow':
      return toggleFollow();
    case 'toggle-histogram':
      return toggleLayout('showHistogram');
    case 'toggle-detail':
      return toggleLayout('showDetail');
    case 'toggle-sidebar':
      return toggleLayout('showSidebar');
    case 'encoding':
      return chooseEncoding();
    case 'columns':
      return editColumns();
    case 'toggle-theme':
      return $('#btn-theme').click();
    case 'help':
      return showHelp();
  }
});

document.addEventListener('keydown', (e) => {
  if (!$('#modal').classList.contains('hidden')) return;
  if (e.key === 'F3' || (e.key === 'g' && (e.ctrlKey || e.metaKey) && e.shiftKey)) {
    e.preventDefault();
    stepSearch(e.shiftKey ? -1 : 1);
  } else if (e.key === 'F2') {
    e.preventDefault();
    stepBookmark(e.shiftKey ? -1 : 1);
  } else if (e.key === 'Tab' && e.ctrlKey && tabs.length > 1) {
    e.preventDefault();
    const i = tabs.indexOf(active!);
    activate(tabs[(i + (e.shiftKey ? -1 : 1) + tabs.length) % tabs.length]);
  } else if (e.key === 'F1') {
    e.preventDefault();
    showHelp();
  }
});

// drag & drop: several files dropped together open as one merged timeline
let dragDepth = 0;
window.addEventListener('dragenter', (e) => {
  e.preventDefault();
  dragDepth++;
  document.body.classList.add('dragging');
});
window.addEventListener('dragleave', () => {
  if (--dragDepth <= 0) {
    dragDepth = 0;
    document.body.classList.remove('dragging');
  }
});
window.addEventListener('dragover', (e) => e.preventDefault());
window.addEventListener('drop', (e) => {
  e.preventDefault();
  dragDepth = 0;
  document.body.classList.remove('dragging');
  const files = [...(e.dataTransfer?.files ?? [])].map((f) => ulv().pathForFile(f)).filter(Boolean);
  void openPaths(files, files.length > 1);
});

matchMedia('(prefers-color-scheme: dark)').addEventListener('change', applyTheme);

async function init() {
  settings = (await ulv().getSettings()) as Settings;
  settings.highlights ??= DEFAULT_HIGHLIGHTS.map((r) => ({ ...r }));
  applyTheme();
  applyLayout();
  renderFormatOptions();
  activate(null);
  const files = await ulv().startupFiles();
  if (files.length) await openPaths(files, false);
  // test hook: lets automated UI tests drive the app without native dialogs
  (window as unknown as Record<string, unknown>).__ulv = { openPaths, state: () => ({ tabs, active }) };
}

void init();


