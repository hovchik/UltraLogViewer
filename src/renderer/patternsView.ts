// Patterns view: the view's messages grouped by template (numbers, ids, times… replaced), most common first.
// Shown in place of the grid; double-click (or Enter) filters to one pattern.

import { Level, PatternStat } from '../core/types';
import type { Tab } from './state';
import { contextMenu, esc, fmtNum, h } from './util';

export interface PatternsHost {
  onlyPattern(p: PatternStat): void;
  excludePattern(p: PatternStat): void;
  copy(text: string, what: string): void;
  /** Rescan; `all` reads every view row instead of a sample. */
  rescan(all: boolean): void;
  selectionChanged(): void;
  close(): void;
}

const LEVEL_COLORS = ['var(--fg-3)', 'var(--trace)', 'var(--debug)', 'var(--accent)', 'var(--warn)', 'var(--error)', 'var(--fatal-bg)'];
const LEVEL_ORDER = [Level.Fatal, Level.Error, Level.Warn, Level.Info, Level.Debug, Level.Trace, Level.None];
const PLACEHOLDER = /(<(?:num|guid|ip|time|str|url|hex)>)/;

/** Template text as HTML with the placeholders dimmed. */
export function templateHtml(tpl: string): string {
  return tpl
    .split(PLACEHOLDER)
    .map((part, i) => (i % 2 ? `<span class="ph">${esc(part)}</span>` : esc(part)))
    .join('');
}

function sparkline(spark: number[]): string {
  const max = Math.max(1, ...spark);
  const w = 100, hgt = 16;
  const pts = spark.map((v, i) => `${((i / (spark.length - 1)) * w).toFixed(1)},${(hgt - 1 - (v / max) * (hgt - 2)).toFixed(1)}`).join(' ');
  return `<svg width="${w}" height="${hgt}" viewBox="0 0 ${w} ${hgt}"><polyline points="${pts}" fill="none" stroke="var(--accent)" stroke-width="1.3" stroke-linejoin="round"/></svg>`;
}

function levelBar(levels: number[], count: number): string {
  let html = '';
  for (const l of LEVEL_ORDER) {
    const n = levels[l] ?? 0;
    if (!n) continue;
    html += `<i style="width:${((n / Math.max(1, count)) * 100).toFixed(2)}%;background:${LEVEL_COLORS[l]}"></i>`;
  }
  return html;
}

export class PatternsView {
  private head: HTMLElement;
  private list: HTMLElement;
  private tab: Tab | null = null;

  constructor(readonly el: HTMLElement, private host: PatternsHost) {
    this.head = h('div', { class: 'pt-head' });
    const cols = h(
      'div',
      { class: 'pt-cols' },
      h('span', { class: 'pt-n' }, 'Count'),
      h('span', { class: 'pt-pct' }, '%'),
      h('span', { class: 'pt-lv' }, 'Levels'),
      h('span', { class: 'pt-tr' }, 'Trend'),
      h('span', { class: 'pt-tpl' }, 'Pattern'),
    );
    this.list = h('div', { class: 'pt-list' });
    el.append(this.head, cols, this.list);
    el.addEventListener('keydown', (e) => this.onKey(e));
    this.list.addEventListener('mousedown', (e) => {
      const i = this.indexAt(e);
      if (i === null) return;
      e.preventDefault();
      el.focus();
      this.select(i);
    });
    this.list.addEventListener('dblclick', (e) => {
      const p = this.patternAt(e);
      if (p) this.host.onlyPattern(p);
    });
    this.list.addEventListener('contextmenu', (e) => {
      e.preventDefault();
      const i = this.indexAt(e);
      if (i === null) return;
      this.select(i);
      const p = this.tab!.patterns!.patterns[i];
      contextMenu(e.clientX, e.clientY, [
        { label: 'Only this pattern', shortcut: 'Enter', action: () => this.host.onlyPattern(p) },
        { label: 'Exclude this pattern', action: () => this.host.excludePattern(p) },
        'sep',
        { label: 'Copy pattern', action: () => this.host.copy(p.template, 'Pattern') },
        { label: 'Copy sample', action: () => this.host.copy(p.sample, 'Sample') },
      ]);
    });
  }

  private indexAt(e: MouseEvent): number | null {
    const row = (e.target as HTMLElement).closest('.pt-row') as HTMLElement | null;
    return row ? Number(row.dataset.i) : null;
  }

  private patternAt(e: MouseEvent): PatternStat | undefined {
    const i = this.indexAt(e);
    return i === null ? undefined : this.tab?.patterns?.patterns[i];
  }

  selected(): PatternStat | undefined {
    const t = this.tab;
    return t?.patterns?.patterns[t.patternSel ?? -1];
  }

  render(tab: Tab | null) {
    this.tab = tab;
    this.head.innerHTML = '';
    this.list.innerHTML = '';
    if (!tab) return;
    const r = tab.patterns;
    if (!r || (tab.patternsBusy && !r.patterns.length)) {
      this.head.append(h('span', { class: 'pt-sum' }, 'Finding patterns…'));
      return;
    }
    const parts: Node[] = [
      h('span', { class: 'pt-sum' }, tab.patternsBusy ? 'Finding patterns…' : `${fmtNum(r.total)} ${r.total === 1 ? 'pattern' : 'patterns'} in ${fmtNum(r.scanned)} of ${fmtNum(r.viewCount)} entries${r.sampled ? ' (sample)' : ''}`),
    ];
    if (r.sampled) parts.push(h('a', { class: 'pt-all', title: 'Read every entry of the view', onclick: () => this.host.rescan(true) }, 'Scan all'));
    parts.push(h('span', { class: 'sp' }), h('button', { class: 'mini pt-refresh', title: 'Find patterns again', onclick: () => this.host.rescan(false) }, '↻'));
    this.head.append(...parts);
    if (!r.patterns.length) {
      this.list.append(h('div', { class: 'empty-hint pt-empty' }, 'No entries in this view.'));
      return;
    }
    let html = '';
    r.patterns.forEach((p, i) => {
      const pct = r.scanned ? ((p.count / r.scanned) * 100).toFixed(1) : '0.0';
      const tip = `${p.template}\n\nSample: ${p.sample}`;
      html +=
        `<div class="pt-row${i === tab.patternSel ? ' sel' : ''}" data-i="${i}" title="${esc(tip)}">` +
        `<span class="pt-n">${fmtNum(p.count)}</span><span class="pt-pct">${pct}</span>` +
        `<span class="pt-lv"><span class="lvbar">${levelBar(p.levels, p.count)}</span></span>` +
        `<span class="pt-tr">${sparkline(p.spark)}</span>` +
        `<span class="pt-tpl">${templateHtml(p.template)}</span></div>`;
    });
    this.list.innerHTML = html;
    this.reveal();
  }

  private select(i: number) {
    const t = this.tab;
    const n = t?.patterns?.patterns.length ?? 0;
    if (!t || !n) return;
    t.patternSel = Math.max(0, Math.min(n - 1, i));
    for (const el of this.list.querySelectorAll('.pt-row.sel')) el.classList.remove('sel');
    this.list.querySelector(`.pt-row[data-i="${t.patternSel}"]`)?.classList.add('sel');
    this.reveal();
    this.host.selectionChanged();
  }

  private reveal() {
    (this.list.querySelector('.pt-row.sel') as HTMLElement | null)?.scrollIntoView({ block: 'nearest' });
  }

  private onKey(e: KeyboardEvent) {
    const t = this.tab;
    if (!t) return;
    const cur = t.patternSel ?? -1;
    switch (e.key) {
      case 'ArrowDown':
        this.select(cur + 1);
        break;
      case 'ArrowUp':
        this.select(Math.max(0, cur - 1));
        break;
      case 'PageDown':
        this.select(cur + 10);
        break;
      case 'PageUp':
        this.select(Math.max(0, cur - 10));
        break;
      case 'Home':
        this.select(0);
        break;
      case 'End':
        this.select(Number.MAX_SAFE_INTEGER);
        break;
      case 'Enter': {
        const p = this.selected();
        if (p) this.host.onlyPattern(p);
        break;
      }
      case 'Escape':
        this.host.close();
        break;
      default:
        return;
    }
    e.preventDefault();
    e.stopPropagation();
  }
}
