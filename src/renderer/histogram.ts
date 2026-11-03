// Timeline strip: entry counts over time stacked by severity, or by any field ("Group by": the top values in their own
// colours plus "other", with a legend that filters). Click jumps, drag sets a time filter, hover shows a tooltip, the
// splitter below resizes it. The time span on screen in the grid is a band.

import { isDottedName } from '../core/fieldPicks';
import type { GroupedHistogram } from '../core/histogramGroups';
import type { Histogram } from '../core/types';
import { formatDuration, formatTime, histogramTip } from '../core/timestamp';
import { h } from './util';

export interface HistogramHost {
  jumpToTime(t: number): void;
  setTimeRange(from: number, to: number): void;
  /** The splitter was dragged; `height` is already clamped to 40..240. */
  onResize(height: number): void;
  /** The "Group by" button was clicked (the host shows the choices next to it). */
  onGroupMenu(anchor: HTMLElement): void;
  /** A legend item was clicked: filter to that value. */
  onLegendClick(index: number): void;
}

export const HIST_MIN = 40;
export const HIST_MAX = 240;
/** Height of the bar above the chart with the "Group by" button, the legend and the time span. */
const BAR_H = 17;
/** Colours of the top values of a grouped timeline; "other" is grey. */
export const GROUP_COLORS = ['#4c6ef5', '#f59f00', '#12b886', '#e64980', '#7950f2', '#1098ad', '#fa5252', '#82c91e'];
export const OTHER_COLOR = '#868e96';
/** Legend labels: dotted names (loggers, classes) by their last part, unless two of them would then read the same. */
export function legendLabels(keys: string[]): string[] {
  const short = keys.map((k) => (isDottedName(k) ? k.slice(k.lastIndexOf('.') + 1) : k));
  return short.map((s, i) => (short.indexOf(s) !== short.lastIndexOf(s) ? keys[i] : s));
}
const colorOf = (g: GroupedHistogram, i: number) => (i === g.keys.length - 1 && g.keys[i] === '(other)' ? OTHER_COLOR : GROUP_COLORS[i % GROUP_COLORS.length]);

export class HistogramView {
  private canvas: HTMLCanvasElement;
  private tip: HTMLElement;
  private data: Histogram | null = null;
  private drag: { x0: number; x1: number } | null = null;
  private viewport: [number, number] | null = null;
  private hover = -1;
  private groups: GroupedHistogram | null = null;
  private groupLabel = 'Level';
  private legendClickable = true;
  private groupBtn: HTMLButtonElement;
  private legend: HTMLElement;
  private span: HTMLElement;

  constructor(private el: HTMLElement, private host: HistogramHost, splitter?: HTMLElement | null) {
    this.canvas = el.querySelector('canvas')!;
    this.tip = el.querySelector('.hist-tip')!;
    this.groupBtn = h('button', { class: 'hist-group', title: 'Stack the timeline by level, logger, thread, file or any parsed field' });
    this.groupBtn.addEventListener('click', () => this.host.onGroupMenu(this.groupBtn));
    this.legend = h('div', { class: 'hist-legend' });
    this.span = h('span', { class: 'hist-span', style: 'margin-left:auto' });
    el.append(h('div', { class: 'hist-bar' }, this.groupBtn, this.legend, this.span));
    this.renderBar();
    this.canvas.addEventListener('mousemove', (e) => this.onHover(e));
    this.canvas.addEventListener('mouseleave', () => {
      this.tip.style.display = 'none';
      if (this.hover >= 0) {
        this.hover = -1;
        this.draw();
      }
    });
    this.canvas.addEventListener('mousedown', (e) => this.onDown(e));
    splitter?.addEventListener('mousedown', (e) => this.startResize(e));
    new ResizeObserver(() => this.draw()).observe(el);
  }

  setHeight(px: number) {
    this.el.style.height = `${Math.max(HIST_MIN, Math.min(HIST_MAX, px))}px`;
  }

  private startResize(e: MouseEvent) {
    e.preventDefault();
    const startY = e.clientY;
    const startH = this.el.offsetHeight;
    const move = (ev: MouseEvent) => this.setHeight(startH + ev.clientY - startY);
    const up = () => {
      window.removeEventListener('mousemove', move);
      window.removeEventListener('mouseup', up);
      this.host.onResize(this.el.offsetHeight);
    };
    window.addEventListener('mousemove', move);
    window.addEventListener('mouseup', up);
  }

  get bucketsWanted() {
    return Math.max(20, Math.floor(this.canvas.clientWidth / 4));
  }

  set(data: Histogram | null) {
    this.data = data;
    this.hover = -1;
    if (this.groups && (!data || this.groups.start !== data.start || this.groups.bucketMs !== data.bucketMs || this.groups.counts.length !== data.counts.length)) this.groups = null;
    this.renderBar();
    this.draw();
  }

  /**
   * Stacks the bars by a field instead of by level (null: by level). `label` names the field on the button; the legend
   * filters on click when `clickable`. Groups that do not fit the current buckets are ignored until they are replaced.
   */
  setGroups(g: GroupedHistogram | null, label: string, clickable = true) {
    const d = this.data;
    this.groups = g && d && g.start === d.start && g.bucketMs === d.bucketMs && g.counts.length === d.counts.length ? g : null;
    this.groupLabel = label;
    this.legendClickable = clickable;
    this.renderBar();
    this.draw();
  }

  /** The grouping shown (null: by level), for tests. */
  get shownGroups(): GroupedHistogram | null {
    return this.groups;
  }

  private renderBar() {
    this.groupBtn.textContent = `Group by: ${this.groupLabel} ▾`;
    this.legend.innerHTML = '';
    const g = this.groups;
    if (g) {
      const labels = legendLabels(g.keys);
      g.keys.forEach((k, i) => {
        const item = h(
          'span',
          { class: this.legendClickable ? 'lg click' : 'lg', title: this.legendClickable ? `${k}: ${g.totals[i].toLocaleString('en-US')} entries. Click to show only these` : k },
          h('i', { class: 'sw', style: `background:${colorOf(g, i)}` }),
          h('span', { class: 'k' }, labels[i]),
          h('span', { class: 'n' }, g.totals[i].toLocaleString('en-US')),
        );
        if (this.legendClickable) item.addEventListener('click', () => this.host.onLegendClick(i));
        this.legend.append(item);
      });
    }
    const d = this.data;
    this.span.textContent = d && d.counts.length ? `${formatTime(d.start)} – ${formatTime(d.start + d.counts.length * d.bucketMs)} · ${formatDuration(d.bucketMs)} / bar` : '';
  }

  /** Time span currently on screen in the grid, drawn as a marker. */
  setViewport(v: [number, number] | null) {
    this.viewport = v;
    this.draw();
  }

  private xToTime(x: number): number | null {
    const d = this.data;
    if (!d || !d.counts.length) return null;
    const w = this.canvas.clientWidth;
    return d.start + (x / w) * d.counts.length * d.bucketMs;
  }

  private draw() {
    const c = this.canvas;
    const dpr = window.devicePixelRatio || 1;
    const w = c.clientWidth, hgt = c.clientHeight;
    if (!w || !hgt) return;
    c.width = w * dpr;
    c.height = hgt * dpr;
    const ctx = c.getContext('2d')!;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, w, hgt);
    const css = getComputedStyle(document.documentElement);
    const d = this.data;
    if (!d || !d.counts.length) {
      ctx.fillStyle = css.getPropertyValue('--fg-3');
      ctx.font = '11px system-ui';
      ctx.fillText('No timestamps to chart', 10, hgt / 2 + 4);
      return;
    }
    const max = Math.max(1, ...d.counts.map((b) => b[0] + b[1] + b[2]));
    const bw = w / d.counts.length;
    const top = BAR_H + 1;
    const ph = hgt - top - 1;
    const barW = Math.max(1, bw - (bw > 3 ? 1 : 0));
    const g = this.groups;
    if (g) {
      // stacked by field value, the most common value at the bottom
      g.counts.forEach((b, i) => {
        let y = hgt - 1;
        for (let k = 0; k < b.length; k++) {
          if (!b[k]) continue;
          const bh = Math.max(1, (b[k] / max) * ph);
          ctx.globalAlpha = 0.85;
          ctx.fillStyle = colorOf(g, k);
          ctx.fillRect(i * bw, y - bh, barW, bh);
          y -= bh;
        }
      });
    } else {
      const colors = [css.getPropertyValue('--accent'), css.getPropertyValue('--warn'), css.getPropertyValue('--error')];
      d.counts.forEach((b, i) => {
        let y = hgt - 1;
        for (let k = 0; k < 3; k++) {
          if (!b[k]) continue;
          const bh = Math.max(1, (b[k] / max) * ph);
          ctx.globalAlpha = k === 0 ? 0.45 : 0.9;
          ctx.fillStyle = colors[k];
          ctx.fillRect(i * bw, y - bh, barW, bh);
          y -= bh;
        }
      });
    }
    ctx.globalAlpha = 1;
    const span = d.counts.length * d.bucketMs;
    if (this.viewport) {
      // the time span on screen in the grid: a subtle band
      const x0 = ((this.viewport[0] - d.start) / span) * w;
      const x1 = ((this.viewport[1] - d.start) / span) * w;
      ctx.fillStyle = css.getPropertyValue('--accent');
      ctx.globalAlpha = 0.14;
      ctx.fillRect(x0, top, Math.max(2, x1 - x0), ph);
      // dim what is outside the viewport window when it covers less than 90% of the width
      if (x1 - x0 < w * 0.9) {
        ctx.fillStyle = css.getPropertyValue('--bg');
        ctx.globalAlpha = 0.35;
        if (x0 > 0) ctx.fillRect(0, top, x0, ph);
        const right = Math.max(x1, x0 + 2);
        if (right < w) ctx.fillRect(right, top, w - right, ph);
      }
      ctx.globalAlpha = 1;
    }
    if (this.hover >= 0 && this.hover < d.counts.length) {
      ctx.fillStyle = css.getPropertyValue('--fg');
      ctx.globalAlpha = 0.12;
      ctx.fillRect(this.hover * bw, top, Math.max(1, bw), hgt - top);
      ctx.globalAlpha = 1;
    }
    if (this.drag) {
      ctx.fillStyle = css.getPropertyValue('--accent');
      ctx.globalAlpha = 0.25;
      const a = Math.min(this.drag.x0, this.drag.x1);
      ctx.fillRect(a, top, Math.abs(this.drag.x1 - this.drag.x0), hgt - top);
      ctx.globalAlpha = 1;
    }
  }

  private onHover(e: MouseEvent) {
    const d = this.data;
    if (!d || !d.counts.length) return;
    const x = e.offsetX;
    const i = Math.max(0, Math.min(d.counts.length - 1, Math.floor((x / this.canvas.clientWidth) * d.counts.length)));
    const b = d.counts[i];
    const t0 = d.start + i * d.bucketMs;
    this.tip.style.display = 'block';
    const g = this.groups;
    let tip = histogramTip(t0, t0 + d.bucketMs, b);
    if (g) {
      // span and count, then the bucket's largest groups
      const top = g.counts[i].map((n, k) => [n, k]).filter(([n]) => n > 0).sort((x, y) => y[0] - x[0]).slice(0, 5);
      tip = [...tip.split('\n').slice(0, 2), ...top.map(([n, k]) => `${g.keys[k]}: ${n.toLocaleString('en-US')}`)].join('\n');
    }
    this.tip.textContent = tip;
    this.tip.style.top = `${BAR_H + 2}px`;
    if (i !== this.hover) {
      this.hover = i;
      this.draw();
    }
    const tw = this.tip.offsetWidth;
    this.tip.style.left = `${Math.min(this.canvas.clientWidth - tw - 4, Math.max(4, x + 12))}px`;
  }

  private onDown(e: MouseEvent) {
    const x0 = e.offsetX;
    const rect = this.canvas.getBoundingClientRect();
    this.drag = { x0, x1: x0 };
    const move = (ev: MouseEvent) => {
      this.drag = { x0, x1: Math.max(0, Math.min(rect.width, ev.clientX - rect.left)) };
      this.draw();
    };
    const up = () => {
      window.removeEventListener('mousemove', move);
      window.removeEventListener('mouseup', up);
      const dr = this.drag!;
      this.drag = null;
      this.draw();
      if (Math.abs(dr.x1 - dr.x0) < 4) {
        const t = this.xToTime(dr.x0);
        if (t !== null) this.host.jumpToTime(t);
      } else {
        const a = this.xToTime(Math.min(dr.x0, dr.x1));
        const b = this.xToTime(Math.max(dr.x0, dr.x1));
        if (a !== null && b !== null) this.host.setTimeRange(Math.floor(a), Math.ceil(b));
      }
    };
    window.addEventListener('mousemove', move);
    window.addEventListener('mouseup', up);
  }
}
