// Analytics view: a dashboard over the whole log, filled in by the engine's background analysis (core/analytics.ts)
// while it runs. Every bar, segment and row is interactive: hover shows the numbers, click filters the log to it
// (Shift+click hides it instead), and dragging over the timeline filters to that time range.

import { AnalyticsNumeric, AnalyticsPattern, AnalyticsResult, Level, LEVEL_NAMES, ValueCount } from '../core/types';
import type { Tab } from './state';
import { templateHtml } from './patternsView';
import { esc, fmtBytes, fmtNum, h } from './util';

export interface AnalyticsHost {
  /** Show only this level (`only`), or hide it. */
  filterLevel(level: Level, only: boolean): void;
  filterTime(from: number, to: number): void;
  filterPattern(template: string, mode: 'include' | 'exclude'): void;
  filterField(key: string, value: string, mode: 'include' | 'exclude'): void;
  /** A plain text rule (exception names, IPs, URLs). */
  filterText(text: string, mode: 'include' | 'exclude'): void;
  jumpToTime(t: number): void;
  start(): void;
  pause(): void;
  restart(): void;
  close(): void;
}

/** Timeline series, most severe first: the levels they stack and their colour. */
const SERIES: { name: string; levels: Level[]; color: string; filter: Level }[] = [
  { name: 'Errors', levels: [Level.Fatal, Level.Error], color: 'var(--error)', filter: Level.Error },
  { name: 'Warnings', levels: [Level.Warn], color: 'var(--warn)', filter: Level.Warn },
  { name: 'Info', levels: [Level.Info], color: 'var(--accent)', filter: Level.Info },
  { name: 'Debug & trace', levels: [Level.Debug, Level.Trace, Level.None], color: 'var(--fg-3)', filter: Level.Debug },
];
const LEVEL_COLORS = ['var(--fg-3)', 'var(--trace)', 'var(--debug)', 'var(--accent)', 'var(--warn)', 'var(--error)', 'var(--fatal-bg)'];
const LEVEL_ORDER = [Level.Fatal, Level.Error, Level.Warn, Level.Info, Level.Debug, Level.Trace, Level.None];
const LEVEL_LABEL = ['No level', 'Trace', 'Debug', 'Info', 'Warning', 'Error', 'Fatal'];
const WEEKDAYS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
const SOURCE_COLORS = ['#2a78d6', '#eb6834', '#1baf7a', '#eda100', '#e87ba4', '#008300', '#6250d6', '#e34948'];
const HINT = 'Click: show only these · Shift+click: hide them';

const p2 = (n: number) => String(n).padStart(2, '0');
export function fmtTime(t: number | null, withDate = true): string {
  if (t == null) return '–';
  const d = new Date(t);
  const time = `${p2(d.getHours())}:${p2(d.getMinutes())}:${p2(d.getSeconds())}`;
  return withDate ? `${d.getFullYear()}-${p2(d.getMonth() + 1)}-${p2(d.getDate())} ${time}` : time;
}
export function fmtSpan(ms: number): string {
  if (!(ms > 0)) return '0 s';
  if (ms < 1000) return `${Math.round(ms)} ms`;
  const s = ms / 1000;
  if (s < 60) return `${s.toFixed(s < 10 ? 1 : 0)} s`;
  const m = s / 60;
  if (m < 60) return `${Math.floor(m)} min ${Math.round(s % 60)} s`;
  const hr = m / 60;
  if (hr < 48) return `${Math.floor(hr)} h ${Math.round(m % 60)} min`;
  return `${Math.floor(hr / 24)} d ${Math.round(hr % 24)} h`;
}
const fmtMs = (ms: number) => (ms < 1 ? `${ms.toFixed(2)} ms` : ms < 1000 ? `${ms.toFixed(ms < 10 ? 1 : 0)} ms` : fmtSpan(ms));
const fmtCompact = (n: number) => (n >= 1e9 ? `${(n / 1e9).toFixed(1)}B` : n >= 1e6 ? `${(n / 1e6).toFixed(1)}M` : n >= 1e4 ? `${(n / 1e3).toFixed(1)}k` : fmtNum(Math.round(n)));
const fmtVal = (x: number) => (Math.abs(x) >= 1e4 ? fmtCompact(x) : Number.isInteger(x) ? fmtNum(x) : x.toFixed(Math.abs(x) < 10 ? 2 : 1));
const pct = (n: number, of: number) => (of > 0 ? (n / of) * 100 : 0);
const fmtPct = (n: number, of: number) => {
  const p = pct(n, of);
  return p === 0 ? '0%' : p < 0.1 ? '<0.1%' : `${p.toFixed(p < 10 ? 1 : 0)}%`;
};
const errorsOf = (levels: number[]) => (levels[Level.Error] ?? 0) + (levels[Level.Fatal] ?? 0);

/** Why the analysis is not moving, or how far it is. */
function stateText(r: AnalyticsResult): string {
  switch (r.state) {
    case 'waiting':
      return 'Waiting: loading and filtering go first';
    case 'running':
      return `Analysing ${Math.floor(r.progress * 100)}% · ${fmtNum(r.scanned)} of ${fmtNum(r.total)} entries read`;
    case 'paused':
      return `Paused at ${Math.floor(r.progress * 100)}%`;
    case 'done':
      return `Analysed ${fmtNum(r.total)} entries in ${fmtSpan(r.elapsedMs)}`;
    case 'cancelled':
      return 'Stopped';
    case 'error':
      return `Analysis failed: ${r.error ?? 'unknown error'}`;
  }
}

/** Short status-bar label for a running analysis, or null when there is nothing to show. */
export function analyticsStatus(r: AnalyticsResult | null | undefined): { label: string; fraction: number } | null {
  if (!r || r.state === 'done' || r.state === 'cancelled' || r.state === 'error') return null;
  const label = r.state === 'paused' ? 'Analysis paused' : r.state === 'waiting' ? 'Analysis waiting' : 'Analysing';
  return { label, fraction: r.progress };
}

export class AnalyticsView {
  private head: HTMLElement;
  private body: HTMLElement;
  private tip: HTMLElement;
  private acts: ((shift: boolean) => void)[] = [];
  private tab: Tab | null = null;
  private renderAt = 0;
  private renderTimer: ReturnType<typeof setTimeout> | undefined;
  private drag: { svg: SVGSVGElement; x0: number; x1: number; start: number; end: number } | null = null;

  constructor(readonly el: HTMLElement, private host: AnalyticsHost) {
    this.head = h('div', { class: 'an-head' });
    this.body = h('div', { class: 'an-body' });
    this.tip = h('div', { class: 'an-tip hidden', role: 'tooltip' });
    el.append(this.head, this.body, this.tip);
    el.addEventListener('click', (e) => this.onClick(e));
    el.addEventListener('mousemove', (e) => this.onMove(e));
    el.addEventListener('mouseleave', () => this.tip.classList.add('hidden'));
    this.body.addEventListener('scroll', () => this.tip.classList.add('hidden'), { passive: true });
    el.addEventListener('mousedown', (e) => this.onDown(e));
    window.addEventListener('mousemove', (e) => this.onDrag(e));
    window.addEventListener('mouseup', (e) => this.onUp(e));
    el.addEventListener('keydown', (e) => {
      const t = e.target as HTMLElement;
      if ((e.key === 'Enter' || e.key === ' ') && t.dataset.a) {
        e.preventDefault();
        this.acts[Number(t.dataset.a)]?.(e.shiftKey);
      }
    });
    el.addEventListener('focusin', (e) => {
      const t = e.target as HTMLElement;
      if (t.dataset.tip) this.showTip(t, t.getBoundingClientRect().left + 10, t.getBoundingClientRect().bottom + 4);
    });
  }

  /** Redraws at most every 400 ms while snapshots stream in; `now` redraws at once. */
  render(tab: Tab | null, now = false) {
    this.tab = tab;
    clearTimeout(this.renderTimer);
    const wait = now ? 0 : Math.max(0, 400 - (Date.now() - this.renderAt));
    if (wait === 0) return this.renderNow();
    this.renderTimer = setTimeout(() => this.renderNow(), wait);
  }

  private act(fn: (shift: boolean) => void): number {
    this.acts.push(fn);
    return this.acts.length - 1;
  }

  /** Attributes of an interactive mark: its tooltip and what a click does. */
  private mark(tip: string, fn?: (shift: boolean) => void): string {
    const a = fn ? ` data-a="${this.act(fn)}" tabindex="0"` : '';
    return ` data-tip="${esc(tip)}"${a}`;
  }

  private renderNow() {
    if (this.drag) return; // keep the drawn selection
    this.renderAt = Date.now();
    this.acts = [];
    const r = this.tab?.analytics ?? null;
    this.renderHead(r);
    if (!r) {
      this.body.innerHTML = '<div class="an-empty muted">The analysis starts once the file is loaded.</div>';
      return;
    }
    const keep = this.body.scrollTop;
    this.body.classList.toggle('stale', r.state === 'waiting' && r.scanned === 0);
    this.body.innerHTML = [
      this.insights(r),
      this.tiles(r),
      this.timelineCard(r),
      `<div class="an-grid">${[this.levelsCard(r), this.sourcesCard(r), this.hoursCard(r), this.weekdaysCard(r)].join('')}</div>`,
      this.patternsCard('Top message patterns', 'Most common templates (numbers, ids and times replaced)', r.patterns.slice(0, 25), r, 'count'),
      `<div class="an-grid">${[
        this.patternsCard('Error patterns', 'Templates with the most errors', r.errorPatterns.slice(0, 12), r, 'errors'),
        this.exceptionsCard(r),
        this.patternsCard('New late in the log', 'First seen in the last quarter of the time span', r.newPatterns, r, 'first'),
        this.patternsCard('Gone quiet', 'Common earlier, nothing in the second half', r.stoppedPatterns, r, 'last'),
        this.burstsCard(r),
        this.gapsCard(r),
        this.durationsCard(r),
        this.patternsCard('Slowest operations', 'Templates with the longest average duration in the message', r.slowPatterns, r, 'dur'),
      ].join('')}</div>`,
      this.fieldsCard(r),
      `<div class="an-grid">${[this.valuesCard('Top IP addresses', r.ips, r.scanned), this.valuesCard('Top URLs', r.urls, r.scanned), this.valuesCard('HTTP methods', r.methods, r.scanned)].join('')}</div>`,
    ].join('');
    this.body.scrollTop = keep;
  }

  private renderHead(r: AnalyticsResult | null) {
    const state = r ? stateText(r) : 'Not started';
    const running = r && (r.state === 'running' || r.state === 'waiting');
    const frac = r ? (r.state === 'done' ? 1 : r.progress) : 0;
    this.head.innerHTML = '';
    this.head.append(
      h('b', {}, 'Analytics'),
      h('span', { class: 'muted' }, 'whole log, in the background'),
      h('span', { class: 'sp' }),
      h('span', { class: `an-state ${r?.state ?? ''}` }, state),
      h(
        'span',
        { class: `an-prog${running ? ' on' : ''}`, role: 'progressbar', 'aria-valuemin': '0', 'aria-valuemax': '100', 'aria-valuenow': String(Math.round(frac * 100)) },
        h('i', { style: `width:${(frac * 100).toFixed(1)}%` }),
      ),
      running
        ? h('button', { class: 'mini', onclick: () => this.host.pause(), title: 'Stop reading; Resume continues where it stopped' }, 'Pause')
        : r?.state === 'paused' || !r
          ? h('button', { class: 'mini', onclick: () => this.host.start() }, r ? 'Resume' : 'Start')
          : '',
      h('button', { class: 'mini', onclick: () => this.host.restart(), title: 'Analyse the whole log again' }, 'Re-run'),
      h('button', { class: 'mini', onclick: () => this.host.close(), title: 'Back to the log (Esc)' }, '×'),
    );
  }

  // ----------------------------------------------------------------- cards

  private card(title: string, sub: string, body: string, cls = ''): string {
    return `<section class="an-card ${cls}"><h3>${esc(title)}</h3>${sub ? `<div class="an-sub muted">${esc(sub)}</div>` : ''}${body}</section>`;
  }

  private insights(r: AnalyticsResult): string {
    const items: string[] = [];
    const s = r.summary;
    const add = (html: string, fn?: (shift: boolean) => void, tip = '') => items.push(`<li${fn ? this.mark(tip || 'Click to filter the log to this', fn) : ''}>${html}</li>`);
    const errs = errorsOf(r.levels);
    if (s.entries) {
      const rate = fmtPct(errs, s.entries);
      add(errs ? `<b>${fmtNum(errs)}</b> errors (${rate} of all entries), <b>${fmtNum(r.levels[Level.Warn])}</b> warnings.` : 'No errors in the whole log.', errs ? () => this.host.filterLevel(Level.Error, true) : undefined);
    }
    const burst = r.bursts[0];
    if (burst) add(`Error burst at <b>${esc(fmtTime(burst.from))}</b>: ${fmtNum(burst.errors)} errors in ${esc(fmtSpan(burst.to - burst.from))}, ${burst.factor.toFixed(0)}× the usual rate.`, () => this.host.filterTime(burst.from, burst.to));
    const ep = r.errorPatterns[0];
    if (ep) add(`Most frequent error: <span class="tpl">${templateHtml(ep.template)}</span> (${fmtNum(errorsOf(ep.levels))}×).`, (sh) => this.host.filterPattern(ep.template, sh ? 'exclude' : 'include'));
    const ex = r.exceptions[0];
    if (ex) add(`Top exception <b>${esc(ex.name)}</b>: ${fmtNum(ex.count)}×, last at ${esc(fmtTime(ex.last))}.`, (sh) => this.host.filterText(ex.name, sh ? 'exclude' : 'include'));
    const np = r.newPatterns[0];
    if (np) add(`New message since ${esc(fmtTime(np.first))}: <span class="tpl">${templateHtml(np.template)}</span> (${fmtNum(np.count)}×).`, (sh) => this.host.filterPattern(np.template, sh ? 'exclude' : 'include'));
    const gap = r.gaps[0];
    if (gap) add(`Longest silence: <b>${esc(fmtSpan(gap.ms))}</b> without entries after ${esc(fmtTime(gap.from))}.`, () => this.host.jumpToTime(gap.from), 'Click to jump there');
    if (s.peakAt != null && s.ratePerMin > 0 && s.peakPerMin > s.ratePerMin * 3) add(`Busiest moment ${esc(fmtTime(s.peakAt))}: ${fmtCompact(s.peakPerMin)}/min, ${(s.peakPerMin / s.ratePerMin).toFixed(0)}× the average.`, () => r.timeline && this.host.filterTime(s.peakAt!, s.peakAt! + r.timeline.bucketMs));
    const slow = r.slowPatterns[0];
    if (slow) add(`Slowest operation (avg ${esc(fmtMs(slow.durAvg))}, max ${esc(fmtMs(slow.durMax))}): <span class="tpl">${templateHtml(slow.template)}</span>.`, (sh) => this.host.filterPattern(slow.template, sh ? 'exclude' : 'include'));
    if (!items.length) return '';
    return this.card('Insights', r.state === 'done' ? '' : 'So far: these grow while the analysis runs', `<ul class="an-insights">${items.join('')}</ul>`, 'wide');
  }

  private tiles(r: AnalyticsResult): string {
    const s = r.summary;
    const span = s.timeMin != null && s.timeMax != null ? s.timeMax - s.timeMin : 0;
    const errs = errorsOf(r.levels);
    const tile = (label: string, value: string, sub = '', tip = '', fn?: (shift: boolean) => void) =>
      `<div class="an-tile"${tip || fn ? this.mark(tip || label, fn) : ''}><span class="lbl">${esc(label)}</span><b>${esc(value)}</b><span class="muted">${esc(sub)}</span></div>`;
    return `<div class="an-tiles">${[
      tile('Entries', fmtCompact(s.entries), `${fmtBytes(s.bytes)} · ${s.sources} ${s.sources === 1 ? 'file' : 'files'}`, `${fmtNum(s.entries)} entries`),
      tile('Time span', span ? fmtSpan(span) : '–', s.timeMin != null ? `${fmtTime(s.timeMin)} → ${fmtTime(s.timeMax, false)}` : 'no timestamps'),
      tile('Rate', s.ratePerMin ? `${fmtCompact(s.ratePerMin)}/min` : '–', s.peakPerMin ? `peak ${fmtCompact(s.peakPerMin)}/min` : ''),
      tile('Errors', fmtCompact(errs), fmtPct(errs, s.entries), `${fmtNum(errs)} errors and fatals\n${HINT}`, (sh) => this.host.filterLevel(Level.Error, !sh)),
      tile('Warnings', fmtCompact(r.levels[Level.Warn]), fmtPct(r.levels[Level.Warn], s.entries), `${fmtNum(r.levels[Level.Warn])} warnings\n${HINT}`, (sh) => this.host.filterLevel(Level.Warn, !sh)),
      tile('Patterns', fmtCompact(s.patterns), 'distinct templates'),
      tile('Exceptions', fmtCompact(s.exceptions), 'distinct types'),
      tile('Multi-line', fmtCompact(s.multiline), r.scanned ? `${fmtPct(s.multiline, r.scanned)} · avg ${Math.round(s.avgLength)} chars` : ''),
    ].join('')}</div>`;
  }

  private timelineCard(r: AnalyticsResult): string {
    const tl = r.timeline;
    if (!tl || !tl.counts.length) return '';
    const n = tl.counts.length;
    const W = 1000, H = 150, gap = n > 200 ? 0 : 1;
    const bw = W / n;
    const sums = tl.counts.map((c) => SERIES.map((s) => s.levels.reduce((a, l) => a + (c[l] ?? 0), 0)));
    const max = Math.max(1, ...sums.map((x) => x.reduce((a, b) => a + b, 0)));
    let marks = '';
    for (const b of r.bursts) {
      const x0 = ((b.from - tl.start) / tl.bucketMs) * bw, x1 = ((b.to - tl.start) / tl.bucketMs) * bw;
      marks += `<rect class="burst" x="${x0.toFixed(1)}" y="0" width="${Math.max(2, x1 - x0).toFixed(1)}" height="${H}"${this.mark(`Error burst: ${fmtNum(b.errors)} errors, ${b.factor.toFixed(0)}× usual\n${fmtTime(b.from)} – ${fmtTime(b.to, false)}\nClick: show this time range`, () => this.host.filterTime(b.from, b.to))}/>`;
    }
    sums.forEach((ser, i) => {
      const total = ser.reduce((a, b) => a + b, 0);
      const from = tl.start + i * tl.bucketMs;
      const x = i * bw;
      let y = H;
      // bottom up: least severe first, so errors sit on top where the eye lands
      for (let k = SERIES.length - 1; k >= 0; k--) {
        const v = ser[k];
        if (!v) continue;
        const hgt = Math.max(1, (v / max) * (H - 4));
        y -= hgt;
        marks += `<rect x="${(x + gap / 2).toFixed(2)}" y="${y.toFixed(1)}" width="${Math.max(0.6, bw - gap).toFixed(2)}" height="${hgt.toFixed(1)}" fill="${SERIES[k].color}" pointer-events="none"/>`;
      }
      const lines = [`${fmtTime(from)} – ${fmtTime(from + tl.bucketMs, false)}`, `${fmtNum(total)} entries`, ...SERIES.map((s, k) => (ser[k] ? `${s.name}: ${fmtNum(ser[k])}` : '')).filter(Boolean), 'Click: show this bucket · drag: a range'];
      marks += `<rect class="hit" x="${x.toFixed(2)}" y="0" width="${bw.toFixed(2)}" height="${H}"${this.mark(lines.join('\n'), () => this.host.filterTime(from, from + tl.bucketMs))}/>`;
    });
    const end = tl.start + n * tl.bucketMs;
    const ticks = [0, 0.25, 0.5, 0.75, 1].map((f) => `<span style="left:${f * 100}%">${esc(fmtTime(tl.start + f * (end - tl.start), f === 0 || end - tl.start > 864e5))}</span>`).join('');
    const legend = SERIES.map((s) => `<span${this.mark(`${s.name}\n${HINT}`, (sh) => this.host.filterLevel(s.filter, !sh))}><i style="background:${s.color}"></i>${esc(s.name)}</span>`).join('');
    const body = `<div class="an-legend">${legend}<span class="muted">${esc(fmtSpan(tl.bucketMs))} per bar · peak ${fmtNum(max)}</span></div>
      <svg class="an-timeline" viewBox="0 0 ${W} ${H}" preserveAspectRatio="none" data-start="${tl.start}" data-end="${end}">${marks}<rect class="sel hidden" x="0" y="0" width="0" height="${H}"/></svg>
      <div class="an-ticks">${ticks}</div>`;
    return this.card('Levels over time', 'Shaded: error bursts. Click a bar for its time range, drag across bars for a longer one.', body, 'wide');
  }

  /** Horizontal bars: label, bar, count and share. */
  private bars(rows: { label: string; html?: string; count: number; color?: string; segs?: { n: number; color: string }[]; tip: string; fn?: (shift: boolean) => void }[], of: number): string {
    const max = Math.max(1, ...rows.map((r) => r.count));
    return `<div class="an-bars">${rows
      .map((r) => {
        const fill = r.segs
          ? r.segs.filter((s) => s.n > 0).map((s) => `<i style="width:${pct(s.n, max).toFixed(2)}%;background:${s.color}"></i>`).join('')
          : `<i style="width:${pct(r.count, max).toFixed(2)}%;background:${r.color ?? 'var(--accent)'}"></i>`;
        return `<div class="an-bar${r.fn ? ' act' : ''}"${this.mark(r.tip, r.fn)}><span class="lbl">${r.html ?? esc(r.label)}</span><span class="track">${fill}</span><span class="n">${fmtCompact(r.count)}</span><span class="p muted">${fmtPct(r.count, of)}</span></div>`;
      })
      .join('')}</div>`;
  }

  private levelsCard(r: AnalyticsResult): string {
    const total = r.levels.reduce((a, b) => a + b, 0);
    const rows = LEVEL_ORDER.filter((l) => r.levels[l]).map((l) => ({
      label: LEVEL_LABEL[l],
      count: r.levels[l],
      color: LEVEL_COLORS[l],
      tip: `${LEVEL_NAMES[l]}: ${fmtNum(r.levels[l])} (${fmtPct(r.levels[l], total)})\n${HINT}`,
      fn: (sh: boolean) => this.host.filterLevel(l, !sh),
    }));
    return this.card('Levels', '', rows.length ? this.bars(rows, total) : '<div class="muted">No entries</div>');
  }

  private sourcesCard(r: AnalyticsResult): string {
    if (r.sources.length < 2) return '';
    const total = r.sources.reduce((a, s) => a + s.entries, 0);
    const rows = r.sources.slice(0, 12).map((s, i) => ({
      label: s.name,
      count: s.entries,
      segs: [
        { n: s.entries - errorsOf(s.levels) - (s.levels[Level.Warn] ?? 0), color: SOURCE_COLORS[i % SOURCE_COLORS.length] },
        { n: s.levels[Level.Warn] ?? 0, color: 'var(--warn)' },
        { n: errorsOf(s.levels), color: 'var(--error)' },
      ],
      tip: `${s.name}\n${fmtNum(s.entries)} entries · ${fmtNum(errorsOf(s.levels))} errors · ${fmtNum(s.levels[Level.Warn] ?? 0)} warnings\n${fmtTime(s.timeMin)} → ${fmtTime(s.timeMax)}`,
    }));
    return this.card('Files', 'Entries per file; warnings and errors at the end of each bar', this.bars(rows, total));
  }

  private columns(values: number[], labels: string[], tips: string[], fns: ((shift: boolean) => void)[] | null, errors?: number[]): string {
    const max = Math.max(1, ...values);
    return `<div class="an-cols">${values
      .map((v, i) => {
        const e = errors?.[i] ?? 0;
        return `<div class="col${fns ? ' act' : ''}"${this.mark(tips[i], fns?.[i])}><span class="stack" style="height:${pct(v, max).toFixed(1)}%"><i style="height:${pct(e, Math.max(1, v)).toFixed(1)}%"></i></span><span class="cl">${esc(labels[i])}</span></div>`;
      })
      .join('')}</div>`;
  }

  private hoursCard(r: AnalyticsResult): string {
    if (!r.hours.length || !r.summary.timed) return '';
    const v = r.hours.map((x) => x[0]);
    return this.card(
      'Hour of day',
      'Local time; the red part is errors',
      this.columns(v, v.map((_, i) => (i % 3 === 0 ? String(i) : '')), r.hours.map((x, i) => `${p2(i)}:00–${p2(i)}:59\n${fmtNum(x[0])} entries · ${fmtNum(x[2])} errors · ${fmtNum(x[1])} warnings`), null, r.hours.map((x) => x[2])),
    );
  }

  private weekdaysCard(r: AnalyticsResult): string {
    if (!r.weekdays.length || !r.summary.timed) return '';
    const active = r.weekdays.filter((x) => x > 0).length;
    if (active < 2) return '';
    return this.card('Day of week', '', this.columns(r.weekdays, WEEKDAYS, r.weekdays.map((n, i) => `${WEEKDAYS[i]}: ${fmtNum(n)} entries`), null));
  }

  private spark(p: AnalyticsPattern, r: AnalyticsResult): string {
    const max = Math.max(1, ...p.spark);
    const W = 120, H = 18, bw = W / p.spark.length;
    const bars = p.spark.map((v, i) => (v ? `<rect x="${(i * bw).toFixed(1)}" y="${(H - Math.max(1, (v / max) * H)).toFixed(1)}" width="${Math.max(1, bw - 0.6).toFixed(1)}" height="${Math.max(1, (v / max) * H).toFixed(1)}"/>` : '')).join('');
    const sp = r.spark;
    const what = sp.byTime ? `${fmtTime(sp.start)} → ${fmtTime(sp.end)}` : 'start → end of the log';
    return `<svg class="an-spark" viewBox="0 0 ${W} ${H}" preserveAspectRatio="none"><title>${esc(`Occurrences over the log (${what})`)}</title>${bars}</svg>`;
  }

  private levelSegs(levels: number[], count: number): string {
    return LEVEL_ORDER.filter((l) => levels[l] > 0)
      .map((l) => `<i style="width:${pct(levels[l], count).toFixed(2)}%;background:${LEVEL_COLORS[l]}"></i>`)
      .join('');
  }

  private patternsCard(title: string, sub: string, list: AnalyticsPattern[], r: AnalyticsResult, by: 'count' | 'errors' | 'first' | 'last' | 'dur'): string {
    if (!list.length) return by === 'count' ? this.card(title, sub, '<div class="muted">Nothing read yet</div>', 'wide') : '';
    const of = Math.max(1, r.scanned);
    const rows = list
      .map((p) => {
        const value =
          by === 'errors' ? fmtCompact(errorsOf(p.levels)) : by === 'dur' ? fmtMs(p.durAvg) : fmtCompact(p.count);
        const extra = by === 'first' ? `since ${fmtTime(p.first)}` : by === 'last' ? `last ${fmtTime(p.last)}` : by === 'dur' ? `max ${fmtMs(p.durMax)} · ${fmtNum(p.durCount)}×` : fmtPct(p.count, of);
        const tip = [
          p.sample,
          `${fmtNum(p.count)} entries (${fmtPct(p.count, of)}) · ${fmtNum(errorsOf(p.levels))} errors · ${fmtNum(p.levels[Level.Warn])} warnings`,
          p.first != null ? `${fmtTime(p.first)} → ${fmtTime(p.last)}` : '',
          p.durCount ? `duration avg ${fmtMs(p.durAvg)}, max ${fmtMs(p.durMax)}` : '',
          'Click: show only this pattern · Shift+click: hide it',
        ].filter(Boolean).join('\n');
        return `<div class="an-pt act"${this.mark(tip, (sh) => this.host.filterPattern(p.template, sh ? 'exclude' : 'include'))}><span class="n">${value}</span><span class="lv">${this.levelSegs(p.levels, p.count)}</span><span class="tpl">${templateHtml(p.template)}</span>${by === 'count' ? this.spark(p, r) : ''}<span class="x muted">${esc(extra)}</span></div>`;
      })
      .join('');
    return this.card(title, sub, `<div class="an-pts">${rows}</div>`, by === 'count' ? 'wide' : '');
  }

  private exceptionsCard(r: AnalyticsResult): string {
    if (!r.exceptions.length) return '';
    const of = r.exceptions.reduce((a, e) => a + e.count, 0);
    const rows = r.exceptions.slice(0, 12).map((e) => ({
      label: e.name.replace(/^(?:[a-z_$][\w$]*\.)+/, ''),
      count: e.count,
      color: 'var(--tk-exception)',
      tip: `${e.name}\n${fmtNum(e.count)}× · ${fmtTime(e.first)} → ${fmtTime(e.last)}\n${HINT}`,
      fn: (sh: boolean) => this.host.filterText(e.name, sh ? 'exclude' : 'include'),
    }));
    return this.card('Exceptions', 'Exception and error types anywhere in the entries, stack traces included', this.bars(rows, of));
  }

  private burstsCard(r: AnalyticsResult): string {
    if (!r.bursts.length) return '';
    const rows = r.bursts.map((b) => ({
      label: `${fmtTime(b.from)} · ${fmtSpan(b.to - b.from)}`,
      count: b.errors,
      color: 'var(--error)',
      tip: `${fmtNum(b.errors)} errors of ${fmtNum(b.total)} entries, ${b.factor.toFixed(1)}× the usual rate\nClick: show this time range`,
      fn: () => this.host.filterTime(b.from, b.to),
    }));
    return this.card('Error bursts', 'Times with far more errors than usual', this.bars(rows, errorsOf(r.levels)));
  }

  private gapsCard(r: AnalyticsResult): string {
    if (!r.gaps.length) return '';
    const max = Math.max(...r.gaps.map((g) => g.ms));
    const rows = r.gaps
      .map(
        (g) =>
          `<div class="an-bar act"${this.mark(`No entries for ${fmtSpan(g.ms)}\n${fmtTime(g.from)} → ${fmtTime(g.to)}\nClick: jump there`, () => this.host.jumpToTime(g.from))}><span class="lbl">${esc(fmtTime(g.from))}</span><span class="track"><i style="width:${pct(g.ms, max).toFixed(1)}%;background:var(--fg-3)"></i></span><span class="n">${esc(fmtSpan(g.ms))}</span></div>`,
      )
      .join('');
    return this.card('Silences', 'Longest stretches without a single entry', `<div class="an-bars">${rows}</div>`);
  }

  private histogram(num: AnalyticsNumeric, unit: (x: number) => string, fn?: (lo: number, hi: number) => void): string {
    const { start, width, counts } = num.hist;
    const max = Math.max(1, ...counts);
    const cols = counts
      .map((c, i) => {
        const lo = start + i * width, hi = lo + width;
        return `<div class="col"${this.mark(`${unit(lo)} – ${unit(hi)}\n≈ ${fmtNum(c)} values`, fn && (() => fn(lo, hi)))}><span class="stack" style="height:${pct(c, max).toFixed(1)}%"></span></div>`;
      })
      .join('');
    const stats = [
      ['min', num.min],
      ['p50', num.p50],
      ['p90', num.p90],
      ['p99', num.p99],
      ['max', num.max],
      ['avg', num.avg],
    ]
      .map(([k, v]) => `<span><span class="muted">${k}</span> ${esc(unit(v as number))}</span>`)
      .join('');
    return `<div class="an-cols hist">${cols}</div><div class="an-stats">${stats}</div>`;
  }

  private durationsCard(r: AnalyticsResult): string {
    const d = r.durations;
    if (!d) return '';
    return this.card('Durations', `${fmtNum(d.count)} durations found in messages ("took 120 ms")`, this.histogram(d, fmtMs));
  }

  private fieldsCard(r: AnalyticsResult): string {
    const fields = r.fields.filter((f) => f.top.length);
    if (!fields.length) return '';
    const of = Math.max(1, r.scanned);
    const cards = fields
      .slice(0, 24)
      .map((f) => {
        const sub = `${fmtNum(f.distinct)}${f.distinctCapped ? '+' : ''} distinct · in ${fmtPct(f.present, of)} of entries`;
        const top = this.bars(
          f.top.slice(0, 8).map(([v, n]) => ({
            label: v,
            count: n,
            tip: `${f.key} = ${v}\n${fmtNum(n)} entries (${fmtPct(n, f.present)})\n${HINT}`,
            fn: (sh: boolean) => this.host.filterField(f.key, v, sh ? 'exclude' : 'include'),
          })),
          f.present,
        );
        const num = f.numeric ? this.histogram(f.numeric, fmtVal) : '';
        return `<div class="an-field"><h4>${esc(f.key)}</h4><div class="an-sub muted">${esc(sub)}</div>${num}${top}</div>`;
      })
      .join('');
    return this.card('Fields', 'Value distributions of every parsed field; numbers get a histogram and percentiles', `<div class="an-fields">${cards}</div>`, 'wide');
  }

  private valuesCard(title: string, values: ValueCount[], of: number): string {
    if (!values.length) return '';
    const rows = values.slice(0, 10).map(([v, n]) => ({
      label: v,
      count: n,
      color: 'var(--tk-id)',
      tip: `${v}\n${fmtNum(n)} entries\n${HINT}`,
      fn: (sh: boolean) => this.host.filterText(v, sh ? 'exclude' : 'include'),
    }));
    return this.card(title, '', this.bars(rows, of));
  }

  // ----------------------------------------------------------- interaction

  private target(e: Event): HTMLElement | SVGElement | null {
    return (e.target as Element).closest?.('[data-tip]') as HTMLElement | null;
  }

  private onClick(e: MouseEvent) {
    const t = this.target(e);
    const a = t?.getAttribute('data-a');
    if (a == null) return;
    e.preventDefault();
    this.acts[Number(a)]?.(e.shiftKey);
  }

  private showTip(t: Element, x: number, y: number) {
    const text = t.getAttribute('data-tip') ?? '';
    this.tip.textContent = text;
    this.tip.classList.remove('hidden');
    const box = this.el.getBoundingClientRect();
    const w = this.tip.offsetWidth, hgt = this.tip.offsetHeight;
    let left = x - box.left + 14, top = y - box.top + 14;
    if (left + w > box.width - 4) left = Math.max(4, x - box.left - w - 14);
    if (top + hgt > box.height - 4) top = Math.max(4, y - box.top - hgt - 10);
    this.tip.style.left = `${left}px`;
    this.tip.style.top = `${top}px`;
  }

  private onMove(e: MouseEvent) {
    if (this.drag) return;
    const t = this.target(e);
    if (!t) return this.tip.classList.add('hidden');
    this.showTip(t, e.clientX, e.clientY);
  }

  // drag over the timeline: a time range filter
  private onDown(e: MouseEvent) {
    const svg = (e.target as Element).closest?.('svg.an-timeline') as SVGSVGElement | null;
    if (!svg || e.button !== 0) return;
    const box = svg.getBoundingClientRect();
    const x = e.clientX - box.left;
    this.drag = { svg, x0: x, x1: x, start: Number(svg.dataset.start), end: Number(svg.dataset.end) };
  }

  private onDrag(e: MouseEvent) {
    const d = this.drag;
    if (!d) return;
    const box = d.svg.getBoundingClientRect();
    d.x1 = Math.max(0, Math.min(box.width, e.clientX - box.left));
    const sel = d.svg.querySelector('rect.sel') as SVGRectElement;
    const vb = d.svg.viewBox.baseVal.width / Math.max(1, box.width);
    const lo = Math.min(d.x0, d.x1), hi = Math.max(d.x0, d.x1);
    if (hi - lo < 4) return sel.classList.add('hidden');
    sel.classList.remove('hidden');
    sel.setAttribute('x', String(lo * vb));
    sel.setAttribute('width', String((hi - lo) * vb));
    const at = (x: number) => d.start + (x / box.width) * (d.end - d.start);
    this.tip.textContent = `${fmtTime(at(lo))} → ${fmtTime(at(hi))}\nRelease: show this time range`;
    this.tip.classList.remove('hidden');
  }

  private onUp(e: MouseEvent) {
    const d = this.drag;
    if (!d) return;
    this.drag = null;
    const box = d.svg.getBoundingClientRect();
    const lo = Math.min(d.x0, d.x1), hi = Math.max(d.x0, d.x1);
    if (hi - lo < 4) return; // a click: the bar's own action
    e.preventDefault();
    // swallow the click that follows the drag
    const stop = (ev: MouseEvent) => ev.stopPropagation();
    this.el.addEventListener('click', stop, { capture: true, once: true });
    setTimeout(() => this.el.removeEventListener('click', stop, { capture: true }), 0);
    const at = (x: number) => d.start + (x / box.width) * (d.end - d.start);
    this.tip.classList.add('hidden');
    this.host.filterTime(Math.floor(at(lo)), Math.ceil(at(hi)));
  }
}
