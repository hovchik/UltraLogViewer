// Virtualised log table. Only the rows on screen exist in the DOM; rows are fetched from the
// engine on demand. Scroll positions are scaled so tens of millions of rows still fit within
// the browser's maximum element height.

import { compilePattern, matchRanges } from '../core/query';
import type { ColumnDef, RowData } from '../core/types';
import { engine } from './api';
import type { HighlightRule, Tab } from './state';
import { esc } from './util';

const MAX_PX = 8_000_000;
const FETCH_MARGIN = 80;

export interface GridHost {
  highlights(): HighlightRule[];
  columnWidth(col: ColumnDef): number;
  hiddenColumns(): string[];
  onHeaderMenu(e: MouseEvent): void;
  setColumnWidth(key: string, width: number): void;
  onSelectionChange(): void;
  onTopRowChange(): void;
  /** Return true if the key was handled. */
  onKey(e: KeyboardEvent): boolean;
  toggleBookmark(row: RowData): void;
  copyRows(from: number, to: number): void;
}

interface Span {
  s: number;
  e: number;
  open: string;
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
  private gen = 0;

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
      this.host.onHeaderMenu(e);
    });
    root.addEventListener('keydown', (e) => this.onKey(e));
    new ResizeObserver(() => {
      this.syncScroll();
      this.render();
    }).observe(this.body);
    this.rowH = parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--row-h')) || 20;
  }

  // ---------------------------------------------------------------- state

  setTab(tab: Tab | null) {
    this.tab = tab;
    this.cache.clear();
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

  visibleRows() {
    return Math.max(1, Math.floor(this.body.clientHeight / this.rowH));
  }

  private maxTop() {
    return Math.max(0, this.count - this.visibleRows());
  }

  // --------------------------------------------------------------- header

  /** Visible columns with resolved widths; exactly one column (0 width) stretches. */
  private cols(): { col: ColumnDef; width: number }[] {
    const hidden = this.host.hiddenColumns();
    const all = this.tab?.info.columns ?? [];
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
    else if (row >= t.topRow + vis) this.scrollTo(row - vis + 1);
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
    const vis = this.visibleRows() + 1;
    const from = t.topRow;
    const to = Math.min(this.count, from + vis);
    void this.ensureRows(from, to);
    const visible = this.cols();
    const cols = visible.map((c) => c.col);
    const widths = visible.map((c) => c.width);
    const selLo = Math.min(t.anchor, t.focus);
    const selHi = Math.max(t.anchor, t.focus);
    const hitRows = t.search.result?.rows;
    const curHit = hitRows && hitRows.length ? hitRows[t.search.current] : -1;
    let html = '';
    for (let r = from; r < to; r++) {
      const row = this.cache.get(r);
      if (!row) {
        html += `<div class="r" data-row="${r}"><div class="g"></div></div>`;
        continue;
      }
      const cls = ['r', `l${row.level}`];
      if (row.context) cls.push('ctx');
      if (r >= selLo && r <= selHi) cls.push('sel');
      if (r === t.focus) cls.push('cur');
      const isHit = hitRows ? binaryHas(hitRows, r) : false;
      if (isHit) cls.push('hitrow');
      let rowStyle = '';
      for (const { rule, re } of this.hlCompiled) {
        if (!rule.row) continue;
        re.lastIndex = 0;
        if (re.test(row.text)) {
          rowStyle = ` style="background:${rule.color}38"`;
          break;
        }
      }
      const bm = t.bookmarks.has(row.key) ? '<i class="bmk"></i>' : '';
      const more = row.lineCount > 1 ? `<span class="more">+${row.lineCount - 1}</span>` : '';
      html += `<div class="${cls.join(' ')}" data-row="${r}"${rowStyle}><div class="g" title="Line ${row.line}${row.lineCount > 1 ? `–${row.line + row.lineCount - 1}` : ''}">${bm}${more}${row.line}</div>`;
      for (let i = 0; i < cols.length; i++) {
        const c = cols[i];
        let v: string;
        if (c.key === '__source') v = t.info.sources[row.source]?.displayName ?? '';
        else v = row.fields[c.key] ?? '';
        if (c.key === 'message' || widths[i] === 0) {
          const nl = v.indexOf('\n');
          if (nl >= 0) v = v.slice(0, nl);
        }
        if (v.length > 2000) v = v.slice(0, 2000) + '…';
        const w = widths[i];
        const style = w ? ` style="width:${w}px"` : '';
        const cc = w ? 'c' : 'c flex';
        const content = c.key === 'level' ? `<span class="lvtag">${esc(v)}</span>` : this.markup(v, r === curHit);
        html += `<div class="${cc}${c.key === '__source' ? ' src' : ''}"${style}>${content}</div>`;
      }
      html += '</div>';
    }
    this.rowsEl.innerHTML = html;
    this.drawOverview();
  }

  private markup(text: string, current: boolean): string {
    if (!text || (!this.searchRe && !this.hlCompiled.length)) return esc(text);
    const spans: Span[] = [];
    if (this.searchRe) for (const [s, e] of matchRanges(this.searchRe, text, 200)) spans.push({ s, e, open: current ? '<mark class="cur">' : '<mark>' });
    for (const { rule, re } of this.hlCompiled) {
      if (rule.row) continue;
      for (const [s, e] of matchRanges(re, text, 100)) spans.push({ s, e, open: `<mark style="background:transparent;color:${rule.color};font-weight:600">` });
    }
    if (!spans.length) return esc(text);
    spans.sort((a, b) => a.s - b.s);
    let out = '';
    let pos = 0;
    for (const sp of spans) {
      if (sp.s < pos) continue; // overlapping: earlier (search) span wins
      out += esc(text.slice(pos, sp.s)) + sp.open + esc(text.slice(sp.s, sp.e)) + '</mark>';
      pos = sp.e;
    }
    return out + esc(text.slice(pos));
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
