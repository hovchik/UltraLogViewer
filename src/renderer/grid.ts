// Virtualised log table. Only the rows on screen exist in the DOM; rows are fetched from the
// engine on demand. Scroll positions are scaled so tens of millions of rows still fit within
// the browser's maximum element height.

import { fold, FoldLine } from '../core/folding';
import { compilePattern, matchRanges } from '../core/query';
import { formatTimeCell, formatGap } from '../core/timestamp';
import { tokenize } from '../core/tokenizer';
import type { ColumnDef, RowData } from '../core/types';
import { engine } from './api';
import { DENSITY, fmtRepeat, HighlightRule, Tab, ViewSettings } from './state';
import { esc } from './util';

const MAX_PX = 8_000_000;
const FETCH_MARGIN = 80;
const GUTTER_W = 74;
/** Continuation lines shown when a multi-line entry is expanded inline. */
const MAX_EXPAND = 40;
/** Appended rows are tinted with the accent colour, fading out over this many ms. */
const FLASH_MS = 1500;
const isMac = typeof navigator !== 'undefined' && /Mac/.test(navigator.platform);
/** Ctrl (Cmd on macOS) turns tokens into search links. */
const linkMod = (e: MouseEvent | KeyboardEvent) => (isMac ? e.metaKey : e.ctrlKey);

/** Lines of an entry's inline expansion (lines 2..), with framework frames folded unless turned off. */
export function expansionLines(row: RowData, view: ViewSettings, unfolded: Set<string>): FoldLine[] {
  const lines = row.text.split('\n').slice(1);
  if (!view.foldFrames || unfolded.has(row.key)) return lines.map((line) => ({ line, folded: 0 }));
  return fold(lines, view.frameworkPackages);
}

export interface GridHost {
  highlights(): HighlightRule[];
  /** Display settings (density, wrap, tint, tokens, time mode, gaps). */
  view(): ViewSettings;
  columnWidth(col: ColumnDef): number;
  hiddenColumns(): string[];
  /** Effective columns of a tab (format columns plus extra field columns, in the user's order). */
  columns(tab: Tab): ColumnDef[];
  /** `key` is the column that was right-clicked (null for the line gutter). */
  onHeaderMenu(e: MouseEvent, key: string | null): void;
  /** Right-click on a row; `col` is the clicked cell's column key (null for the line gutter). */
  onRowMenu(e: MouseEvent, row: RowData, col: string | null): void;
  setColumnWidth(key: string, width: number): void;
  onSelectionChange(): void;
  onTopRowChange(): void;
  /** Return true if the key was handled. */
  onKey(e: KeyboardEvent): boolean;
  toggleBookmark(row: RowData): void;
  copyRows(from: number, to: number): void;
  /** Ctrl+click on a token in the message column or an expanded line. */
  onTokenClick(row: RowData, text: string): void;
}

/** Text styling layers, lowest first: tokens, then highlight rules, then search hits. */
interface Layer {
  open: string;
  close: string;
}

export class Grid {
  private header: HTMLElement;
  private body: HTMLElement;
  private scroller: HTMLElement;
  private spacer: HTMLElement;
  private rowsEl: HTMLElement;
  private overviewEl: HTMLCanvasElement;
  private tab: Tab | null = null;
  private cache = new Map<number, RowData>();
  private fetching: string | null = null;
  private ignoreScroll = false;
  private overviewLevels: number[] = [];
  private searchRe: RegExp | null = null;
  private hlCompiled: { rule: HighlightRule; re: RegExp }[] = [];
  private rowH = 20;
  private lineH = 18;
  private gen = 0;
  /** Measured pixel heights of rows from the last paints (variable-height mode only). */
  private heights = new Map<number, number>();
  /** Rows fully visible in the last paint. */
  private lastFull = 0;
  private relayout = false;
  private bodyWidth = 0;
  private focusTime: { tab: number; gen: number; row: number; time: number | null } | null = null;
  /** Folded expansion lines per row key (dropped with the row cache). */
  private foldCache = new Map<string, FoldLine[]>();
  /** Appended view rows [from, to) and when they arrived, for the fade-out tint. */
  private flashes: { from: number; to: number; at: number }[] = [];
  private flashTimer: ReturnType<typeof setInterval> | null = null;

  constructor(readonly root: HTMLElement, private host: GridHost) {
    this.header = root.querySelector('.grid-header')!;
    this.body = root.querySelector('.grid-body')!;
    this.scroller = root.querySelector('.grid-scroll')!;
    this.spacer = root.querySelector('.grid-spacer')!;
    this.rowsEl = root.querySelector('.grid-rows')!;
    this.overviewEl = root.querySelector('.overview')!;

    this.scroller.addEventListener('scroll', () => this.onScroll());
    this.body.addEventListener('wheel', (e) => this.onWheel(e), { passive: false });
    this.rowsEl.addEventListener('mousedown', (e) => this.onMouseDown(e));
    this.rowsEl.addEventListener('dblclick', (e) => {
      const row = this.rowAt(e);
      if (row !== null && this.cache.get(row)) this.host.toggleBookmark(this.cache.get(row)!);
    });
    this.overviewEl.addEventListener('mousedown', (e) => this.onOverviewClick(e));
    this.header.addEventListener('contextmenu', (e) => {
      e.preventDefault();
      const key = ((e.target as HTMLElement).closest('.h') as HTMLElement | null)?.dataset.key ?? null;
      this.host.onHeaderMenu(e, key);
    });
    this.rowsEl.addEventListener('contextmenu', (e) => this.onContextMenu(e));
    this.rowsEl.addEventListener('mousemove', (e) => this.hoverToken(e));
    this.rowsEl.addEventListener('mouseleave', () => this.clearTokenHover());
    window.addEventListener('keyup', (e) => !linkMod(e) && this.clearTokenHover());
    window.addEventListener('blur', () => this.clearTokenHover());
    root.addEventListener('keydown', (e) => this.onKey(e));
    new ResizeObserver(() => {
      if (this.body.clientWidth !== this.bodyWidth) {
        this.bodyWidth = this.body.clientWidth;
        this.heights.clear();
      }
      this.syncScroll();
      this.render();
    }).observe(this.body);
    this.applyView(false);
  }

  /** Applies density, wrap and tint settings (call after they change). */
  applyView(redraw = true) {
    const v = this.host.view();
    const d = DENSITY[v.density] ?? DENSITY.compact;
    this.rowH = d.rowH;
    this.lineH = Math.round(d.fontSize * 1.4);
    const st = document.documentElement.style;
    st.setProperty('--row-h', `${this.rowH}px`);
    st.setProperty('--font-size', `${d.fontSize}px`);
    st.setProperty('--line-h', `${this.lineH}px`);
    this.root.classList.toggle('wrap', v.wrap);
    this.root.classList.toggle('tint', v.tintRows);
    this.heights.clear();
    this.foldCache.clear();
    if (!redraw) return;
    this.syncScroll();
    this.render();
  }

  // ---------------------------------------------------------------- state

  setTab(tab: Tab | null) {
    this.tab = tab;
    this.flashes = [];
    this.foldCache.clear();
    this.cache.clear();
    this.heights.clear();
    this.gen++;
    this.overviewLevels = [];
    this.renderHeader();
    this.refreshPatterns();
    this.syncScroll();
    this.render();
  }

  /** Drops cached rows (filter change, new data). */
  invalidate(keepCache = false) {
    if (!keepCache) {
      this.cache.clear();
      this.heights.clear();
      this.foldCache.clear();
      this.gen++;
    }
    this.syncScroll();
    this.render();
  }

  refreshPatterns() {
    const t = this.tab;
    this.searchRe = null;
    if (t?.search.pattern.text && t.search.result) {
      try {
        this.searchRe = compilePattern(t.search.pattern);
      } catch {
        /* invalid regex: no highlight */
      }
    }
    this.hlCompiled = [];
    for (const rule of this.host.highlights()) {
      if (!rule.enabled || !rule.text) continue;
      try {
        this.hlCompiled.push({ rule, re: compilePattern(rule) });
      } catch {
        /* skip invalid */
      }
    }
  }

  setOverview(levels: number[]) {
    this.overviewLevels = levels;
    this.drawOverview();
  }

  get count() {
    return this.tab?.info.viewCount ?? 0;
  }

  /** Rows that fit fully on screen (from the last paint when rows have different heights). */
  visibleRows() {
    if (this.variable() && this.lastFull) return this.lastFull;
    return this.fixedRows();
  }

  private fixedRows() {
    return Math.max(1, Math.floor(this.body.clientHeight / this.rowH));
  }

  /** True when rows can be taller than one line (soft wrap, or entries expanded inline). */
  private variable() {
    return !!this.tab && (this.host.view().wrap || this.tab.expanded.size > 0);
  }

  private maxTop() {
    return this.topForBottom(this.count - 1);
  }

  /** Expansion lines of a row (folded), cached per row key. */
  private expandLines(row: RowData): FoldLine[] {
    const t = this.tab!;
    const key = `${row.key}${t.unfolded.has(row.key) ? '+' : ''}`;
    let lines = this.foldCache.get(key);
    if (!lines) {
      lines = expansionLines(row, this.host.view(), t.unfolded);
      if (this.foldCache.size > 2000) this.foldCache.clear();
      this.foldCache.set(key, lines);
    }
    return lines;
  }

  /** Shows the framework frames of one entry (click on a "… N framework frames" line). */
  unfold(key: string) {
    const t = this.tab;
    if (!t || t.unfolded.has(key)) return;
    t.unfolded.add(key);
    this.heights.clear();
    this.syncScroll();
    this.render();
  }

  /** Tints view rows [from, to) as just appended; the tint fades out over 1.5 s. */
  flash(from: number, to: number) {
    if (to <= from) return;
    this.flashes.push({ from, to, at: performance.now() });
    if (this.flashTimer) return;
    this.flashTimer = setInterval(() => {
      const now = performance.now();
      this.flashes = this.flashes.filter((f) => now - f.at < FLASH_MS);
      if (!this.flashes.length && this.flashTimer) {
        clearInterval(this.flashTimer);
        this.flashTimer = null;
      }
      this.render();
    }, 50);
  }

  clearFlash() {
    this.flashes = [];
  }

  /** Flash tint opacity (0..0.18) of a view row. */
  private flashAlpha(r: number, now: number): number {
    let a = 0;
    for (const f of this.flashes) if (r >= f.from && r < f.to) a = Math.max(a, 0.18 * (1 - (now - f.at) / FLASH_MS));
    return a;
  }

  /** Height of the inline expansion of a row (0 when collapsed). */
  private expansionHeight(row: RowData): number {
    if (row.lineCount <= 1 || !this.tab?.expanded.has(row.key)) return 0;
    const n = this.expandLines(row).length;
    if (n <= 0) return 0;
    return (Math.min(n, MAX_EXPAND) + (n > MAX_EXPAND ? 1 : 0)) * this.lineH + 4;
  }

  /** Pixel height of a row: measured, or estimated (an uncached row counts as one compact line). */
  private heightOf(r: number): number {
    const m = this.heights.get(r);
    if (m !== undefined) return m;
    const row = this.cache.get(r);
    return row ? this.rowH + this.expansionHeight(row) : this.rowH;
  }

  /** The top row that makes `last` the last fully visible row. */
  private topForBottom(last: number): number {
    if (last < 0) return 0;
    if (!this.variable()) return Math.max(0, last - this.fixedRows() + 1);
    const avail = this.body.clientHeight;
    let used = 0;
    let r = last;
    while (r >= 0) {
      used += this.heightOf(r);
      if (used > avail) break;
      r--;
    }
    return Math.max(0, Math.min(last, r + 1));
  }

  // --------------------------------------------------------------- header

  /** Visible columns with resolved widths; exactly one column (0 width) stretches. */
  private cols(): { col: ColumnDef; width: number }[] {
    const hidden = this.host.hiddenColumns();
    const all = this.tab ? this.host.columns(this.tab) : [];
    let cols = all.filter((c) => !hidden.includes(c.key)).map((col) => ({ col, width: this.host.columnWidth(col) }));
    if (!cols.length) cols = all.map((col) => ({ col, width: this.host.columnWidth(col) }));
    if (cols.length && !cols.some((c) => c.width === 0)) cols[cols.length - 1].width = 0;
    return cols;
  }

  renderHeader() {
    const t = this.tab;
    this.header.innerHTML = '';
    if (!t) return;
    const gutter = document.createElement('div');
    gutter.className = 'h-gutter';
    gutter.textContent = 'Line';
    this.header.append(gutter);
    for (const { col, width: w } of this.cols()) {
      const el = document.createElement('div');
      el.className = w ? 'h' : 'h flex';
      el.dataset.key = col.key;
      if (w) el.style.width = `${w}px`;
      el.textContent = col.title;
      if (w) {
        const rs = document.createElement('div');
        rs.className = 'rs';
        rs.addEventListener('mousedown', (e) => this.startResize(e, col, el));
        el.append(rs);
      }
      this.header.append(el);
    }
  }

  private startResize(e: MouseEvent, col: ColumnDef, el: HTMLElement) {
    e.preventDefault();
    e.stopPropagation();
    const startX = e.clientX;
    const startW = el.offsetWidth;
    const move = (ev: MouseEvent) => {
      const w = Math.max(30, startW + ev.clientX - startX);
      el.style.width = `${w}px`;
      this.host.setColumnWidth(col.key, w);
      this.heights.clear();
      this.render();
    };
    const up = () => {
      window.removeEventListener('mousemove', move);
      window.removeEventListener('mouseup', up);
    };
    window.addEventListener('mousemove', move);
    window.addEventListener('mouseup', up);
  }

  // ------------------------------------------------------------- scrolling

  private totalPx() {
    return Math.min(MAX_PX, this.count * this.rowH + this.rowH);
  }

  /** Positions the native scrollbar to reflect tab.topRow. */
  syncScroll() {
    const t = this.tab;
    this.spacer.style.height = `${this.totalPx()}px`;
    if (!t) return;
    t.topRow = Math.max(0, Math.min(this.maxTop(), t.topRow));
    const range = this.scroller.scrollHeight - this.scroller.clientHeight;
    const max = this.maxTop();
    const want = max ? Math.round((t.topRow / max) * range) : 0;
    if (Math.abs(this.scroller.scrollTop - want) > 1) {
      this.ignoreScroll = true;
      this.scroller.scrollTop = want;
    }
  }

  private onScroll() {
    if (this.ignoreScroll) {
      this.ignoreScroll = false;
      return;
    }
    const t = this.tab;
    if (!t) return;
    const range = this.scroller.scrollHeight - this.scroller.clientHeight;
    const max = this.maxTop();
    const top = range > 0 ? Math.round((this.scroller.scrollTop / range) * max) : 0;
    if (top !== t.topRow) {
      t.topRow = top;
      t.follow = t.topRow >= max;
      this.host.onTopRowChange();
      this.render();
    }
  }

  private onWheel(e: WheelEvent) {
    const t = this.tab;
    if (!t || e.ctrlKey) return;
    e.preventDefault();
    const lines = e.deltaMode === 1 ? e.deltaY : e.deltaMode === 2 ? e.deltaY * this.visibleRows() : e.deltaY / this.rowH;
    const step = Math.sign(lines) * Math.max(1, Math.round(Math.abs(lines)));
    this.scrollTo(t.topRow + step);
  }

  scrollTo(top: number) {
    const t = this.tab;
    if (!t) return;
    const max = this.maxTop();
    t.topRow = Math.max(0, Math.min(max, top));
    t.follow = t.topRow >= max;
    this.syncScroll();
    this.host.onTopRowChange();
    this.render();
  }

  /** Scrolls so that `row` is visible. */
  reveal(row: number, align: 'nearest' | 'center' = 'nearest') {
    const t = this.tab;
    if (!t) return;
    const vis = this.visibleRows();
    if (align === 'center') this.scrollTo(row - Math.floor(vis / 2));
    else if (row < t.topRow) this.scrollTo(row);
    else if (row >= t.topRow + vis) this.scrollTo(this.topForBottom(row));
    else this.render();
  }

  // -------------------------------------------------------------- fetching

  private async ensureRows(from: number, to: number) {
    const t = this.tab;
    if (!t) return;
    let missing = false;
    for (let r = from; r < to; r++)
      if (!this.cache.has(r)) {
        missing = true;
        break;
      }
    if (!missing) return;
    const start = Math.max(0, from - FETCH_MARGIN);
    const end = Math.min(this.count, to + FETCH_MARGIN);
    const key = `${t.id}:${start}:${end}:${this.gen}`;
    if (this.fetching === key) return;
    this.fetching = key;
    const gen = this.gen;
    try {
      const rows = await engine.rows(t.id, start, end - start);
      if (gen !== this.gen || this.tab !== t) return;
      if (this.cache.size > 5000) this.cache.clear();
      for (const r of rows) this.cache.set(r.row, r);
    } catch {
      /* doc closed */
    } finally {
      if (this.fetching === key) this.fetching = null;
    }
    if (this.tab === t) this.render();
  }

  getCached(row: number): RowData | undefined {
    return this.cache.get(row);
  }

  // ---------------------------------------------------------------- render

  render() {
    const t = this.tab;
    if (!t) {
      this.rowsEl.innerHTML = '';
      this.drawOverview();
      return;
    }
    const view = this.host.view();
    const variable = this.variable();
    const vh = this.body.clientHeight;
    const from = t.topRow;
    const to = Math.min(this.count, from + Math.floor(vh / this.rowH) + 1);
    // One row before the top for the delta / gap of the first visible row.
    void this.ensureRows(Math.max(0, from - 1), to);
    const visible = this.cols();
    const cols = visible.map((c) => c.col);
    const widths = visible.map((c) => c.width);
    const selLo = Math.min(t.anchor, t.focus);
    const selHi = Math.max(t.anchor, t.focus);
    const hitRows = t.search.result?.rows;
    const curHit = hitRows && hitRows.length ? hitRows[t.search.current] : -1;
    const hasTime = cols.some((c) => c.key === 'time');
    const refTime = hasTime && view.timeMode === 'relative' ? this.focusRowTime() : null;
    const gapMs = view.gapMs > 0 ? view.gapMs : 0;
    const gutterFont = parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--font-size')) || 12.5;
    let expandX = -1;
    let html = '';
    const now = performance.now();
    // The ×N badge of grouped rows goes into the message column (or the stretching one).
    let badgeCol = cols.findIndex((c) => c.key === 'message');
    if (badgeCol < 0) badgeCol = widths.findIndex((w) => w === 0);
    for (let r = from; r < to; r++) {
      const row = this.cache.get(r);
      if (!row) {
        html += `<div class="r" data-row="${r}"><div class="g"></div></div>`;
        continue;
      }
      const prev = r > 0 ? this.cache.get(r - 1) : undefined;
      const prevTime = prev ? prev.time : null;
      const selected = r >= selLo && r <= selHi;
      const expanded = row.lineCount > 1 && t.expanded.has(row.key);
      const cls = ['r', `l${row.level}`];
      if (row.context) cls.push('ctx');
      if (selected) cls.push('sel');
      if (r === t.focus) cls.push('cur');
      const tall = variable && (view.wrap || expanded);
      if (tall) cls.push('var');
      if (expanded) cls.push('open');
      const isHit = hitRows ? binaryHas(hitRows, r) : false;
      if (isHit) cls.push('hitrow');
      const gap = gapMs && row.time !== null && prevTime !== null && row.time - prevTime >= gapMs ? row.time - prevTime : 0;
      if (gap) cls.push('gap');
      const styles: string[] = [];
      for (const { rule, re } of this.hlCompiled) {
        if (!rule.row) continue;
        re.lastIndex = 0;
        if (re.test(row.text)) {
          styles.push(`background-color:${rule.color}38`);
          break;
        }
      }
      const fa = this.flashes.length ? this.flashAlpha(r, now) : 0;
      if (fa > 0) {
        const tint = `color-mix(in srgb, var(--accent) ${(fa * 100).toFixed(1)}%, transparent)`;
        styles.push(`background-image:linear-gradient(${tint},${tint})`);
        cls.push('flash');
      }
      const rowStyle = styles.length ? ` style="${styles.join(';')}"` : '';
      // (a tinted Fatal row is white on red: token colours would be unreadable there)
      const tokens = view.tokenColors && !selected && !row.context && !(view.tintRows && row.level === 6);
      const bm = t.bookmarks.has(row.key) ? '<i class="bmk"></i>' : '';
      const more = row.lineCount > 1 ? `<span class="more" title="${expanded ? 'Collapse (←)' : 'Expand (→)'}">+${row.lineCount - 1}</span>` : '';
      let gapEl = '';
      if (gap) {
        const label = formatGap(gap);
        // Monospace estimate: label (9.5 px) at x 2, then the badge and line number right-aligned.
        const need = 2 + label.length * 9.5 * 0.6 + 4 + (row.lineCount > 1 ? (String(row.lineCount - 1).length + 1) * 6 + 3 : 0) + String(row.line).length * gutterFont * 0.6 + 6;
        gapEl = need <= GUTTER_W ? `<span class="gapl">${esc(label)}</span>` : '';
      }
      html += `<div class="${cls.join(' ')}" data-row="${r}"${rowStyle}>${tall ? '<div class="ln">' : ''}<div class="g" title="Line ${row.line}${row.lineCount > 1 ? `–${row.line + row.lineCount - 1}` : ''}${gap ? ` · gap ${formatGap(gap)}` : ''}">${gapEl}${bm}${more}${row.line}</div>`;
      for (let i = 0; i < cols.length; i++) {
        const c = cols[i];
        let v: string;
        if (c.key === '__source') v = t.info.sources[row.source]?.displayName ?? '';
        else if (c.key === 'time') v = formatTimeCell(view.timeMode, row.time, row.fields.time ?? '', { utc: view.timeUtc, ref: refTime, prev: r > 0 ? prevTime : null });
        else v = row.fields[c.key] ?? '';
        const isMsg = c.key === 'message';
        if (isMsg || widths[i] === 0) {
          const nl = v.indexOf('\n');
          if (nl >= 0) v = v.slice(0, nl);
        }
        if (v.length > 2000) v = v.slice(0, 2000) + '…';
        const w = widths[i];
        const style = w ? ` style="width:${w}px"` : '';
        let cc = w ? 'c' : 'c flex';
        if (isMsg) cc += ' msg';
        if (c.key === '__source') cc += ' src';
        let content = c.key === 'level' ? `<span class="lvtag">${esc(v)}</span>` : this.markup(v, r === curHit, isMsg && tokens);
        if (i === badgeCol && row.repeat > 1) content = `<span class="rep" title="Repeated ${row.repeat.toLocaleString('en-US')} times">${fmtRepeat(row.repeat)}</span>${content}`;
        if (isMsg && view.wrap) content = `<div class="wr">${content}</div>`;
        html += `<div class="${cc}" data-col="${esc(c.key)}"${style}>${content}</div>`;
      }
      if (tall) html += '</div>';
      if (expanded) {
        if (expandX < 0) expandX = this.expandX();
        html += this.expansion(row, expandX, tokens);
      }
      html += '</div>';
    }
    this.rowsEl.innerHTML = html;
    if (variable) this.measure(vh);
    else this.lastFull = 0;
    this.drawOverview();
  }

  /** Records row heights of the paint and keeps the top row in range (variable-height mode). */
  private measure(vh: number) {
    const t = this.tab!;
    let full = 0;
    for (const el of this.rowsEl.children as HTMLCollectionOf<HTMLElement>) {
      const r = Number(el.dataset.row);
      if (this.cache.has(r)) this.heights.set(r, el.offsetHeight);
      if (el.offsetTop + el.offsetHeight <= vh + 0.5) full++;
    }
    this.lastFull = Math.max(1, full);
    if (this.relayout) return;
    const max = this.maxTop();
    if ((t.follow && t.topRow !== max) || t.topRow > max) {
      this.relayout = true;
      try {
        t.topRow = max;
        this.syncScroll();
        this.render();
        this.host.onTopRowChange();
      } finally {
        this.relayout = false;
      }
    }
  }

  /** X of the message column (or the first column) where inline expansions start. */
  private expandX(): number {
    const h = (this.header.querySelector('.h[data-key="message"]') ?? this.header.querySelector('.h')) as HTMLElement | null;
    return h ? h.offsetLeft : GUTTER_W;
  }

  /** Lines 2.. of a multi-line entry, shown under its row. */
  private expansion(row: RowData, x: number, tokens: boolean): string {
    const lines = this.expandLines(row);
    let html = `<div class="rx" style="margin-left:${x}px">`;
    for (const { line: raw, folded } of lines.slice(0, MAX_EXPAND)) {
      if (folded) {
        html += `<div class="rl fold" title="Click to show all frames of this entry">${esc(raw)}</div>`;
        continue;
      }
      const line = raw.length > 2000 ? raw.slice(0, 2000) + '…' : raw;
      html += `<div class="rl">${line ? this.markup(line, false, tokens) : ' '}</div>`;
    }
    if (lines.length > MAX_EXPAND) html += `<div class="rl rx-more">… ${(lines.length - MAX_EXPAND).toLocaleString('en-US')} more lines</div>`;
    return html + '</div>';
  }

  /** Time of the focused row, for the relative time mode (fetched when it is not cached). */
  private focusRowTime(): number | null {
    const t = this.tab!;
    const row = this.cache.get(t.focus);
    if (row) {
      this.focusTime = { tab: t.id, gen: this.gen, row: t.focus, time: row.time };
      return row.time;
    }
    const f = this.focusTime;
    if (f && f.tab === t.id && f.gen === this.gen && f.row === t.focus) return f.time;
    const want = { tab: t.id, gen: this.gen, row: t.focus };
    void engine
      .rows(t.id, t.focus, 1)
      .then((rows) => {
        if (this.tab !== t || this.gen !== want.gen || t.focus !== want.row) return;
        this.focusTime = { ...want, time: rows[0]?.time ?? null };
        this.render();
      })
      .catch(() => undefined);
    return null;
  }

  /** Expands or collapses a multi-line entry inline. Returns false when the row has a single line. */
  setExpanded(r: number, on: boolean): boolean {
    const t = this.tab;
    const row = this.cache.get(r);
    if (!t || !row || row.lineCount <= 1) return false;
    if (on === t.expanded.has(row.key)) return true;
    if (on) t.expanded.add(row.key);
    else t.expanded.delete(row.key);
    this.heights.delete(r);
    this.syncScroll();
    this.render();
    if (on && r >= t.topRow) {
      // keep as much of the expansion on screen as possible
      const top = this.topForBottom(r);
      if (top > t.topRow && r >= t.topRow + this.visibleRows()) this.scrollTo(Math.min(r, top));
    }
    return true;
  }

  private markup(text: string, current: boolean, tokens = false): string {
    if (!text) return '';
    const hasHl = this.hlCompiled.some((h) => !h.rule.row);
    if (!this.searchRe && !hasHl && !tokens) return esc(text);
    const layers: Layer[] = [{ open: '', close: '' }];
    const at = new Uint16Array(text.length);
    let any = false;
    const paint = (s: number, e: number, layer: Layer) => {
      if (e <= s) return;
      layers.push(layer);
      at.fill(layers.length - 1, s, Math.min(e, text.length));
      any = true;
    };
    if (tokens) for (const tk of tokenize(text)) paint(tk.start, tk.start + tk.length, { open: `<span class="tk-${tk.kind}">`, close: '</span>' });
    // Earlier rules win over later ones, so paint them last.
    for (let k = this.hlCompiled.length - 1; k >= 0; k--) {
      const { rule, re } = this.hlCompiled[k];
      if (rule.row) continue;
      for (const [s, e] of matchRanges(re, text, 100)) paint(s, e, { open: `<mark style="background:transparent;color:${rule.color};font-weight:600">`, close: '</mark>' });
    }
    if (this.searchRe) for (const [s, e] of matchRanges(this.searchRe, text, 200)) paint(s, e, { open: current ? '<mark class="cur">' : '<mark>', close: '</mark>' });
    if (!any) return esc(text);
    let out = '';
    let i = 0;
    while (i < text.length) {
      const id = at[i];
      let j = i + 1;
      while (j < text.length && at[j] === id) j++;
      const l = layers[id];
      out += l.open + esc(text.slice(i, j)) + l.close;
      i = j;
    }
    return out;
  }

  // -------------------------------------------------------------- overview

  drawOverview() {
    const c = this.overviewEl;
    const dpr = window.devicePixelRatio || 1;
    const w = c.clientWidth, hgt = c.clientHeight;
    if (!w || !hgt) return;
    if (c.width !== w * dpr || c.height !== hgt * dpr) {
      c.width = w * dpr;
      c.height = hgt * dpr;
    }
    const ctx = c.getContext('2d')!;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, w, hgt);
    const t = this.tab;
    const n = this.count;
    if (!t || !n) return;
    const css = getComputedStyle(document.documentElement);
    // three lanes: warnings | errors | search hits
    const lane = w / 3;
    const lv = this.overviewLevels;
    if (lv.length) {
      const bh = hgt / lv.length;
      const warn = css.getPropertyValue('--warn'), err = css.getPropertyValue('--error');
      for (let i = 0; i < lv.length; i++) {
        const f = lv[i];
        if (f & 1) {
          ctx.fillStyle = warn;
          ctx.fillRect(0, i * bh, lane, Math.max(1, bh));
        }
        if (f & 2) {
          ctx.fillStyle = err;
          ctx.fillRect(lane, i * bh, lane, Math.max(1, bh));
        }
      }
    }
    const hits = t.search.result?.rows;
    if (hits?.length) {
      ctx.fillStyle = css.getPropertyValue('--hit-cur');
      let lastY = -2;
      for (const r of hits) {
        const y = Math.floor((r / n) * hgt);
        if (y === lastY) continue;
        lastY = y;
        ctx.fillRect(lane * 2, y, lane, 2);
      }
    }
    ctx.fillStyle = css.getPropertyValue('--bookmark');
    for (const b of t.bookmarks.values()) {
      const row = b.row;
      if (row === undefined || row === null) continue;
      ctx.fillRect(0, Math.floor((row / n) * hgt), w, 2);
    }
    const vis = this.visibleRows();
    ctx.strokeStyle = css.getPropertyValue('--fg-3');
    ctx.lineWidth = 1;
    const y0 = (t.topRow / n) * hgt;
    const vh = Math.max(4, (vis / n) * hgt);
    ctx.strokeRect(0.5, y0 + 0.5, w - 1, Math.min(hgt - y0 - 1, vh));
  }

  private onOverviewClick(e: MouseEvent) {
    const go = (ev: MouseEvent) => {
      const rect = this.overviewEl.getBoundingClientRect();
      const f = Math.max(0, Math.min(1, (ev.clientY - rect.top) / rect.height));
      this.scrollTo(Math.round(f * this.count - this.visibleRows() / 2));
    };
    go(e);
    const up = () => {
      window.removeEventListener('mousemove', go);
      window.removeEventListener('mouseup', up);
    };
    window.addEventListener('mousemove', go);
    window.addEventListener('mouseup', up);
  }

  // ------------------------------------------------------------- selection

  private rowAt(e: MouseEvent): number | null {
    const el = (e.target as HTMLElement).closest('.r') as HTMLElement | null;
    return el ? Number(el.dataset.row) : null;
  }

  private onMouseDown(e: MouseEvent) {
    const t = this.tab;
    const row = this.rowAt(e);
    if (!t || row === null || e.button !== 0) return;
    // We re-render the rows below, which detaches the event target; without this the browser's
    // default focus handling would then move focus to <body>.
    e.preventDefault();
    this.root.focus();
    if (linkMod(e) && !e.shiftKey) {
      const tk = this.tokenAt(e);
      const r = this.cache.get(row);
      if (tk && r) {
        this.clearTokenHover();
        this.host.onTokenClick(r, tk.text);
        return;
      }
    }
    const foldEl = (e.target as HTMLElement).closest('.rx .fold');
    if (foldEl && !e.shiftKey) {
      const r = this.cache.get(row);
      if (r) this.unfold(r.key);
      return;
    }
    if ((e.target as HTMLElement).closest('.more') && !e.shiftKey) {
      // the "+N" badge expands / collapses the entry inline
      const r = this.cache.get(row);
      if (r) {
        t.anchor = t.focus = row;
        this.setExpanded(row, !t.expanded.has(r.key));
        this.host.onSelectionChange();
      }
      return;
    }
    if ((e.target as HTMLElement).closest('.g') && !e.shiftKey && e.detail === 1 && (e.target as HTMLElement).closest('.g')?.querySelector('.bmk')) {
      // clicking a bookmark marker removes it
      const r = this.cache.get(row);
      if (r) this.host.toggleBookmark(r);
    }
    if (e.shiftKey) t.focus = row;
    else t.anchor = t.focus = row;
    this.render();
    this.host.onSelectionChange();
    // drag to extend selection
    const move = (ev: MouseEvent) => {
      const el = document.elementFromPoint(ev.clientX, ev.clientY)?.closest('.r') as HTMLElement | null;
      if (el && el.dataset.row) {
        const r = Number(el.dataset.row);
        if (r !== t.focus) {
          t.focus = r;
          this.render();
          this.host.onSelectionChange();
        }
      }
    };
    const up = () => {
      window.removeEventListener('mousemove', move);
      window.removeEventListener('mouseup', up);
    };
    window.addEventListener('mousemove', move);
    window.addEventListener('mouseup', up);
  }

  // ------------------------------------------------------- token links

  /** The token under the pointer in a message cell or an expanded line, with its text range. */
  private tokenAt(e: MouseEvent): { text: string; range: Range } | null {
    const cell = (e.target as HTMLElement).closest('.c.msg, .rx .rl:not(.fold):not(.rx-more)') as HTMLElement | null;
    if (!cell) return null;
    const caret = document.caretRangeFromPoint?.(e.clientX, e.clientY);
    if (!caret || !cell.contains(caret.startContainer)) return null;
    // text nodes of the cell, minus the ×N badge
    const nodes: { node: Text; start: number }[] = [];
    let text = '';
    const walker = document.createTreeWalker(cell, NodeFilter.SHOW_TEXT, {
      acceptNode: (n) => ((n.parentElement?.closest('.rep') ? NodeFilter.FILTER_REJECT : NodeFilter.FILTER_ACCEPT)),
    });
    let offset = -1;
    for (let n = walker.nextNode() as Text | null; n; n = walker.nextNode() as Text | null) {
      if (n === caret.startContainer) offset = text.length + caret.startOffset;
      nodes.push({ node: n, start: text.length });
      text += n.data;
    }
    if (offset < 0) return null;
    const tokens = tokenize(text);
    const tk = tokens.find((t) => offset >= t.start && offset < t.start + t.length) ?? tokens.find((t) => offset === t.start + t.length);
    if (!tk) return null;
    const locate = (pos: number, end: boolean): [Text, number] => {
      for (let i = nodes.length - 1; i >= 0; i--) {
        const { node, start } = nodes[i];
        if (end ? pos > start : pos >= start) return [node, Math.min(node.data.length, pos - start)];
      }
      return [nodes[0].node, 0];
    };
    const range = document.createRange();
    range.setStart(...locate(tk.start, false));
    range.setEnd(...locate(tk.start + tk.length, true));
    // the caret snaps to the nearest character: make sure the pointer is really over the token
    const hit = [...range.getClientRects()].some((rc) => e.clientX >= rc.left - 1 && e.clientX <= rc.right + 1 && e.clientY >= rc.top - 1 && e.clientY <= rc.bottom + 1);
    if (!hit) return null;
    let value = text.slice(tk.start, tk.start + tk.length);
    if (tk.kind === 'string' && value.length >= 2) value = value.slice(1, -1);
    return { text: value, range };
  }

  private hovering = false;

  private hoverToken(e: MouseEvent) {
    if (!linkMod(e)) {
      if (this.hovering) this.clearTokenHover();
      return;
    }
    const tk = this.tokenAt(e);
    if (!tk) return this.clearTokenHover();
    CSS.highlights?.set('tk-link', new Highlight(tk.range));
    this.rowsEl.classList.add('tk-link');
    this.hovering = true;
  }

  private clearTokenHover() {
    if (!this.hovering) return;
    this.hovering = false;
    CSS.highlights?.delete('tk-link');
    this.rowsEl.classList.remove('tk-link');
  }

  private onContextMenu(e: MouseEvent) {
    e.preventDefault();
    const t = this.tab;
    const r = this.rowAt(e);
    const row = r === null ? undefined : this.cache.get(r);
    if (!t || r === null || !row) return;
    const col = ((e.target as HTMLElement).closest('.c') as HTMLElement | null)?.dataset.col ?? null;
    this.root.focus();
    // Keep a multi-row selection when right-clicking inside it.
    if (r < Math.min(t.anchor, t.focus) || r > Math.max(t.anchor, t.focus)) {
      t.anchor = t.focus = r;
      this.render();
      this.host.onSelectionChange();
    }
    this.host.onRowMenu(e, row, col);
  }

  select(row: number, extend = false, align: 'nearest' | 'center' = 'nearest') {
    const t = this.tab;
    if (!t || !this.count) return;
    row = Math.max(0, Math.min(this.count - 1, row));
    t.focus = row;
    if (!extend) t.anchor = row;
    this.reveal(row, align);
    this.host.onSelectionChange();
  }

  private onKey(e: KeyboardEvent) {
    const t = this.tab;
    if (!t) return;
    if (this.host.onKey(e)) {
      e.preventDefault();
      return;
    }
    const page = this.visibleRows() - 1;
    const mod = e.ctrlKey || e.metaKey;
    switch (e.key) {
      case 'ArrowDown':
        this.select(t.focus + 1, e.shiftKey);
        break;
      case 'ArrowUp':
        this.select(t.focus - 1, e.shiftKey);
        break;
      case 'PageDown':
        this.select(t.focus + page, e.shiftKey);
        break;
      case 'PageUp':
        this.select(t.focus - page, e.shiftKey);
        break;
      case 'ArrowRight':
      case 'ArrowLeft':
        if (mod || e.altKey || e.shiftKey) return;
        this.setExpanded(t.focus, e.key === 'ArrowRight');
        break;
      case 'Home':
        this.select(0, e.shiftKey);
        break;
      case 'End':
        this.select(this.count - 1, e.shiftKey);
        t.follow = true;
        this.host.onTopRowChange();
        break;
      case 'c':
      case 'C':
        if (!mod) return;
        this.host.copyRows(Math.min(t.anchor, t.focus), Math.max(t.anchor, t.focus));
        break;
      case 'a':
        if (!mod) return;
        t.anchor = 0;
        t.focus = this.count - 1;
        this.render();
        this.host.onSelectionChange();
        break;
      default:
        return;
    }
    e.preventDefault();
  }
}

function binaryHas(arr: number[], v: number): boolean {
  let lo = 0, hi = arr.length - 1;
  while (lo <= hi) {
    const mid = (lo + hi) >> 1;
    if (arr[mid] === v) return true;
    if (arr[mid] < v) lo = mid + 1;
    else hi = mid - 1;
  }
  return false;
}
