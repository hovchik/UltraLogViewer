// Timeline strip: entry counts over time stacked by severity. Click jumps, drag sets a time filter.

import type { Histogram } from '../core/types';
import { formatDuration, formatTime } from '../core/timestamp';

export interface HistogramHost {
  jumpToTime(t: number): void;
  setTimeRange(from: number, to: number): void;
}

export class HistogramView {
  private canvas: HTMLCanvasElement;
  private tip: HTMLElement;
  private data: Histogram | null = null;
  private drag: { x0: number; x1: number } | null = null;
  private viewport: [number, number] | null = null;

  constructor(el: HTMLElement, private host: HistogramHost) {
    this.canvas = el.querySelector('canvas')!;
    this.tip = el.querySelector('.hist-tip')!;
    this.canvas.addEventListener('mousemove', (e) => this.onHover(e));
    this.canvas.addEventListener('mouseleave', () => (this.tip.style.display = 'none'));
    this.canvas.addEventListener('mousedown', (e) => this.onDown(e));
    new ResizeObserver(() => this.draw()).observe(el);
  }

  get bucketsWanted() {
    return Math.max(20, Math.floor(this.canvas.clientWidth / 4));
  }

  set(data: Histogram | null) {
    this.data = data;
    this.draw();
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
    const top = 14;
    const ph = hgt - top - 2;
    const colors = [css.getPropertyValue('--accent'), css.getPropertyValue('--warn'), css.getPropertyValue('--error')];
    d.counts.forEach((b, i) => {
      let y = hgt - 1;
      for (let k = 0; k < 3; k++) {
        if (!b[k]) continue;
        const bh = Math.max(1, (b[k] / max) * ph);
        ctx.globalAlpha = k === 0 ? 0.45 : 0.9;
        ctx.fillStyle = colors[k];
        ctx.fillRect(i * bw, y - bh, Math.max(1, bw - (bw > 3 ? 1 : 0)), bh);
        y -= bh;
      }
    });
    ctx.globalAlpha = 1;
    ctx.fillStyle = css.getPropertyValue('--fg-3');
    ctx.font = '10px system-ui';
    ctx.fillText(formatTime(d.start), 4, 10);
    const endLabel = formatTime(d.start + d.counts.length * d.bucketMs);
    ctx.fillText(endLabel, w - ctx.measureText(endLabel).width - 4, 10);
    const mid = `${formatDuration(d.bucketMs)} / bar`;
    ctx.fillText(mid, w / 2 - ctx.measureText(mid).width / 2, 10);
    const span = d.counts.length * d.bucketMs;
    if (this.viewport) {
      const x0 = ((this.viewport[0] - d.start) / span) * w;
      const x1 = ((this.viewport[1] - d.start) / span) * w;
      ctx.fillStyle = css.getPropertyValue('--fg-2');
      ctx.globalAlpha = 0.15;
      ctx.fillRect(x0, top, Math.max(2, x1 - x0), ph);
      ctx.globalAlpha = 1;
    }
    if (this.drag) {
      ctx.fillStyle = css.getPropertyValue('--accent');
      ctx.globalAlpha = 0.25;
      const a = Math.min(this.drag.x0, this.drag.x1);
      ctx.fillRect(a, 0, Math.abs(this.drag.x1 - this.drag.x0), hgt);
      ctx.globalAlpha = 1;
    }
  }

  private onHover(e: MouseEvent) {
    const d = this.data;
    if (!d || !d.counts.length) return;
    const x = e.offsetX;
    const i = Math.min(d.counts.length - 1, Math.floor((x / this.canvas.clientWidth) * d.counts.length));
    const b = d.counts[i];
    const t0 = d.start + i * d.bucketMs;
    this.tip.style.display = 'block';
    this.tip.textContent = `${formatTime(t0)}  ·  ${b[0] + b[1] + b[2]} entries  ·  ${b[2]} errors  ·  ${b[1]} warnings`;
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
