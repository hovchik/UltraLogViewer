import { FORMAT_LABELS } from '../core/formats';
import { fieldToken, OPERATORS, operatorHint, splitToken, tokenAt, validatePattern, validateQuery } from '../core/query';
import { ColumnDef, CustomFormat, DocInfo, FilterRule, FilterSpec, Level, LEVEL_NAMES, PatternStat, RowData, TextPattern } from '../core/types';
import { engine, EngineEvent, ulv } from './api';
import { Autocomplete, Suggestion } from './autocomplete';
import { DetailView } from './detail';
import { Grid } from './grid';
import { HistogramView } from './histogram';
import { openPalette } from './palette';
import { PatternsView } from './patternsView';
import { Sidebar } from './sidebar';
import type { TimeMode } from '../core/timestamp';
import { describeWindow, OpenWindow, parseDuration, parsePoint, windowEmpty } from '../core/timeWindow';
import { Bookmark, DEFAULT_HIGHLIGHTS, defaultFilter, HighlightRule, Settings, Tab, uid, viewSettings } from './state';
import { baseName, dirName, parseWorkspace, resolvePath, resolveWorkspace, serializeWorkspace } from './workspace';
import { $, ask, contextMenu, debounce, esc, fmtBytes, fmtNum, h, MenuItem, modal, toast } from './util';

let settings: Settings = {};
const tabs: Tab[] = [];
let active: Tab | null = null;

const searchInput = $<HTMLInputElement>('#search');
const quickInput = $<HTMLInputElement>('#quick');
const formatSel = $<HTMLSelectElement>('#format');
const toggles = { case: $('#s-case'), word: $('#s-word'), regex: $('#s-regex') };

const saveSettings = debounce(() => void ulv().setSettings(settings as Record<string, unknown>), 300);

// ---------------------------------------------------------------- components

/** Set while one pane of a split is moved to follow the other, so that move is not synced back. */
let syncingPanes = false;

/**
 * What a grid sees of the app. The app works on the active tab, so whatever the user does in a grid (select, press a
 * key, open a menu) first makes its pane the active one (see focusPane); rendering asks about the grid's own tab.
 */
function gridHost(self: () => Grid) {
  const focus = () => !syncingPanes && (focusPane(self()), true);
  return {
    highlights: () => settings.highlights ?? [],
    view: () => viewSettings(settings),
    columnWidth: (col: ColumnDef) => (col.width === 0 ? 0 : settings.columnWidths?.[col.key] ?? col.width),
    hiddenColumns: () => hiddenOf(paneTab(self())),
    columns: (tab: Tab) => effectiveColumns(tab),
    onHeaderMenu: (e: MouseEvent, key: string | null) => focus() && headerMenu(e, key),
    onRowMenu: (e: MouseEvent, row: RowData, col: string | null) => focus() && rowMenu(e, row, col),
    setColumnWidth: (key: string, w: number) => {
      (settings.columnWidths ??= {})[key] = w;
      saveSettings();
    },
    onSelectionChange: () => {
      if (!focus()) return;
      void updateDetail();
      renderStatus();
      syncOtherPane();
    },
    onTopRowChange: () => {
      // Scrolling (or a layout pass) in the other pane does not take the focus.
      if (self() !== grid) return;
      updateHistogramViewport();
      // scrolled to the last row: the new-lines pill has done its job
      if (active?.follow && active.newRows) {
        active.newRows = 0;
        renderPill();
      }
      renderStatus();
    },
    onKey: (e: KeyboardEvent) => focus() && gridKey(e),
    toggleBookmark: (row: RowData) => focus() && void toggleBookmark(row),
    copyRows: (a: number, b: number) => focus() && void copyRows(a, b),
    onTokenClick: (row: RowData, text: string) => focus() && searchToken(row, text),
  };
}

const mainGrid: Grid = new Grid($('#grid'), gridHost(() => mainGrid));
const splitGrid: Grid = new Grid($('#grid2'), gridHost(() => splitGrid));
/** The grid of the active tab: the main one, or the split pane's when that has focus. */
let grid = mainGrid;

const sidebar = new Sidebar($('#sidebar'), {
  settings: () => settings,
  saveSettings,
  applyFilter: () => active && void applyFilter(active),
  highlightsChanged: () => {
    for (const g of visibleGrids()) {
      g.refreshPatterns();
      g.render();
    }
  },
  jumpToBookmark: (b) => void jumpToBookmark(b),
  removeBookmark: (b) => active && void removeBookmark(active, b.key),
  bookmarkNote: (b) => void editNote(b),
  columns: (tab) => effectiveColumns(tab),
  isColumnShown,
  toggleColumn: (key) => setColumnShown(key, !isColumnShown(key)),
  filterByValue: (key, value, mode) => active && filterByValue(active, key, value, mode),
  rescanFields: () => active && void refreshFields(active),
  clearAllFilters: () => active && clearAllFilters(active),
});

const detail = new DetailView(
  $('#detail'),
  (size, dock) => {
    if (dock === 'right') settings.detailWidth = size;
    else settings.detailHeight = size;
    saveSettings();
  },
  (e, row, key) => fieldMenu(e, row, key),
  () => viewSettings(settings).detailDock,
  () => setView('detailDock', viewSettings(settings).detailDock === 'right' ? 'bottom' : 'right'),
  () => viewSettings(settings),
  (key) => {
    if (!active) return;
    grid.unfold(key);
    active.unfolded.add(key);
    void updateDetail();
  },
);

const patternsView = new PatternsView($('#patterns'), {
  onlyPattern: (p) => active && addPatternRule(active, p, 'include'),
  excludePattern: (p) => active && addPatternRule(active, p, 'exclude'),
  copy: (text, what) => copyText(text, what),
  rescan: (all) => active && void refreshPatterns(active, all),
  selectionChanged: () => void updateDetail(),
  close: () => {
    if (!active) return;
    setPatternsShown(active, false);
    grid.root.focus();
  },
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
  onResize: (height) => {
    settings.histogramHeight = height;
    saveSettings();
    if (active) void refreshOverview(active);
  },
}, $('#hist-split'));

// ---------------------------------------------------------------------- tabs

function placeholderInfo(id: number, title: string): DocInfo {
  return { id, title, sources: [], columns: [], total: 0, viewCount: 0, levelCounts: [0, 0, 0, 0, 0, 0, 0], indexing: true, progress: 0, timeMin: null, timeMax: null, merged: false, ignoreFilter: false };
}

/** Opens one tab over the given files (several = merged timeline) without activating it. */
async function openTab(g: string[], opts: { format?: string; encoding?: string; window?: OpenWindow | null } = {}): Promise<Tab> {
  const window = windowEmpty(opts.window) ? null : opts.window!;
  const id = await engine.open(g, {
    customFormats: settings.customFormats,
    format: opts.format || undefined,
    encoding: (opts.encoding || undefined) as 'auto' | undefined,
    cache: settings.indexCache !== false,
    window,
  });
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
    formatId: opts.format ?? '',
    encoding: opts.encoding && opts.encoding !== 'auto' ? opts.encoding : undefined,
    window,
    expanded: new Set(),
    unfolded: new Set(),
  };
  tabs.push(tab);
  return tab;
}

/** `window`: open only this time window of the files. */
async function openPaths(paths: string[], merge: boolean, window: OpenWindow | null = null) {
  if (!paths.length) return;
  const groups = merge ? [paths] : paths.map((p) => [p]);
  for (const g of groups) activate(await openTab(g, { window }));
  void ulv().addRecent(paths);
  settings.recent = [...paths, ...(settings.recent ?? []).filter((p) => !paths.includes(p))].slice(0, 15);
}

function activate(tab: Tab | null) {
  active = tab;
  quickInput.value = tab?.filter.query ?? '';
  formatSel.value = tab?.formatId ?? '';
  showPanes(tab);
  renderTabs();
  sidebar.render(tab);
  $('#empty').classList.toggle('hidden', !!tab);
  applyPatternsLayout();
  renderPill();
  syncMenu();
  renderBanner();
  renderPartialBanner();
  ac.close();
  if (tab) {
    void refreshOverview(tab);
    void updateDetail();
    if (!tab.fieldStats && !tab.info.indexing) refreshFieldsSoon(tab);
  } else {
    detail.render(null, undefined);
    histogram.set(null);
    renderRecent();
  }
  renderStatus();
  renderToolbar();
}

/** Closes a tab (with its split pane); for a split pane, closes the split. */
async function closeTab(tab: Tab) {
  if (tab.splitOf) return closeSplit(tab.splitOf);
  const i = tabs.indexOf(tab);
  if (i < 0) return;
  tabs.splice(i, 1);
  const wasActive = ownerOf(active) === tab;
  for (const t of tab.split ? [tab, tab.split] : [tab]) {
    clearTimeout(statsTimers.get(t));
    statsTimers.delete(t);
    clearTimeout(patternTimers.get(t));
    patternTimers.delete(t);
  }
  const split = tab.split;
  tab.split = null;
  // The files stay open while another tab (a duplicate) still uses them.
  await engine.close(tab.id);
  if (split) await engine.close(split.id);
  if (wasActive) activate(tabs[Math.min(i, tabs.length - 1)] ?? null);
  else renderTabs();
}

function renderTabs() {
  const el = $('#tabs');
  el.innerHTML = '';
  for (const t of tabs) {
    const live = t.info.sources.some((s) => s.live);
    const tabEl = h(
      'div',
      { class: t === ownerOf(active) ? 'tab active' : 'tab', title: [...t.paths, ...(formatSummary(t) ? [`Format: ${formatSummary(t)!.label}`] : [])].join('\n'), onclick: () => t !== ownerOf(active) && activate(t) },
      h('span', { class: live ? 'dot' : 'dot off', title: live ? 'Live: new lines appear automatically' : 'Static (compressed or converted file)' }),
      h('span', { class: 'name' }, tabTitle(t)),
      h('button', { class: 'x', title: 'Close (Ctrl+W)', onclick: (e: Event) => { e.stopPropagation(); void closeTab(t); } }, '×'),
    );
    tabEl.addEventListener('auxclick', (e) => e.button === 1 && void closeTab(t));
    tabEl.addEventListener('contextmenu', (e) => {
      e.preventDefault();
      contextMenu(e.clientX, e.clientY, [
        { label: 'Duplicate tab', shortcut: 'Ctrl+Shift+T', action: () => void duplicateTab(t) },
        t.split ? { label: 'Close split', action: () => void closeSplit(t) } : { label: 'Split right', shortcut: 'Ctrl+\\', action: () => void splitRight(t) },
        'sep',
        { label: 'Close tab', shortcut: 'Ctrl+W', action: () => void closeTab(t) },
      ]);
    });
    el.append(tabEl);
  }
}

function renderRecent() {
  const el = $('#recent');
  el.innerHTML = '';
  const recent = settings.recent ?? [];
  const workspaces = settings.recentWorkspaces ?? [];
  if (workspaces.length) {
    el.append(h('div', { class: 'muted', style: 'margin-bottom:4px' }, 'Workspaces'));
    for (const p of workspaces.slice(0, 5)) el.append(h('a', { class: 'ws', title: p, onclick: () => void openWorkspace(p) }, p));
    if (recent.length) el.append(h('div', { style: 'height:10px' }));
  }
  if (!recent.length) return;
  el.append(h('div', { class: 'muted', style: 'margin-bottom:4px' }, 'Recent'));
  for (const p of recent.slice(0, 8)) el.append(h('a', { title: p, onclick: () => void openPaths([p], false) }, p));
}

// --------------------------------------------------- exploration tabs and split view
//
// "Duplicate tab" opens another tab over the same indexed files, and "Split right" puts a second grid beside the
// first, each with its own filter. Both are engine documents over one shared set of files (engine.duplicate): nothing
// is indexed again and the files are tailed once. The focused pane is the active tab, so the toolbar, sidebar,
// timeline and details pane follow whichever grid has focus.

let mainShown: Tab | null = null;
let splitShown: Tab | null = null;
const heads = {
  main: { el: $('#head-main'), quick: $<HTMLInputElement>('#head-main .pane-quick'), count: $('#head-main .pane-count') },
  split: { el: $('#head-split'), quick: $<HTMLInputElement>('#head-split .pane-quick'), count: $('#head-split .pane-count') },
};

/** Every open tab, split panes included. */
const allTabs = () => tabs.flatMap((t) => (t.split ? [t, t.split] : [t]));
/** The tab in the tab strip: the tab itself, or the tab a split pane belongs to. */
const ownerOf = (t: Tab | null) => t?.splitOf ?? t;
/** The tab a grid shows. */
const paneTab = (g: Grid) => (g === mainGrid ? mainShown : splitShown);
/** The grid showing a tab, or null when it is not on screen. */
const gridOf = (t: Tab) => (t === mainShown ? mainGrid : t === splitShown ? splitGrid : null);
/** The grids on screen: display settings and highlights apply to both panes of a split. */
const visibleGrids = () => (splitShown ? [mainGrid, splitGrid] : [mainGrid]);
const tabTitle = (t: Tab) => t.info.title + (t.copy ? ` (${t.copy + 1})` : '');

/** Puts a tab (and its split pane) in the grids; `tab` is the focused pane. */
function showPanes(tab: Tab | null) {
  const owner = ownerOf(tab);
  if (mainShown !== owner) mainGrid.setTab((mainShown = owner));
  const split = owner?.split ?? null;
  if (splitShown !== split) {
    if (split) splitGrid.applyView(false); // display settings may have changed while it was hidden
    splitGrid.setTab((splitShown = split));
  }
  for (const id of ['#pane-split', '#pane-bar', '#head-main']) $(id).classList.toggle('hidden', !split);
  grid = split && tab === split ? splitGrid : mainGrid;
  // The toolbar's quick filter belongs to the active tab; in a split each pane has its own.
  quickInput.closest<HTMLElement>('.tb-group')!.style.visibility = split ? 'hidden' : '';
  if (!split) $('#pane-main').style.flex = '';
  renderPaneHeads();
}

/** Makes the pane of `g` the active one (a click or key in it, or focus). */
function focusPane(g: Grid) {
  if (g === grid || syncingPanes || !splitShown) return;
  const t = paneTab(g);
  if (t) activate(t);
}

function renderPaneHeads() {
  if (!splitShown) return;
  for (const [head, g] of [[heads.main, mainGrid], [heads.split, splitGrid]] as const) {
    const t = paneTab(g);
    if (!t) continue;
    head.el.classList.toggle('focused', g === grid);
    const i = t.info;
    head.count.textContent = t.filtering && t.progress ? `Filtering ${Math.round(t.progress.fraction * 100)}%` : t.contextMode || (i.viewCount === i.total && isDefaultFilter(t)) ? fmtNum(i.total) : `${fmtNum(i.viewCount)} of ${fmtNum(i.total)}`;
    if (document.activeElement !== head.quick && head.quick.value !== (t.filter.query ?? '')) head.quick.value = t.filter.query ?? '';
  }
  const owner = ownerOf(active);
  $('#head-split .pane-sync').classList.toggle('on', owner?.syncSplit !== false);
}

function commitPaneQuick(g: Grid, input: HTMLInputElement) {
  const t = paneTab(g);
  if (!t) return;
  const q = input.value;
  if (q === (t.filter.query ?? '')) return;
  const err = validateQuery(q, !!t.filter.queryCaseSensitive);
  if (err) return toast(`Quick filter: ${err}`, true);
  t.filter.query = q;
  if (t === active) quickInput.value = q;
  void applyFilter(t);
}

for (const [head, g] of [[heads.main, mainGrid], [heads.split, splitGrid]] as const) {
  g.root.addEventListener('focus', () => focusPane(g));
  head.quick.addEventListener('focus', () => focusPane(g));
  const soon = debounce(() => commitPaneQuick(g, head.quick), 350);
  head.quick.addEventListener('input', soon);
  head.quick.addEventListener('keydown', (e) => {
    if (e.key === 'Enter') commitPaneQuick(g, head.quick);
    else if (e.key !== 'Escape') return;
    e.preventDefault();
    g.root.focus();
  });
}
$('#head-split .pane-close').onclick = () => void closeSplit(ownerOf(active));
$('#head-split .pane-sync').onclick = () => {
  const owner = ownerOf(active);
  if (!owner) return;
  owner.syncSplit = owner.syncSplit === false;
  renderPaneHeads();
};
// Dragging the bar between the panes resizes them.
$('#pane-bar').addEventListener('mousedown', (e) => {
  e.preventDefault();
  const panes = $('#panes').getBoundingClientRect();
  const move = (m: MouseEvent) => {
    const f = Math.min(0.85, Math.max(0.15, (m.clientX - panes.left) / panes.width));
    $('#pane-main').style.flex = `${f} 1 0`;
    $('#pane-split').style.flex = `${1 - f} 1 0`;
  };
  const up = () => {
    window.removeEventListener('mousemove', move);
    window.removeEventListener('mouseup', up);
  };
  window.addEventListener('mousemove', move);
  window.addEventListener('mouseup', up);
});

function focusQuick() {
  const box = !splitShown ? quickInput : grid === splitGrid ? heads.split.quick : heads.main.quick;
  box.focus();
  box.select();
}

/** A new tab object over the document `id`, which reads the same files as `t`. */
function cloneTab(t: Tab, id: number, info: DocInfo, extra: Partial<Tab>): Tab {
  return {
    id,
    paths: t.paths,
    info,
    filter: defaultFilter(),
    search: { pattern: { ...t.search.pattern }, result: null, current: 0 },
    bookmarks: new Map([...t.bookmarks].map(([k, b]) => [k, { ...b }])),
    anchor: 0,
    focus: 0,
    topRow: 0,
    follow: false,
    formatId: t.formatId,
    encoding: t.encoding,
    window: t.window,
    expanded: new Set(t.expanded),
    unfolded: new Set(t.unfolded),
    columns: copyColumns(t),
    ...extra,
  };
}

/** Opens another tab over the same files with the same filter, search and columns: instant, no reindexing. */
async function duplicateTab(t: Tab | null): Promise<Tab | undefined> {
  if (!t || t.info.indexing) return;
  const owner = ownerOf(t)!;
  const id = await engine.duplicate(t.id);
  const same = allTabs().filter((x) => x.paths.length === t.paths.length && x.paths.every((p, i) => p === t.paths[i]));
  const dup = cloneTab(t, id, await engine.info(id), {
    copy: Math.max(...same.map((x) => x.copy ?? 0)) + 1,
    filter: structuredClone(t.filter),
    anchor: t.anchor,
    focus: t.focus,
    topRow: t.topRow,
  });
  tabs.splice(tabs.indexOf(owner) + 1, 0, dup);
  activate(dup);
  if (!isDefaultFilter(dup)) await applyFilter(dup); // runs the search too
  else {
    await resolveBookmarkRows(dup);
    if (dup.search.pattern.text) void runSearch(dup, false, dup.search.pattern, false);
  }
  refreshFieldsSoon(dup);
  return dup;
}

function toggleSplit() {
  const owner = ownerOf(active);
  if (!owner) return;
  if (owner.split) void closeSplit(owner);
  else void splitRight(owner);
}

/** Opens a second, unfiltered pane over the same files to the right of the tab, scrolled to the selected entry. */
async function splitRight(t: Tab | null): Promise<Tab | undefined> {
  const owner = ownerOf(t);
  if (!owner || owner.info.indexing) return;
  if (owner.split) return owner.split;
  const id = await engine.duplicate(owner.id);
  const pane = cloneTab(owner, id, await engine.info(id), { splitOf: owner });
  owner.split = pane;
  owner.syncSplit ??= true;
  activate(pane);
  await resolveBookmarkRows(pane);
  refreshFieldsSoon(pane);
  await syncPanes(owner, pane);
  return pane;
}

async function closeSplit(owner: Tab | null) {
  const pane = owner?.split;
  if (!owner || !pane) return;
  owner.split = null;
  clearTimeout(statsTimers.get(pane));
  statsTimers.delete(pane);
  const wasActive = active === pane || active === owner;
  if (wasActive) activate(owner);
  else renderTabs();
  await engine.close(pane.id);
}

/** After a selection in one pane: the other pane shows the same entry if it passes its filter, else the nearest by time. */
function syncOtherPane() {
  const t = active;
  const owner = ownerOf(t);
  if (!t || !owner?.split || owner.syncSplit === false || syncingPanes) return;
  void syncPanes(t, t === owner ? owner.split : owner);
}

async function syncPanes(from: Tab, to: Tab) {
  const g = gridOf(to);
  if (!g || !from.info.viewCount || !to.info.viewCount) return;
  const row = gridOf(from)?.getCached(from.focus) ?? (await engine.rows(from.id, from.focus, 1))[0];
  if (!row || gridOf(to) !== g) return;
  let target = await engine.rowForKey(to.id, row.key);
  if (target == null && row.time != null) target = await engine.rowForTime(to.id, row.time);
  if (target == null || gridOf(to) !== g) return;
  const onScreen = target >= to.topRow && target < to.topRow + g.visibleRows();
  if (target === to.focus && onScreen) return;
  syncingPanes = true;
  try {
    g.select(target, false, onScreen ? 'nearest' : 'center');
  } finally {
    syncingPanes = false;
  }
}

/** A pane that is on screen but not active: redraw it after its rows changed. */
function refreshInactivePane(t: Tab) {
  const g = gridOf(t);
  if (t === active || !g) return;
  if (t.follow) t.topRow = Number.MAX_SAFE_INTEGER;
  g.renderHeader();
  g.invalidate();
  renderPaneHeads();
}

// --------------------------------------------------------------- engine events

const refreshOverviewSoon = debounce((tab: Tab) => void refreshOverview(tab), 250);
const rerunSearchSoon = debounce((tab: Tab) => void runSearch(tab, false, undefined, false), 800);

window.ulv.onEvent((raw) => {
  const ev = raw as EngineEvent;
  const tab = allTabs().find((t) => t.id === ev.docId);
  if (!tab) return;
  if (ev.type === 'progress') {
    tab.progress = ev.fraction >= 1 ? undefined : { phase: ev.phase, fraction: ev.fraction };
    if (tab === active) renderStatus();
    return;
  }
  if (ev.type === 'view-partial') {
    showPartialView(tab, ev);
    return;
  }
  if (ev.type === 'doc-error') {
    tab.error = ev.message;
    toast(ev.message, true);
    return;
  }
  const wasIndexing = tab.info.indexing;
  const before = tab.info.viewCount;
  tab.info = ev.info;
  if (wasIndexing && !ev.info.indexing) void onDocReady(tab);
  // live tail: rows appended to the view
  const appended = !ev.reset && !wasIndexing && !ev.info.indexing ? ev.info.viewCount - before : 0;
  if (appended > 0 && !tab.follow) tab.newRows = (tab.newRows ?? 0) + appended;
  if (tab.showPatterns && !ev.info.indexing && !wasIndexing && (ev.reset || Date.now() - (tab.patternsAt ?? 0) > 10_000) && !patternTimers.has(tab)) refreshPatternsSoon(tab);
  if (tab !== active) {
    renderTabs();
    refreshInactivePane(tab);
    return;
  }
  if (appended > 0 && document.visibilityState === 'visible') grid.flash(before, ev.info.viewCount);
  renderPill();
  if (ev.reset) grid.renderHeader();
  if (tab.follow) tab.topRow = Number.MAX_SAFE_INTEGER;
  grid.invalidate();
  if (tab.follow && tab.info.viewCount) {
    tab.focus = tab.anchor = tab.info.viewCount - 1;
    void updateDetail();
  }
  refreshOverviewSoon(tab);
  if (tab.search.result && !ev.info.indexing) rerunSearchSoon(tab);
  // Field stats follow the tail at most every 10 s.
  if (!ev.info.indexing && !wasIndexing && (ev.reset || Date.now() - (tab.statsAt ?? 0) > 10_000) && !statsTimers.has(tab)) refreshFieldsSoon(tab);
  if (!$('#sidebar').contains(document.activeElement)) sidebar.render(tab);
  renderStatus();
  renderTabs();
  renderPartialBanner();
});

/**
 * Rows a running filter has found so far. Within one epoch the view only gains rows at its end, so the grid keeps its
 * cache; a new epoch (the first partial view replacing the old one) redraws it. Events of an older run are dropped.
 */
function showPartialView(tab: Tab, ev: Extract<EngineEvent, { type: 'view-partial' }>) {
  if (!tab.filtering || ev.seq !== tab.filterSeq) return;
  tab.info = ev.info;
  const sameEpoch = tab.partialEpoch === ev.epoch;
  tab.partialEpoch = ev.epoch;
  if (tab !== active) {
    gridOf(tab)?.invalidate(sameEpoch);
    renderPaneHeads();
    return;
  }
  if (!sameEpoch && tab.search.result) {
    // The hits are rows of the old view; the search runs again when the filter is done.
    tab.search.result = null;
    grid.refreshPatterns();
    renderSearchCount();
  }
  grid.invalidate(sameEpoch);
  renderStatus();
}

/** "Filtered 8,695,680 entries in 0.82 s": makes the cost of a scan visible after it finishes. */
const scanCost = (verb: string, entries: number, ms: number) => `${verb} ${fmtNum(entries)} ${entries === 1 ? 'entry' : 'entries'} in ${(ms / 1000).toFixed(2)} s`;

async function onDocReady(tab: Tab) {
  tab.progress = undefined;
  if (tab.pending) await applyPending(tab);
  // Line numbers of a partial view are not the file's: its bookmarks are neither restored nor saved.
  // Saved per source path, so the members of a zip and the files of a merge each keep their own (as in the .NET app).
  if (!isPartial(tab)) {
    let restored = 0;
    for (const [source, src] of tab.info.sources.entries()) {
      for (const s of settings.bookmarks?.[src.path] ?? []) {
        const key = await engine.keyForLine(tab.id, source, s.line);
        if (!key) continue;
        const rows = await engine.rows(tab.id, (await engine.rowForKey(tab.id, key)) ?? 0, 1);
        tab.bookmarks.set(key, { key, line: s.line, source, note: s.note, preview: firstLine(rows[0]?.fields.message || rows[0]?.text || '') });
        restored++;
      }
    }
    if (restored) await resolveBookmarkRows(tab);
  }
  if (!isDefaultFilter(tab)) await applyFilter(tab);
  if (tab.search.pattern.text) void runSearch(tab, false, tab.search.pattern);
  refreshFieldsSoon(tab);
  if (tab === active) {
    sidebar.render(tab);
    void updateDetail();
  }
}

// -------------------------------------------------------------------- filter

const isDefaultFilter = (t: Tab) =>
  t.filter.levels.every(Boolean) &&
  !t.filter.rules.length &&
  !t.filter.query &&
  t.filter.timeFrom == null &&
  t.filter.timeTo == null &&
  !t.filter.bookmarksOnly &&
  (t.filter.groupRepeats ?? 'off') === 'off' &&
  t.filter.clearedBefore == null;

/** "Clear all filters": levels, rules, query, time range, bookmarks-only, context, group repeats and Clear. */
function clearAllFilters(t: Tab) {
  t.filter = { ...defaultFilter(), bookmarks: t.filter.bookmarks };
  if (t === active) quickInput.value = '';
  void applyFilter(t);
}

/** Clear (Ctrl+K): hides every entry so far; only lines appended from now on are shown. */
function clearView(t: Tab) {
  if (t.info.indexing) return;
  t.filter.clearedBefore = t.info.sources.map((s) => s.entries);
  void applyFilter(t);
}

function setGroupRepeats(t: Tab, mode: NonNullable<FilterSpec['groupRepeats']>) {
  if ((t.filter.groupRepeats ?? 'off') === mode) return;
  t.filter.groupRepeats = mode;
  void applyFilter(t);
}

/** Tells main what the menu should show for the active tab (Group repeats, Patterns). */
function syncMenu() {
  void ulv().setMenuState({ groupRepeats: active?.filter.groupRepeats ?? 'off', patterns: !!active?.showPatterns });
}

async function applyFilter(tab: Tab) {
  const focusKey = gridOf(tab)?.getCached(tab.focus)?.key;
  tab.filter.bookmarks = [...tab.bookmarks.keys()];
  // Any filter change leaves "show in context" first.
  const wasContext = !!tab.contextMode;
  let info: DocInfo | null;
  const seq = (tab.filterSeq = (tab.filterSeq ?? 0) + 1);
  const started = performance.now();
  try {
    if (wasContext) {
      tab.contextMode = false;
      if (tab === active) renderBanner();
      await engine.setIgnoreFilter(tab.id, false);
    }
    tab.filtering = true;
    tab.partialEpoch = undefined;
    info = await engine.setFilter(tab.id, tab.filter, seq);
  } catch (e) {
    toast((e as Error).message, true);
    return;
  } finally {
    if (tab.filterSeq === seq) tab.filtering = false;
  }
  if (!info) return; // superseded by a newer filter
  tab.info = info;
  tab.scanCost = isDefaultFilter(tab) ? undefined : scanCost('Filtered', info.total, performance.now() - started);
  tab.progress = undefined;
  tab.newRows = 0;
  if (tab.showPatterns) refreshPatternsSoon(tab);
  const row = focusKey ? await engine.rowForKey(tab.id, focusKey, wasContext) : null;
  tab.anchor = tab.focus = row ?? Math.min(tab.focus, Math.max(0, info.viewCount - 1));
  await resolveBookmarkRows(tab);
  refreshFieldsSoon(tab);
  if (tab !== active) return refreshInactivePane(tab);
  grid.clearFlash();
  renderPill();
  syncMenu();
  grid.invalidate();
  grid.reveal(tab.focus, 'center');
  sidebar.render(tab);
  renderStatus();
  void refreshOverview(tab);
  void updateDetail();
  if (tab.search.pattern.text) void runSearch(tab, false, undefined, false); // keep showing what the filter cost
}

// ------------------------------------------------------------------ patterns

const patternTimers = new Map<Tab, ReturnType<typeof setTimeout>>();

/** Recomputes the patterns after ~300 ms of quiet (a newer run cancels the older one in the engine). */
function refreshPatternsSoon(t: Tab, ms = 300) {
  clearTimeout(patternTimers.get(t));
  patternTimers.set(
    t,
    setTimeout(() => {
      patternTimers.delete(t);
      void refreshPatterns(t);
    }, ms),
  );
}

async function refreshPatterns(t: Tab, all = false) {
  if (!tabs.includes(t) || !t.showPatterns) return;
  if (t.info.indexing) return refreshPatternsSoon(t, 500);
  t.patternsAt = Date.now();
  t.patternsBusy = true;
  if (t === active) patternsView.render(t);
  const selected = t.patterns?.patterns[t.patternSel ?? -1]?.template;
  let r;
  try {
    r = await engine.patterns(t.id, all ? Math.max(1, t.info.viewCount) : undefined);
  } catch {
    return; // closed
  }
  if (r.cancelled || !tabs.includes(t)) return;
  t.patternsBusy = false;
  t.patterns = r;
  const keep = selected === undefined ? -1 : r.patterns.findIndex((p) => p.template === selected);
  t.patternSel = keep >= 0 ? keep : r.patterns.length ? 0 : undefined;
  if (t !== active) return;
  patternsView.render(t);
  void updateDetail();
}

function setPatternsShown(t: Tab, on: boolean) {
  // The patterns view takes the place of the main pane, so in a split it is about the left pane's entries.
  if (on && t.splitOf) {
    activate(t.splitOf);
    t = t.splitOf;
  }
  if (!!t.showPatterns === on) return;
  t.showPatterns = on;
  if (on) {
    t.patterns = null;
    refreshPatternsSoon(t);
  } else {
    clearTimeout(patternTimers.get(t));
    patternTimers.delete(t);
  }
  if (t !== active) return;
  applyPatternsLayout();
  syncMenu();
  renderToolbar();
  void updateDetail();
}

function applyPatternsLayout() {
  const on = !!active?.showPatterns;
  $('#patterns').classList.toggle('hidden', !on);
  mainGrid.root.classList.toggle('hidden', on);
  if (on) {
    patternsView.render(active);
    $('#patterns').focus();
  } else if (active) {
    grid.invalidate(true);
  }
}

function addPatternRule(t: Tab, p: PatternStat, mode: FilterRule['mode']) {
  t.filter.rules.push({ id: uid(), text: p.template, mode, patternMatch: true, regex: false, caseSensitive: true, wholeWord: false, enabled: true });
  if (mode === 'include') setPatternsShown(t, false);
  void applyFilter(t);
  if (mode === 'include') grid.root.focus();
}

// --------------------------------------------------------------- live tail

/** The "↓ N new" pill over the grid while rows arrive and follow is off. */
function renderPill() {
  const el = $('#newpill');
  const t = grid === mainGrid ? active : null; // the pill sits over the main pane
  const n = t && !t.follow && !t.showPatterns ? t.newRows ?? 0 : 0;
  el.classList.toggle('hidden', n <= 0);
  if (n > 0) el.textContent = `↓ ${fmtNum(n)} new`;
}

$('#newpill').addEventListener('mousedown', (e) => e.preventDefault());
$('#newpill').onclick = () => {
  const t = active;
  if (!t) return;
  if (!t.follow) toggleFollow();
  else if (t.info.viewCount) grid.scrollTo(Number.MAX_SAFE_INTEGER);
  grid.root.focus();
};

async function refreshOverview(tab: Tab) {
  if (tab !== active) return;
  const [ov, hist] = await Promise.all([
    engine.overview(tab.id, Math.max(50, Math.floor(grid.root.querySelector('.grid-body')!.clientHeight))),
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

// ----------------------------------------------------------- show in context

/** Shows every entry (rows the filter hides are dimmed), or goes back to the filtered view. */
async function toggleContext(t: Tab) {
  if (t.contextMode) return setContextMode(t, false);
  if (isDefaultFilter(t)) return toast('Show in context needs an active filter');
  return setContextMode(t, true);
}

async function setContextMode(t: Tab, on: boolean) {
  if (!!t.contextMode === on) return;
  const cur = grid.getCached(t.focus) ?? (t.info.viewCount ? (await engine.rows(t.id, t.focus, 1))[0] : undefined);
  try {
    t.info = await engine.setIgnoreFilter(t.id, on);
  } catch (e) {
    return toast((e as Error).message, true);
  }
  t.contextMode = on;
  if (on) t.contextLine = cur?.line;
  // Keep the same entry selected; when leaving, fall back to the nearest following visible row.
  const row = cur ? await engine.rowForKey(t.id, cur.key, !on) : null;
  t.anchor = t.focus = Math.max(0, Math.min(row ?? 0, t.info.viewCount - 1));
  await resolveBookmarkRows(t);
  refreshFieldsSoon(t);
  if (t.showPatterns) refreshPatternsSoon(t);
  if (t !== active) return;
  renderBanner();
  grid.invalidate();
  grid.reveal(t.focus, 'center');
  renderStatus();
  void refreshOverview(t);
  void updateDetail();
  if (t.search.pattern.text) void runSearch(t, false);
  grid.root.focus();
}

function renderBanner() {
  const t = active;
  const el = $('#ctx-banner');
  el.classList.toggle('hidden', !t?.contextMode);
  if (!t?.contextMode) return;
  $('.msg', el).textContent = `Showing every entry around line ${t.contextLine ?? t.focus + 1}. Rows your filters hide are dimmed.`;
}

$('#ctx-exit').onclick = () => active && void setContextMode(active, false);

// ------------------------------------------------------------ partial view

const isPartial = (t: Tab) => t.info.sources.some((s) => s.partial);

/** Text of the partial-view banner, e.g. "Showing 2024-05-01 10:00–11:00 · partial file". */
function partialText(t: Tab): string {
  const src = t.info.sources.filter((s) => s.partial);
  if (!src.length) return '';
  const i = t.info;
  const range = i.timeMin != null && i.timeMax != null ? describeWindow(i.timeMin, i.timeMax) : `no entries in ${describeWindow(src[0].windowFrom, src[0].windowTo)}`;
  return `Showing ${range} · ${i.sources.length > 1 ? 'partial files' : 'partial file'}${src.every((s) => s.live) ? ' · following new lines' : ''}`;
}

function renderPartialBanner() {
  const t = active;
  const on = !!t && !t.info.indexing && isPartial(t);
  const el = $('#partial-banner');
  el.classList.toggle('hidden', !on);
  if (on) $('.msg', el).textContent = partialText(t!);
}

$('#partial-whole').onclick = () => {
  const t = active;
  if (!t) return;
  t.window = null;
  void reopen(t, t.encoding ?? 'auto');
};

/** "Open last…" (a duration back from the newest entry) or "Open time range…" (from / until). */
async function openWindowDialog(range: boolean) {
  const paths = await ulv().openDialog(true);
  if (!paths.length) return;
  let window: OpenWindow | null;
  if (!range) {
    const d = await ask('Open last', { value: '1h', hint: 'Show the newest entries of this long (e.g. 15m, 1h, 2d).' });
    if (d === null) return;
    const last = parseDuration(d);
    if (last === null) return toast('Enter a duration such as 15m, 1h or 2d', true);
    window = { last };
  } else {
    const now = Date.now();
    const from = await ask('Open time range', { value: '-1h', hint: 'From: 2024-05-01 10:00, 10:30 or -2h (empty = the start).' });
    if (from === null) return;
    const until = await ask('Open time range', { value: '', placeholder: '2024-05-01 11:00', hint: 'Until (empty = the end, and keep following new lines).' });
    if (until === null) return;
    const since = from.trim() ? parsePoint(from, now) : null;
    const to = until.trim() ? parsePoint(until, now) : null;
    if ((from.trim() && since === null) || (until.trim() && to === null)) return toast('Enter a time such as 2024-05-01 10:00, 10:30 or -2h', true);
    window = { since, until: to };
  }
  await openPaths(paths, paths.length > 1, windowEmpty(window) ? null : window);
}

// -------------------------------------------------------------- field stats

const statsTimers = new Map<Tab, ReturnType<typeof setTimeout>>();

/** Recounts field values after ~300 ms of quiet (a newer request cancels the older one). */
function refreshFieldsSoon(t: Tab, ms = 300) {
  clearTimeout(statsTimers.get(t));
  statsTimers.set(
    t,
    setTimeout(() => {
      statsTimers.delete(t);
      void refreshFields(t);
    }, ms),
  );
}

async function refreshFields(t: Tab) {
  if (!tabs.includes(t) || t.info.indexing) return;
  t.statsAt = Date.now();
  let st;
  try {
    st = await engine.fieldStats(t.id);
  } catch {
    return; // closed
  }
  if (st.cancelled || !tabs.includes(t)) return;
  t.fieldStats = st;
  if (t === active) {
    sidebar.renderFields(t);
    if (document.activeElement === quickInput && ac.open) ac.update();
  }
}

// ------------------------------------------------------------------ columns

const formatName = (t: Tab) => t.info.sources[0]?.format ?? '';

// A duplicated tab or split pane has column choices of its own (copied when it was made, not saved); other tabs use
// the per-format ones in the settings.
const extrasOf = (t: Tab): string[] => t.columns?.extra ?? settings.extraColumns?.[formatName(t)] ?? [];
const orderOf = (t: Tab): string[] | undefined => (t.columns ? t.columns.order : settings.columnOrder?.[formatName(t)]);
const hiddenOf = (t: Tab | null): string[] => t?.columns?.hidden ?? settings.hiddenColumns ?? [];
const copyColumns = (t: Tab): NonNullable<Tab['columns']> => ({ extra: [...extrasOf(t)], order: [...(orderOf(t) ?? [])], hidden: [...hiddenOf(t)] });

function setExtras(t: Tab, list: string[]) {
  if (t.columns) t.columns.extra = list;
  else if (list.length) (settings.extraColumns ??= {})[formatName(t)] = list;
  else delete settings.extraColumns?.[formatName(t)];
}

function setHidden(t: Tab, list: string[]) {
  if (t.columns) t.columns.hidden = list;
  else settings.hiddenColumns = list;
}

function setOrder(t: Tab, keys: string[] | undefined) {
  if (t.columns) t.columns.order = keys ?? [];
  else if (keys) (settings.columnOrder ??= {})[formatName(t)] = keys;
  else delete settings.columnOrder?.[formatName(t)];
}

/** Format columns plus extra field columns (before the stretching message column), in the user's order. */
function effectiveColumns(t: Tab | null): ColumnDef[] {
  if (!t || !t.info.columns.length) return t?.info.columns ?? [];
  let cols = [...t.info.columns];
  const extras = extrasOf(t).filter((k) => !cols.some((c) => c.key === k));
  if (extras.length) {
    const at = cols.findIndex((c) => c.width === 0);
    cols.splice(at < 0 ? cols.length : at, 0, ...extras.map((k) => ({ key: k, title: k, width: 140 })));
  }
  const order = orderOf(t);
  if (order?.length) {
    const first = order.map((k) => cols.find((c) => c.key === k)).filter((c): c is ColumnDef => !!c);
    cols = [...first, ...cols.filter((c) => !order.includes(c.key))];
  }
  return cols;
}

function isColumnShown(key: string): boolean {
  return effectiveColumns(active).some((c) => c.key === key) && !hiddenOf(active).includes(key);
}

const columnTitle = (t: Tab, key: string) => effectiveColumns(t).find((c) => c.key === key)?.title ?? key;

function columnsChanged() {
  saveSettings();
  grid.renderHeader();
  grid.render();
  if (active) sidebar.renderFields(active);
}

/** Shows or hides a column; field keys that aren't format columns are added/removed as extra columns. */
function setColumnShown(key: string, show: boolean) {
  const t = active;
  if (!t) return;
  const isBase = t.info.columns.some((c) => c.key === key);
  const extras = extrasOf(t);
  if (show) {
    setHidden(t, hiddenOf(t).filter((k) => k !== key));
    if (!isBase && !extras.includes(key)) setExtras(t, [...extras, key]);
  } else if (isBase) {
    setHidden(t, [...new Set([...hiddenOf(t), key])]);
  } else {
    setExtras(t, extras.filter((k) => k !== key));
  }
  columnsChanged();
}

function moveColumn(t: Tab, key: string, dir: 1 | -1) {
  const hidden = hiddenOf(t);
  const keys = effectiveColumns(t).map((c) => c.key);
  const visible = keys.filter((k) => !hidden.includes(k));
  const other = visible[visible.indexOf(key) + dir];
  if (other === undefined) return;
  const a = keys.indexOf(key), b = keys.indexOf(other);
  [keys[a], keys[b]] = [keys[b], keys[a]];
  setOrder(t, keys);
  columnsChanged();
}

function headerMenu(e: MouseEvent, key: string | null) {
  const t = active;
  if (!t) return;
  const cols = effectiveColumns(t);
  const hidden = hiddenOf(t);
  const items: MenuItem[] = key === 'time' ? [...timeMenuItems(), 'sep'] : [];
  const col = cols.find((c) => c.key === key);
  if (col) {
    const visible = cols.filter((c) => !hidden.includes(c.key));
    const vi = visible.indexOf(col);
    items.push(
      { label: `Move "${col.title}" left`, disabled: vi <= 0, action: () => moveColumn(t, col.key, -1) },
      { label: `Move "${col.title}" right`, disabled: vi < 0 || vi >= visible.length - 1, action: () => moveColumn(t, col.key, 1) },
      'sep',
    );
  }
  for (const c of cols) items.push({ label: c.title, checked: !hidden.includes(c.key), action: () => setColumnShown(c.key, hidden.includes(c.key)) });
  items.push(
    'sep',
    { label: 'Reset column widths', action: () => { settings.columnWidths = {}; columnsChanged(); } },
    { label: 'Reset column order', disabled: !orderOf(t)?.length, action: () => { setOrder(t, undefined); columnsChanged(); } },
  );
  contextMenu(e.clientX, e.clientY, items);
}

// --------------------------------------------------------------- value menus

const LEVEL_LABELS = ['Other', 'Trace', 'Debug', 'Info', 'Warning', 'Error', 'Fatal'];
const firstLineTrim = (s: string) => {
  const i = s.indexOf('\n');
  return (i < 0 ? s : s.slice(0, i)).trim();
};
const quoted = (v: string) => `“${v.length > 40 ? v.slice(0, 40) + '…' : v}”`;
const cellValue = (t: Tab, row: RowData, key: string) => (key === '__source' ? t.info.sources[row.source]?.displayName ?? '' : row.fields[key] ?? '');

function copyText(text: string, what: string) {
  void navigator.clipboard.writeText(text).then(() => toast(`${what} copied`));
}

function addExactRule(t: Tab, key: string, value: string, mode: FilterRule['mode']) {
  t.filter.rules.push({ id: uid(), text: value, mode, column: key, exact: true, regex: false, caseSensitive: false, wholeWord: false, enabled: true });
  void applyFilter(t);
}

/** Click on a value in the Fields section: an exact rule, or the level filter for `level`. */
function filterByValue(t: Tab, key: string, value: string, mode: FilterRule['mode']) {
  if (key.toLowerCase() === 'level') {
    const l = LEVEL_NAMES.indexOf(value.toUpperCase() as (typeof LEVEL_NAMES)[number]);
    if (l > 0) {
      if (mode === 'include') t.filter.levels = t.filter.levels.map((_, i) => i === l);
      else t.filter.levels[l] = false;
      void applyFilter(t);
      return;
    }
  }
  addExactRule(t, key, value, mode);
}

function addHighlight(text: string) {
  (settings.highlights ??= []).push({ id: uid(), text, regex: false, caseSensitive: false, wholeWord: false, enabled: true, color: '#e8590c', row: false });
  saveSettings();
  for (const g of visibleGrids()) {
    g.refreshPatterns();
    g.render();
  }
  sidebar.render(active);
}

/** "Only …" / "Exclude …" items for a value (levels and times get their own filters). */
function filterItems(t: Tab, row: RowData, key: string, v: string): MenuItem[] {
  const f = t.filter;
  if (key === 'level') {
    if (row.level === Level.None) return [];
    const l = row.level;
    return [
      { label: `Only ${LEVEL_LABELS[l]}`, action: () => { f.levels = f.levels.map((_, i) => i === l); void applyFilter(t); } },
      { label: `Hide ${LEVEL_LABELS[l]}`, action: () => { f.levels[l] = false; void applyFilter(t); } },
    ];
  }
  if (key === 'time') {
    if (row.time === null) return [];
    const time = row.time;
    return [
      { label: 'Show from this time', action: () => { f.timeFrom = time; void applyFilter(t); } },
      { label: 'Show until this time', action: () => { f.timeTo = time; void applyFilter(t); } },
    ];
  }
  if (key === 'message' || key === '__source' || !v) return [];
  const title = columnTitle(t, key);
  return [
    { label: `Only ${title} = ${quoted(v)}`, action: () => addExactRule(t, key, v, 'include') },
    { label: `Exclude ${title} = ${quoted(v)}`, action: () => addExactRule(t, key, v, 'exclude') },
  ];
}

function rowMenu(e: MouseEvent, row: RowData, key: string | null) {
  const t = active;
  if (!t) return;
  const v = key ? firstLineTrim(cellValue(t, row, key)) : '';
  const items: MenuItem[] = key ? filterItems(t, row, key, v) : [];
  items.push('sep');
  if (t.contextMode) items.push({ label: 'Back to filtered view', shortcut: 'Esc', action: () => void setContextMode(t, false) });
  else if (!isDefaultFilter(t)) items.push({ label: 'Show in context', shortcut: 'C', action: () => void setContextMode(t, true) });
  if (v && key !== 'time' && key !== 'message') items.push({ label: `Highlight ${quoted(v)}`, action: () => addHighlight(v) });
  const missing = Object.keys(row.fields).filter((k) => k.toLowerCase() !== 'message' && !isColumnShown(k));
  if (missing.length) items.push({ label: 'Add column', submenu: missing.map((k) => ({ label: k, action: () => setColumnShown(k, true) })) });
  items.push(
    'sep',
    { label: 'Copy value', disabled: !key, action: () => key && copyText(cellValue(t, row, key), 'Value') },
    { label: 'Copy entry', action: () => copyText(row.text, 'Entry') },
    { label: 'Toggle bookmark', shortcut: 'B', action: () => void toggleBookmark(row) },
  );
  contextMenu(e.clientX, e.clientY, items);
}

/** Right-click on a field in the details pane. */
function fieldMenu(e: MouseEvent, row: RowData, key: string) {
  const t = active;
  if (!t) return;
  const v = firstLineTrim(row.fields[key] ?? '');
  const items: MenuItem[] = filterItems(t, row, key, v);
  items.push('sep');
  if (v && key !== 'time' && key !== 'message') items.push({ label: `Highlight ${quoted(v)}`, action: () => addHighlight(v) });
  if (key !== 'message' && !isColumnShown(key)) items.push({ label: 'Add column', action: () => setColumnShown(key, true) });
  items.push('sep', { label: 'Copy value', action: () => copyText(row.fields[key] ?? '', 'Value') });
  contextMenu(e.clientX, e.clientY, items);
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

/** Searches a tab: the search box for the active tab, else the tab's own last pattern. */
async function runSearch(tab: Tab, jump: boolean, pattern: TextPattern = tab === active ? currentPattern() : tab.search.pattern, showCost = true) {
  tab.search.pattern = pattern;
  const err = pattern.text ? validatePattern(pattern) : null;
  if (tab === active) searchInput.parentElement!.classList.toggle('error', !!err);
  if (!pattern.text || err) {
    tab.search.result = null;
    grid.refreshPatterns();
    grid.render();
    renderSearchCount();
    return;
  }
  const started = performance.now();
  const result = await engine.search(tab.id, pattern);
  if (result.cancelled || tab.search.pattern !== pattern) return;
  tab.search.result = result;
  if (showCost) tab.scanCost = scanCost('Searched', tab.info.total, performance.now() - started);
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

/** Puts a pattern into the search box and its toggles. */
function setSearchBox(p: TextPattern) {
  searchInput.value = p.text;
  toggles.case.classList.toggle('on', p.caseSensitive);
  toggles.word.classList.toggle('on', p.wholeWord);
  toggles.regex.classList.toggle('on', p.regex);
}

/** Ctrl+click on a token: search for its text from the clicked row on. */
function searchToken(row: RowData, text: string) {
  const t = active;
  if (!t || !text) return;
  searchInput.value = text;
  t.anchor = t.focus = row.row;
  grid.render();
  void runSearch(t, true);
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
    grid.root.focus();
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

/** Applies the quick-filter box (no-op when unchanged). */
function commitQuick() {
  if (!active || (active.filter.query ?? '') === quickInput.value) return;
  // A bad query is reported and the view keeps the last good filter.
  const err = validateQuery(quickInput.value, !!active.filter.queryCaseSensitive);
  if (err) return toast(`Quick filter: ${err}`, true);
  active.filter.query = quickInput.value;
  void applyFilter(active);
}

function saveHistory(q: string) {
  q = q.trim();
  if (!q) return;
  settings.queryHistory = [q, ...(settings.queryHistory ?? []).filter((x) => x !== q)].slice(0, 20);
  saveSettings();
}

/** Suggestions for the token at the caret: field values, field names, or (empty box) the history. */
function quickSuggestions(): Suggestion[] {
  const t = active;
  const text = quickInput.value;
  const pick = (q: string) => {
    quickInput.value = q;
    commitQuick();
    saveHistory(q);
    ac.close();
  };
  if (!text.trim()) return (settings.queryHistory ?? []).map((q) => ({ label: q, accept: () => pick(q) }));
  if (!t) return [];
  const [start, len] = tokenAt(text, quickInput.selectionStart ?? text.length);
  const token = text.slice(start, start + len);
  const [prefix, field, op, value] = splitToken(token);
  const replace = (with_: string) => {
    quickInput.value = text.slice(0, start) + with_ + text.slice(start + len);
    const caret = start + with_.length;
    quickInput.setSelectionRange(caret, caret);
  };
  /** A suggestion that completes the token: `commit` applies the query, otherwise the list moves on. */
  const item = (label: string, hint: string | undefined, with_: string, commit: boolean): Suggestion => ({
    label,
    hint,
    accept: () => {
      replace(with_);
      if (!commit) return ac.update();
      ac.close();
      commitQuick();
      saveHistory(quickInput.value);
    },
  });
  const stats = t.fieldStats?.fields ?? [];
  const isTime = op === ':' && (field?.toLowerCase() === 'since' || field?.toLowerCase() === 'until');
  if (field !== null && !isTime) {
    const lk = field.toLowerCase();
    const values: [string, number][] =
      lk === 'level'
        ? LEVEL_NAMES.map((n, i): [string, number] => [n.toLowerCase(), t.info.levelCounts[i] ?? 0]).filter(([, c], i) => i > 0 && c > 0)
        : stats.find((f) => f.key.toLowerCase() === lk)?.top ?? [];
    const needle = value.toLowerCase();
    return values
      .filter(([v]) => v.toLowerCase().includes(needle))
      .slice(0, 10)
      .map(([v, c]) => item(prefix + fieldToken(field, v, op), fmtNum(c), prefix + fieldToken(field, v, op) + ' ', true));
  }
  // since:/until: offer relative times.
  if (field !== null) return ['-15m', '-1h', '-24h', 'today'].filter((r) => r.startsWith(value.toLowerCase())).map((r) => item(`${prefix}${field}:${r}`, 'time', `${prefix}${field}:${r} `, true));
  const keys = [...new Map(['level', ...stats.map((f) => f.key)].map((k) => [k.toLowerCase(), k])).values()];
  if (/^exists\(/i.test(value)) {
    const part = value.slice(7).replace(/\)+$/, '').toLowerCase();
    return keys
      .filter((k) => k.toLowerCase().startsWith(part))
      .slice(0, 10)
      .map((k) => item(`${prefix}exists(${k})`, 'has field', `${prefix}exists(${k}) `, true));
  }
  if (!value) return [];
  const needle = value.toLowerCase();
  const exact = keys.find((k) => k.toLowerCase() === needle);
  // A complete field name: offer the operators.
  if (exact) return [...OPERATORS.map((o) => item(prefix + exact + o, operatorHint(o), prefix + exact + o, false)), item(`${prefix}exists(${exact})`, 'has field', `${prefix}exists(${exact}) `, true)];
  const out = keys.filter((k) => k.toLowerCase().startsWith(needle)).slice(0, 10).map((k) => item(`${prefix}${k}:`, 'field', `${prefix}${k}:`, false));
  for (const kw of ['since:', 'until:', 'exists('])
    if (kw.startsWith(needle) && out.length < 10) out.push(item(prefix + kw, kw === 'exists(' ? 'has field' : 'time', prefix + kw, false));
  return out;
}

const ac = new Autocomplete($('#quick-ac'), quickSuggestions);

// A bare `field:` still being typed filters nothing (see parseQueryExpr), so committing as you type is safe.
const quickSoon = debounce(() => commitQuick(), 300);
quickInput.addEventListener('input', () => {
  quickSoon();
  ac.update();
});
quickInput.addEventListener('focus', () => !quickInput.value && ac.update());
quickInput.addEventListener('blur', () => {
  ac.close();
  if (active?.filter.query) saveHistory(active.filter.query);
});
quickInput.addEventListener('keydown', (e) => {
  if (e.key === 'ArrowDown') {
    e.preventDefault();
    if (ac.open) ac.move(1);
    else ac.update();
  } else if (e.key === 'ArrowUp') {
    if (!ac.open) return;
    e.preventDefault();
    ac.move(-1);
  } else if (e.key === 'Enter') {
    e.preventDefault();
    if (ac.open && ac.acceptCurrent()) return;
    ac.close();
    commitQuick();
    saveHistory(quickInput.value);
  } else if (e.key === 'Tab') {
    if (ac.open && ac.acceptCurrent()) e.preventDefault();
  } else if (e.key === 'Escape') {
    if (ac.open) {
      ac.close();
      return;
    }
    quickInput.value = '';
    quickSoon();
    grid.root.focus();
  }
});

// ----------------------------------------------------------------- bookmarks

const firstLine = (s: string) => {
  const i = s.indexOf('\n');
  return (i < 0 ? s : s.slice(0, i)).slice(0, 160);
};

function persistBookmarks(tab: Tab) {
  if (isPartial(tab)) return;
  const bookmarks = (settings.bookmarks ??= {});
  for (const [source, src] of tab.info.sources.entries()) {
    const list = [...tab.bookmarks.values()].filter((b) => b.source === source).sort((a, b) => a.line - b.line).map((b) => ({ line: b.line, note: b.note }));
    if (list.length) bookmarks[src.path] = list;
    else delete bookmarks[src.path];
  }
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
  grid.root.focus();
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
  grid.root.focus();
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

// ---------------------------------------------------------------- workspaces

function setWorkspaceTitle(file: string | null) {
  document.title = file ? `${baseName(file)} — UltraLogViewer` : 'UltraLogViewer';
}

function rememberWorkspace(file: string) {
  settings.recentWorkspaces = [file, ...(settings.recentWorkspaces ?? []).filter((p) => p !== file)].slice(0, 10);
  if (!active) renderRecent();
}

/** Saves every open tab, its filters, search, bookmarks and columns, and the highlight rules. */
async function saveWorkspace(file?: string | null) {
  if (!tabs.length) return toast('Open a file first: a workspace saves the open tabs');
  file ??= await ulv().workspaceSaveDialog(`${(active?.info.title ?? 'logs').replace(/\.(log|txt|gz)$/i, '')}.ulvw`);
  if (!file) return;
  const input = tabs.map((t) => {
    return {
      paths: t.paths,
      formatId: t.formatId,
      encoding: t.encoding ?? 'auto',
      filter: t.filter,
      search: t === active ? currentPattern() : t.search.pattern,
      bookmarks: [...t.bookmarks.values()].map((b) => ({ path: t.info.sources[b.source]?.path ?? t.paths[b.source] ?? t.paths[0], line: b.line, note: b.note })),
      extraColumns: extrasOf(t),
      columnOrder: orderOf(t) ?? [],
      hiddenColumns: hiddenOf(t),
    };
  });
  const ws = serializeWorkspace(input, active ? tabs.indexOf(ownerOf(active)!) : 0, settings.highlights ?? [], file);
  try {
    await ulv().writeWorkspace(file, JSON.stringify(ws, null, 2));
  } catch (e) {
    return toast((e as Error).message, true);
  }
  rememberWorkspace(file);
  setWorkspaceTitle(file);
  toast(`Workspace saved (${tabs.length} ${tabs.length === 1 ? 'tab' : 'tabs'})`);
}

/** Opens the tabs of a workspace (next to the open ones) and restores its highlights. */
async function openWorkspace(file?: string | null) {
  file ??= await ulv().workspaceOpenDialog();
  if (!file) return;
  let ws;
  try {
    ws = parseWorkspace<HighlightRule>(await ulv().readWorkspace(file));
  } catch (e) {
    return toast(`${baseName(file)}: ${(e as Error).message}`, true);
  }
  const dir = dirName(file);
  const candidates = [...new Set(ws.tabs.flatMap((t) => [...t.paths, ...t.relPaths.map((r) => resolvePath(dir, r))]))];
  const found = await ulv().filesExist(candidates);
  const existing = new Set(candidates.filter((_, i) => found[i]));
  const r = resolveWorkspace(ws, file, (p) => existing.has(p));
  settings.highlights = r.highlights.map((x) => ({ ...x }));
  saveSettings();
  for (const g of visibleGrids()) g.refreshPatterns();
  const opened: Tab[] = [];
  for (const wt of r.tabs) {
    const tab = await openTab(wt.paths, { format: wt.format, encoding: wt.encoding });
    tab.filter = { ...defaultFilter(), ...wt.filter, bookmarks: [], clearedBefore: null };
    tab.search.pattern = wt.search;
    tab.pending = { bookmarks: wt.bookmarks, columns: { extraColumns: wt.extraColumns, columnOrder: wt.columnOrder, hiddenColumns: wt.hiddenColumns } };
    opened.push(tab);
  }
  if (r.missing) toast(`Skipped ${r.missing} missing file(s)`, true);
  void ulv().addRecentWorkspace(file);
  rememberWorkspace(file);
  setWorkspaceTitle(file);
  const target = opened[r.active];
  if (target) {
    setSearchBox(target.search.pattern);
    activate(target);
  } else {
    renderTabs();
  }
}

/** Workspace settings that need the opened document (bookmark keys, the format's column settings). */
async function applyPending(tab: Tab) {
  const p = tab.pending!;
  tab.pending = undefined;
  if (p.columns) {
    const fmt = formatName(tab);
    if (p.columns.extraColumns.length) (settings.extraColumns ??= {})[fmt] = [...p.columns.extraColumns];
    else delete settings.extraColumns?.[fmt];
    if (p.columns.columnOrder.length) (settings.columnOrder ??= {})[fmt] = [...p.columns.columnOrder];
    else delete settings.columnOrder?.[fmt];
    settings.hiddenColumns = [...new Set([...(settings.hiddenColumns ?? []), ...p.columns.hiddenColumns])];
    if (tab === active) columnsChanged();
    else saveSettings();
  }
  for (const b of p.bookmarks) {
    // Saved paths are source paths (a zip member is `archive.zip::member`); a single-file tab takes older entries too.
    let source = tab.info.sources.findIndex((src) => src.path === b.path);
    if (source < 0) source = tab.info.sources.length === 1 ? 0 : tab.paths.indexOf(b.path);
    if (source < 0) continue;
    const key = await engine.keyForLine(tab.id, source, b.line);
    if (!key) continue;
    const rows = await engine.rows(tab.id, (await engine.rowForKey(tab.id, key, true)) ?? 0, 1);
    tab.bookmarks.set(key, { key, line: b.line, source, note: b.note, preview: firstLine(rows[0]?.fields.message || rows[0]?.text || '') });
  }
  if (p.bookmarks.length) {
    persistBookmarks(tab);
    await resolveBookmarkRows(tab);
  }
}

async function openCommandPalette() {
  const items = await ulv().menuItems();
  openPalette(items, (c) => void ulv().runMenuItem(c.id), ulv().platform === 'darwin');
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
  if (t.follow) t.newRows = 0;
  renderPill();
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
    case 'c':
    case 'C':
      void toggleContext(t);
      return true;
    case 'Escape':
      if (!t.contextMode) return false;
      void setContextMode(t, false);
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
  if (t?.showPatterns) return detail.renderPattern(patternsView.selected(), t.patterns?.scanned ?? 0);
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
  const fmt = formatSummary(t);
  if (fmt) parts.push(`<a class="fmt" title="${esc(fmt.title)}">${esc(fmt.label)}${fmt.auto ? '<i>auto</i>' : ''}</a>`);
  if (src) parts.push(`<a class="enc" title="Change encoding">${esc(src.encoding)}</a>${src.compressed ? '<span>gzip</span>' : ''}`);
  parts.push(`<span>${fmtBytes(size)}</span>`, `<span>${fmtNum(lines)} lines · ${fmtNum(i.total)} entries</span>`);
  const grouped = (t.filter.groupRepeats ?? 'off') !== 'off';
  if (t.contextMode) parts.push(`<span title="Show in context: press C or Esc to go back to the filtered view">All <b>${fmtNum(i.viewCount)}</b> entries (filters ignored)</span>`);
  else if (i.viewCount !== i.total || grouped) parts.push(`<span><b>${fmtNum(i.viewCount)}</b> shown${grouped ? ' · grouped' : ''}</span>`);
  const cleared = t.filter.clearedBefore?.reduce((n, c) => n + c, 0);
  if (cleared != null) parts.push(`<span class="cleared">${fmtNum(cleared)} cleared · <a class="show-cleared" title="Show the cleared entries again">Show</a></span>`);
  const sel = Math.abs(t.focus - t.anchor) + 1;
  if (i.viewCount) parts.push(`<span>Row ${fmtNum(t.focus + 1)}${sel > 1 ? ` · ${fmtNum(sel)} selected` : ''}</span>`);
  parts.push('<span class="sp"></span>');
  const busy = t.progress ?? (i.indexing ? { phase: 'index', fraction: i.progress } : undefined);
  if (busy) {
    const label = { index: 'Indexing', filter: 'Filtering', search: 'Searching', export: 'Exporting' }[busy.phase] ?? busy.phase;
    parts.push(`<span>${label} ${Math.round(busy.fraction * 100)}%</span><span class="prog"><i style="width:${(busy.fraction * 100).toFixed(1)}%"></i></span>`);
  } else if (t.scanCost) parts.push(`<span class="scan-cost muted">${esc(t.scanCost)}</span>`);
  if (i.sources.some((s) => s.live)) parts.push(`<span class="live">● live${t.follow ? ' · following' : ''}</span>`);
  el.innerHTML = parts.join('');
  renderPaneHeads();
  el.querySelector('.enc')?.addEventListener('click', chooseEncoding);
  el.querySelector('.fmt')?.addEventListener('click', openFormatPicker);
  syncAutoFormatOption();
  syncTimeless();
  el.querySelector('.show-cleared')?.addEventListener('click', () => {
    if (!active) return;
    active.filter.clearedBefore = null;
    void applyFilter(active);
  });
}

function renderToolbar() {
  $('#btn-follow').classList.toggle('on', !!active?.follow);
  $('#btn-hist').classList.toggle('on', settings.showHistogram !== false);
  $('#btn-detail').classList.toggle('on', settings.showDetail !== false);
  $('#btn-patterns').classList.toggle('on', !!active?.showPatterns);
}

// -------------------------------------------------------------------- layout

type LayoutKey = 'showHistogram' | 'showDetail' | 'showSidebar';
/** The open file has no timestamps at all (plain text): the timeline and time filters have nothing to show. */
const timeless = (t: Tab | null) => !!t && !t.info.indexing && t.info.total > 0 && t.info.timeMin == null;
let wasTimeless = false;

function syncTimeless() {
  if (timeless(active) === wasTimeless) return;
  applyLayout();
  if (active) sidebar.render(active);
}

function applyLayout() {
  const v = viewSettings(settings);
  wasTimeless = timeless(active);
  const hist = settings.showHistogram !== false && !wasTimeless;
  $('#histogram').classList.toggle('hidden', !hist);
  $('#hist-split').classList.toggle('hidden', !hist);
  const histBtn = $<HTMLButtonElement>('#btn-hist');
  histBtn.disabled = wasTimeless;
  histBtn.title = wasTimeless ? 'Timeline: this file has no timestamps' : 'Timeline (Ctrl+Shift+H)';
  histogram.setHeight(v.histogramHeight);
  $('#detail').classList.toggle('hidden', settings.showDetail === false);
  $('#sidebar').classList.toggle('hidden', settings.showSidebar === false);
  $('#work').classList.toggle('dock-right', v.detailDock === 'right');
  $('#detail').style.height = settings.detailHeight ? `${settings.detailHeight}px` : '';
  $('#detail').style.width = v.detailDock === 'right' ? `${v.detailWidth}px` : '';
  renderToolbar();
}

// ------------------------------------------------------------ view settings

type ViewKey = 'tintRows' | 'tokenColors' | 'density' | 'wrap' | 'detailDock' | 'timeMode' | 'timeUtc' | 'gapMs' | 'foldFrames';

/** Changes a display setting, persists it and redraws what it affects. */
function setView<K extends ViewKey>(key: K, value: Settings[K]) {
  settings[key] = value;
  saveSettings();
  if (key === 'detailDock') {
    applyLayout();
    if (active) void updateDetail();
    else detail.render(null, undefined);
    return;
  }
  if (key === 'foldFrames') void updateDetail();
  if (key === 'tintRows' || key === 'density' || key === 'wrap' || key === 'foldFrames') {
    for (const g of visibleGrids()) g.applyView();
    if (active) grid.reveal(active.focus);
    return;
  }
  for (const g of visibleGrids()) g.render();
}

const TIME_MODES: [TimeMode, string][] = [
  ['original', 'Time as written'],
  ['full', 'Full date and time'],
  ['time', 'Time of day'],
  ['relative', 'Relative to selected row'],
  ['delta', 'Delta from previous row'],
];

/** The Time column items (header right-click and View > Time column). */
function timeMenuItems(): MenuItem[] {
  const v = viewSettings(settings);
  return [
    ...TIME_MODES.map(([mode, label]): MenuItem => ({ label, checked: v.timeMode === mode, action: () => setView('timeMode', mode) })),
    'sep',
    { label: 'Show in UTC', checked: v.timeUtc, action: () => setView('timeUtc', !v.timeUtc) },
    { label: 'Mark gaps over 10 s', checked: v.gapMs > 0, action: () => setView('gapMs', v.gapMs > 0 ? 0 : 10000) },
  ];
}

function toggleLayout(key: LayoutKey) {
  // no timeline to show: leave the preference alone for files that have timestamps
  if (key === 'showHistogram' && timeless(active)) return;
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
  for (const g of visibleGrids()) g.render();
  if (active) void refreshOverview(active);
}

// ------------------------------------------------------------------- formats

function renderFormatOptions() {
  formatSel.innerHTML = '';
  formatSel.append(h('option', { value: '' }, 'Format: auto'));
  for (const id of ['timestamped', 'json', 'syslog', 'syslog5424', 'access', 'logcat', 'logfmt', 'postgres', 'mysql-error', 'mysql-general', 'mysql-slow', 'mongodb', 'rust', 'macos', 'laravel', 'plain'])
    formatSel.append(h('option', { value: id }, FORMAT_LABELS[id]));
  for (const c of settings.customFormats ?? []) formatSel.append(h('option', { value: `custom:${c.name}` }, `Custom: ${c.name}`));
  formatSel.append(h('option', { value: '__new' }, '+ Custom format…'));
  formatSel.value = active?.formatId ?? '';
  syncAutoFormatOption();
}

/** The tab's format: one label (or "Mixed formats" for merged files that differ) and per-file detection details. */
function formatSummary(t: Tab): { label: string; title: string; auto: boolean } | null {
  const srcs = t.info.sources;
  if (!srcs.length) return null;
  const labels = [...new Set(srcs.map((s) => s.formatLabel))];
  const lines = srcs.map((s) => {
    const how = !s.formatAuto
      ? `chosen by you${s.formatId === 'plain' ? '' : `; ${fmtNum(s.formatMatched)} of ${fmtNum(s.formatSampled)} sampled lines start an entry`}`
      : s.formatId === 'plain'
        ? 'detected: no known log layout, so each line is one entry'
        : `detected: ${fmtNum(s.formatMatched)} of ${fmtNum(s.formatSampled)} sampled lines start an entry`;
    return `${srcs.length > 1 ? `${s.displayName}: ` : ''}${s.format}\n${srcs.length > 1 ? '  ' : ''}${how}`;
  });
  return {
    label: labels.length > 1 ? 'Mixed formats' : labels[0],
    title: `Log format\n\n${lines.join('\n')}\n\nClick to choose another format.`,
    auto: srcs.every((s) => s.formatAuto),
  };
}

/** "Format: auto" names what auto-detection picked for the active tab. */
function syncAutoFormatOption() {
  const opt = formatSel.querySelector<HTMLOptionElement>('option[value=""]');
  if (!opt) return;
  const fmt = active && active.formatId === '' ? formatSummary(active) : null;
  const text = fmt ? `Auto: ${fmt.label}` : 'Format: auto';
  if (opt.textContent !== text) opt.textContent = text;
  formatSel.title = fmt ? fmt.title : 'Log format';
}

function openFormatPicker() {
  formatSel.focus();
  try {
    formatSel.showPicker();
  } catch {
    /* not allowed without a user gesture: focusing it is enough */
  }
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
  // Duplicates and split panes read the same files, so the engine re-parsed them too.
  for (const o of allTabs()) if (o !== t && o.paths === t.paths) o.formatId = t.formatId;
  t.contextMode = t.info.ignoreFilter;
  renderBanner();
  refreshFieldsSoon(t);
  if (t.showPatterns) refreshPatternsSoon(t);
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
    const hidden = new Set(hiddenOf(t));
    const list = h('div', { class: 'form' });
    for (const c of effectiveColumns(t)) {
      const cb = h('input', { type: 'checkbox', checked: !hidden.has(c.key) });
      cb.onchange = () => setColumnShown(c.key, cb.checked);
      list.append(h('label', {}, cb, c.title));
    }
    box.append(h('h2', {}, 'Columns'), h('div', { class: 'hint' }, 'Choose which columns to show. Drag a column edge in the header to resize it; right-click the header to reorder.'), list, h('div', { class: 'actions' }, h('button', { class: 'btn primary', onclick: close }, 'Done')));
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
  t = ownerOf(t)!;
  await closeSplit(t);
  t.encoding = encoding;
  await engine.close(t.id);
  t.id = await engine.open(t.paths, {
    encoding: encoding as 'auto',
    format: t.formatId || undefined,
    customFormats: settings.customFormats,
    cache: settings.indexCache !== false,
    window: t.window ?? null,
  });
  t.info = placeholderInfo(t.id, t.info.title);
  t.search.result = null;
  t.contextMode = false;
  t.fieldStats = null;
  t.expanded = new Set();
  t.unfolded = new Set();
  t.patterns = null;
  if (ownerOf(active) === t) activate(t);
}

// ---------------------------------------------------------------------- help

function showHelp() {
  const keys: [string, string][] = [
    ['Ctrl+O / Ctrl+Shift+O', 'Open files / open and merge into one timeline'],
    ['Ctrl+F, /', 'Find'],
    ['Enter, F3, n / Shift+Enter, Shift+F3, N', 'Next / previous match'],
    ['Ctrl+L', 'Quick filter (syntax below)'],
    ['e / E', 'Next / previous error'],
    ['w / W', 'Next / previous warning'],
    ['Ctrl+B, b, double-click', 'Toggle bookmark'],
    ['C', 'Show in context / back to filtered view'],
    ['Right-click', 'Filter by a value'],
    ['F2 / Shift+F2', 'Next / previous bookmark'],
    ['Ctrl+G', 'Go to line'],
    ['Ctrl+T, f, End', 'Follow new lines (tail)'],
    ['Shift+click, Shift+arrows', 'Select a range (status bar shows the time between rows)'],
    ['Ctrl+C', 'Copy selected entries'],
    ['Ctrl+E', 'Export the current view (.log, .csv, .jsonl)'],
    ['Ctrl+W, Ctrl+Tab', 'Close tab, next tab'],
    ['Ctrl+Shift+T', 'Duplicate tab (same file, no reindexing)'],
    ['Ctrl+\\', 'Split right / close split'],
    ['Alt+Z', 'Wrap long messages'],
    ['→ / ←', 'Expand / collapse a multi-line entry'],
    ['Ctrl+Shift+P, F1', 'Command palette'],
    ['Ctrl+Shift+A', 'Patterns view'],
    ['Ctrl+K', 'Clear (hide everything so far)'],
    ['Ctrl+click a token', 'Search for it'],
    ['Ctrl+Alt+S', 'Save workspace'],
    ['Timeline: click / drag', 'Jump to time / filter to a time range'],
  ];
  // The quick-filter syntax, shown under the shortcuts.
  const syntax: [string, string][] = [
    ['error timeout', 'Entries containing both words'],
    ['a OR b, a | b', 'Either; OR binds tighter than AND, so a b OR c means a and (b or c)'],
    ['-word, NOT word', 'Without the word'],
    ['(a or b) and not c', 'and / or / not in any case, with parentheses'],
    ['"exact phrase", /regex/', 'A phrase or a regular expression'],
    ['logger:Db, level:error', 'A parsed field contains the value (field:"two words", field:/re/)'],
    ['status>=500, status!=200', 'Compare a field: > >= < <= == != (numbers numerically, other values as text)'],
    ['duration>2s', 'Durations with ms, s, m or h; a bare number is read in the unit you typed'],
    ['level>=warn', 'Levels compare by severity'],
    ['user in (alice,bob)', 'The field equals one of the values'],
    ['exists(traceId), env?=prod', 'The field is present / equals the value or is missing'],
    ['since:-3h, until:-10m', 'Relative to now; also since:2024-05-01T10:00, since:today'],
  ];
  modal((box, close) => {
    const table = (rows: [string, string][]) => {
      const t = h('table', { class: 'keys' });
      for (const [k, d] of rows) {
        const tr = document.createElement('tr');
        tr.innerHTML = `<td><kbd>${esc(k)}</kbd></td><td>${esc(d)}</td>`;
        t.append(tr);
      }
      return t;
    };
    box.classList.add('help');
    box.append(
      h('h2', {}, 'Keyboard shortcuts'),
      table(keys),
      h('h2', { class: 'sub' }, 'Quick filter'),
      table(syntax),
      h('div', { class: 'actions' }, h('button', { class: 'btn primary', onclick: close }, 'Close')),
    );
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
$('#btn-patterns').onclick = () => active && setPatternsShown(active, !active.showPatterns);
$('#btn-clear').onclick = () => active && clearView(active);
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
    case 'open-window':
      return void openPaths((args[0] as string[]) ?? [], false, (args[1] as OpenWindow) ?? null);
    case 'open-last':
      return void openWindowDialog(false);
    case 'open-range':
      return void openWindowDialog(true);
    case 'toggle-index-cache':
      settings.indexCache = settings.indexCache === false;
      saveSettings();
      return toast(settings.indexCache ? 'Indexes of big files are saved, so they reopen instantly' : 'Index cache turned off');
    case 'clear-index-cache':
      return void engine.clearIndexCache().then((n) => toast(`Index cache cleared (${fmtBytes(n)})`));
    case 'export':
      return void exportView();
    case 'close-tab':
      return active && void closeTab(active);
    case 'duplicate-tab':
      return void duplicateTab(active);
    case 'split-right':
      return void splitRight(active);
    case 'close-split':
      return void closeSplit(ownerOf(active));
    case 'find':
      searchInput.focus();
      return searchInput.select();
    case 'quick-filter':
      return focusQuick();
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
    case 'toggle-tint':
      return setView('tintRows', !viewSettings(settings).tintRows);
    case 'toggle-tokens':
      return setView('tokenColors', !viewSettings(settings).tokenColors);
    case 'toggle-density':
      return setView('density', viewSettings(settings).density === 'comfortable' ? 'compact' : 'comfortable');
    case 'toggle-wrap':
      return setView('wrap', !viewSettings(settings).wrap);
    case 'toggle-dock':
      return setView('detailDock', viewSettings(settings).detailDock === 'right' ? 'bottom' : 'right');
    case 'time-mode':
      if (TIME_MODES.some(([m]) => m === args[0])) setView('timeMode', args[0] as TimeMode);
      return;
    case 'toggle-utc':
      return setView('timeUtc', !viewSettings(settings).timeUtc);
    case 'toggle-gaps':
      return setView('gapMs', viewSettings(settings).gapMs > 0 ? 0 : 10000);
    case 'help':
      return showHelp();
    case 'palette':
      return void openCommandPalette();
    case 'toggle-patterns':
      return active && setPatternsShown(active, !active.showPatterns);
    case 'group-repeats':
      if (active && (args[0] === 'off' || args[0] === 'exact' || args[0] === 'pattern')) setGroupRepeats(active, args[0]);
      return;
    case 'toggle-fold':
      return setView('foldFrames', !viewSettings(settings).foldFrames);
    case 'clear-view':
      return active && clearView(active);
    case 'clear-filters':
      return active && clearAllFilters(active);
    case 'open-workspace':
      return void openWorkspace();
    case 'open-workspace-path':
      return void openWorkspace(String(args[0]));
    case 'save-workspace':
      return void saveWorkspace();
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
    const i = tabs.indexOf(ownerOf(active)!);
    activate(tabs[(i + (e.shiftKey ? -1 : 1) + tabs.length) % tabs.length]);
  } else if (e.code === 'KeyT' && (e.ctrlKey || e.metaKey) && e.shiftKey && !e.altKey) {
    e.preventDefault();
    void duplicateTab(active);
  } else if (e.code === 'Backslash' && (e.ctrlKey || e.metaKey) && !e.shiftKey && !e.altKey) {
    e.preventDefault();
    toggleSplit();
  } else if (e.key === 'Escape' && active?.showPatterns && !(e.target instanceof HTMLInputElement || e.target instanceof HTMLTextAreaElement || e.target instanceof HTMLSelectElement)) {
    e.preventDefault();
    setPatternsShown(active, false);
    grid.root.focus();
  } else if (e.key === 'F1' || (e.code === 'KeyP' && (e.ctrlKey || e.metaKey) && e.shiftKey && !e.altKey)) {
    e.preventDefault();
    void openCommandPalette();
  } else if (e.code === 'KeyA' && (e.ctrlKey || e.metaKey) && e.shiftKey && !e.altKey) {
    e.preventDefault();
    if (active) setPatternsShown(active, !active.showPatterns);
  } else if (e.code === 'KeyK' && (e.ctrlKey || e.metaKey) && !e.shiftKey && !e.altKey) {
    e.preventDefault();
    if (active) clearView(active);
  } else if (e.altKey && !e.ctrlKey && !e.metaKey && (e.code === 'KeyZ' || e.key === 'z' || e.key === 'Z')) {
    e.preventDefault();
    setView('wrap', !viewSettings(settings).wrap);
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
  mainGrid.applyView(false);
  splitGrid.applyView(false);
  applyTheme();
  applyLayout();
  renderFormatOptions();
  activate(null);
  const startup = await ulv().startupOptions();
  if (startup.errors.length) toast(startup.errors.join('\n'), true);
  const files = await ulv().startupFiles();
  if (files.length) await openPaths(files, false, startup.window);
  // Piped input is the last startup file: follow it like a tail.
  if (startup.stdin && active && files.length) {
    active.follow = true;
    renderToolbar();
  }
  // test hook: lets automated UI tests drive the app without native dialogs
  (window as unknown as Record<string, unknown>).__ulv = { openPaths, saveWorkspace, openWorkspace, duplicateTab, splitRight, closeSplit, state: () => ({ tabs, active }) };
}

void init();


