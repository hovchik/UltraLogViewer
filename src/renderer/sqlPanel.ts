// The SQL panel (View › SQL, Ctrl+Shift+Q): a query editor over the tab's entries (the engine's LogSql), the results as a
// table or a bar chart, and a click on a result row that has a line (or row) jumps to that entry. It docks below the
// grid so both stay in sight; each tab keeps its own query and result (and the engine its own table).

import { engine } from './api';
import { SQL_DEFAULT_CAP, SQL_DEFAULT_QUERY, sqlCell, sqlChartable, SqlColumn, sqlExamples, SqlResult } from '../core/sqlSchema';
import type { Tab } from './state';
import { fmtNum, h } from './util';

export interface SqlHost {
  /** The tab the panel is about: the active tab's main pane. */
  owner(): Tab | null;
  /** Selects a view row in the tab's main pane. */
  jumpTo(tab: Tab, row: number): void;
  /** The panel opened or closed (for the View menu's check mark). */
  shownChanged(): void;
}

interface SqlTabState {
  query: string;
  result: SqlResult | null;
  schema: SqlColumn[] | null;
  /** Load at most this many entries; 0 = all. */
  cap: number;
  /** Counts runs, so the answer of a superseded run is dropped. */
  seq: number;
}

const ms = (v: number) => (v >= 1000 ? `${(v / 1000).toFixed(1)} s` : `${Math.round(v)} ms`);

/** "12 rows in 4 ms  ·  table loaded in 1.2 s  ·  first 2,000,000 of 8,695,680 entries" (the same text as the Avalonia app). */
export function sqlStatusText(r: SqlResult): string {
  const parts: string[] = [];
  if (!r.error) parts.push(`${fmtNum(r.rows.length)}${r.truncated ? '+' : ''} ${r.rows.length === 1 && !r.truncated ? 'row' : 'rows'} in ${ms(r.queryMs)}`);
  if (r.loadMs >= 1) parts.push(`table loaded in ${ms(r.loadMs)}`);
  parts.push(r.capped ? `first ${fmtNum(r.loaded)} of ${fmtNum(r.total)} entries` : `${fmtNum(r.loaded)} entries`);
  return parts.join('  ·  ');
}

export class SqlPanel {
  shown = false;
  private states = new WeakMap<Tab, SqlTabState>();
  private editor: HTMLTextAreaElement;
  private examples: HTMLSelectElement;
  private runBtn: HTMLButtonElement;
  private cancelBtn: HTMLButtonElement;
  private chartBtn: HTMLButtonElement;
  private loadAllBtn: HTMLButtonElement;
  private status: HTMLElement;
  private prog: HTMLElement;
  private error: HTMLElement;
  private columnsHint: HTMLElement;
  private tableWrap: HTMLElement;
  private chart: HTMLCanvasElement;
  private tab: Tab | null = null;
  private selected = -1;
  private chartOn = false;
  private exampleList: { title: string; sql: string }[] = [];

  constructor(readonly el: HTMLElement, private splitter: HTMLElement, private host: SqlHost) {
    this.editor = h('textarea', { class: 'sql-editor', spellcheck: 'false', placeholder: 'SELECT … FROM logs   (Ctrl+Enter runs)' });
    this.runBtn = h('button', { class: 'btn primary', title: 'Run the query (Ctrl+Enter)', onclick: () => void this.run() }, 'Run');
    this.cancelBtn = h('button', { class: 'mini hidden', onclick: () => this.tab && void engine.sqlCancel(this.tab.id) }, 'Cancel');
    this.examples = h('select', { class: 'btn', title: 'Example queries' });
    this.examples.onchange = () => {
      const ex = this.exampleList[this.examples.selectedIndex - 1];
      this.examples.selectedIndex = 0;
      if (!ex) return;
      this.editor.value = ex.sql;
      void this.run();
    };
    this.chartBtn = h('button', { class: 'btn tog-btn', title: 'Show a (label, number) result as a bar chart', disabled: true, onclick: () => this.setChart(!this.chartOn) }, 'Chart');
    this.prog = h('span', { class: 'prog hidden' }, h('i'));
    this.status = h('span', { class: 'sql-status muted' });
    this.loadAllBtn = h('button', { class: 'mini hidden', title: `Load every entry instead of the first ${fmtNum(SQL_DEFAULT_CAP)} (uses more memory)`, onclick: () => this.loadAll() }, 'Load all');
    const close = h('button', { class: 'mini', title: 'Close the SQL panel (Ctrl+Shift+Q)', onclick: () => this.show(false) }, '×');
    const head = h('div', { class: 'sql-head' }, h('b', {}, 'SQL'), this.runBtn, this.cancelBtn, this.examples, this.chartBtn, this.prog, this.status, this.loadAllBtn, close);
    this.columnsHint = h('div', { class: 'sql-cols muted' });
    this.error = h('div', { class: 'sql-error hidden' });
    this.tableWrap = h('div', { class: 'sql-table', tabindex: '0' });
    this.chart = h('canvas', { class: 'sql-chart hidden' });
    const body = h(
      'div',
      { class: 'sql-body' },
      h('div', { class: 'sql-left' }, this.editor, this.columnsHint),
      h('div', { class: 'sql-right' }, this.error, this.tableWrap, this.chart),
    );
    el.append(head, body);

    this.editor.addEventListener('keydown', (e) => {
      if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
        e.preventDefault();
        void this.run();
      } else if (e.key === 'Escape') {
        e.preventDefault();
        this.show(false);
      }
    });
    this.tableWrap.addEventListener('mousedown', (e) => {
      const tr = (e.target as HTMLElement).closest('tr[data-i]') as HTMLElement | null;
      if (tr) this.select(Number(tr.dataset.i));
    });
    this.tableWrap.addEventListener('keydown', (e) => {
      const step = e.key === 'ArrowDown' ? 1 : e.key === 'ArrowUp' ? -1 : e.key === 'PageDown' ? 10 : e.key === 'PageUp' ? -10 : 0;
      if (!step) return;
      e.preventDefault();
      this.select(this.selected + step);
    });
    this.chart.addEventListener('mousemove', (e) => this.chartTip(e));
    new ResizeObserver(() => this.chartOn && this.drawChart()).observe(this.chart);
    splitter.addEventListener('mousedown', (e) => this.startResize(e));
  }

  private state(tab: Tab): SqlTabState {
    let s = this.states.get(tab);
    if (!s) this.states.set(tab, (s = { query: SQL_DEFAULT_QUERY, result: null, schema: null, cap: SQL_DEFAULT_CAP, seq: 0 }));
    return s;
  }

  toggle() {
    this.show(!this.shown);
  }

  show(on: boolean) {
    if (!this.host.owner()) on = false;
    if (on === this.shown) return;
    this.saveQuery();
    this.shown = on;
    this.el.classList.toggle('hidden', !on);
    this.splitter.classList.toggle('hidden', !on);
    this.host.shownChanged();
    if (on) {
      this.tabChanged();
      this.editor.focus();
    } else if (this.tab) void engine.sqlCancel(this.tab.id);
  }

  private saveQuery() {
    if (this.tab) this.state(this.tab).query = this.editor.value;
  }

  /** Shows the active tab's query and result (when the panel opens and when the tab changes). */
  tabChanged() {
    if (!this.shown) return;
    const tab = this.host.owner();
    if (tab === this.tab) return;
    this.saveQuery();
    if (this.tab) void engine.sqlCancel(this.tab.id);
    this.tab = tab;
    if (!tab) return this.show(false);
    const st = this.state(tab);
    this.editor.value = st.query;
    this.render(st.result);
    if (st.schema) this.renderSchema(st.schema);
    else if (!tab.info.indexing) void engine.sqlSchema(tab.id).then((cols) => {
      st.schema = cols;
      if (this.tab === tab) this.renderSchema(cols);
    });
  }

  /** The tab was closed: forget it (the engine drops its table with the document). */
  closed(tab: Tab) {
    this.states.delete(tab);
    if (this.tab === tab) this.tab = null;
  }

  private renderSchema(cols: SqlColumn[]) {
    this.columnsHint.textContent = `Tables: logs, view (filtered).  Columns: ${cols.map((c) => c.name).join(', ')}`;
    this.columnsHint.title = cols.map((c) => (c.field === null ? c.name : `${c.name}  ← field "${c.field}"`)).join('\n');
    this.exampleList = sqlExamples(cols);
    this.examples.replaceChildren(h('option', { value: '' }, 'Examples'), ...this.exampleList.map((x) => h('option', {}, x.title)));
  }

  /** Load progress, from the engine's "sql" progress events. */
  progress(tab: Tab, fraction: number) {
    if (tab !== this.tab || fraction >= 1) return;
    const st = this.state(tab);
    this.status.textContent = st.cap > 0 && tab.info.total > st.cap ? `Loading the first ${fmtNum(st.cap)} entries…` : `Loading ${fmtNum(tab.info.total)} entries…`;
    this.prog.classList.remove('hidden');
    (this.prog.firstElementChild as HTMLElement).style.width = `${(fraction * 100).toFixed(1)}%`;
  }

  async run() {
    const tab = this.tab ?? this.host.owner();
    if (!tab || tab.info.indexing) return;
    this.tab = tab;
    const st = this.state(tab);
    st.query = this.editor.value;
    const seq = ++st.seq;
    this.runBtn.disabled = true;
    this.cancelBtn.classList.remove('hidden');
    this.status.textContent = 'Running…';
    let r: SqlResult;
    try {
      // Fetched on every run: the columns change when the format does (a cheap sample of the entries).
      st.schema = await engine.sqlSchema(tab.id);
      if (this.tab === tab) this.renderSchema(st.schema);
      r = await engine.sqlQuery(tab.id, st.query, st.cap);
    } catch (e) {
      r = { columns: [], rows: [], truncated: false, error: (e as Error).message, queryMs: 0, loadMs: 0, loaded: 0, total: tab.info.total, capped: false };
    }
    if (seq !== st.seq) return; // a newer run took over
    this.runBtn.disabled = false;
    this.cancelBtn.classList.add('hidden');
    this.prog.classList.add('hidden');
    if (!r.cancelled) st.result = r;
    if (this.tab !== tab) return;
    if (r.cancelled) this.status.textContent = 'Cancelled';
    else this.render(r);
  }

  private loadAll() {
    if (!this.tab) return;
    this.state(this.tab).cap = 0;
    void this.run();
  }

  private setChart(on: boolean) {
    this.chartOn = on;
    this.render(this.tab ? this.state(this.tab).result : null);
  }

  private render(r: SqlResult | null) {
    this.error.classList.toggle('hidden', !r?.error);
    this.error.textContent = r?.error ?? '';
    const chartable = !!r && sqlChartable(r);
    this.chartBtn.disabled = !chartable;
    const chart = chartable && this.chartOn;
    this.chartBtn.classList.toggle('on', chart);
    this.chart.classList.toggle('hidden', !chart);
    this.tableWrap.classList.toggle('hidden', chart);
    this.loadAllBtn.classList.toggle('hidden', !r?.capped);
    this.status.textContent = r ? sqlStatusText(r) : '';
    this.selected = -1;
    this.renderTable(r && !r.error ? r : null);
    if (chart) this.drawChart();
  }

  private renderTable(r: SqlResult | null) {
    this.tableWrap.replaceChildren();
    if (!r) return;
    const head = h('tr', {}, ...r.columns.map((c) => h('th', {}, c)));
    const body = document.createDocumentFragment();
    r.rows.forEach((row, i) => {
      const tr = h('tr', { 'data-i': String(i) });
      for (const v of row) tr.append(h('td', { class: v === null ? 'null' : typeof v === 'number' ? 'num' : undefined, title: typeof v === 'string' && v.length > 60 ? v.slice(0, 2000) : undefined }, sqlCell(v)));
      body.append(tr);
    });
    this.tableWrap.append(h('table', {}, h('thead', {}, head), h('tbody', {}, body)));
  }

  private select(i: number) {
    const r = this.tab && this.state(this.tab).result;
    if (!r || !r.rows.length || !this.tab) return;
    i = Math.max(0, Math.min(r.rows.length - 1, i));
    this.tableWrap.querySelector('tr.sel')?.classList.remove('sel');
    const tr = this.tableWrap.querySelector(`tr[data-i="${i}"]`) as HTMLElement | null;
    tr?.classList.add('sel');
    tr?.scrollIntoView({ block: 'nearest' });
    this.selected = i;
    void this.jump(this.tab, r, i);
  }

  /** Selects the entry of a result row: by its line (and source), else by its row in the view. */
  private async jump(tab: Tab, r: SqlResult, i: number) {
    const col = (name: string) => r.columns.findIndex((c) => c.toLowerCase() === name);
    const row = r.rows[i];
    const li = col('line');
    const ri = col('row');
    let target: number | null = null;
    if (li >= 0 && typeof row[li] === 'number') {
      const si = col('source');
      const name = si >= 0 ? row[si] : null;
      const src = typeof name === 'string' ? Math.max(0, tab.info.sources.findIndex((s) => s.displayName === name)) : 0;
      target = await engine.rowForLine(tab.id, src, row[li] as number);
    } else if (ri >= 0 && typeof row[ri] === 'number' && (row[ri] as number) < tab.info.viewCount) target = row[ri] as number;
    if (target !== null && this.tab === tab) this.host.jumpTo(tab, target);
  }

  // -------------------------------------------------------------------- chart

  private static readonly LEFT = 56;
  private static readonly BOTTOM = 22;
  private static readonly TOP = 10;
  private static readonly MAX_BARS = 1000;

  private bars(): SqlResult | null {
    const r = this.tab ? this.state(this.tab).result : null;
    return r && sqlChartable(r) ? r : null;
  }

  private drawChart(hover = -1) {
    const r = this.bars();
    const c = this.chart;
    const dpr = window.devicePixelRatio || 1;
    const w = c.clientWidth;
    const hgt = c.clientHeight;
    c.width = Math.max(1, Math.round(w * dpr));
    c.height = Math.max(1, Math.round(hgt * dpr));
    const g = c.getContext('2d');
    if (!g || !r) return;
    g.scale(dpr, dpr);
    const css = getComputedStyle(this.el);
    const color = (v: string) => css.getPropertyValue(v).trim();
    const n = Math.min(SqlPanel.MAX_BARS, r.rows.length);
    const num = (v: unknown) => (typeof v === 'number' ? v : 0);
    let max = 0;
    let min = 0;
    for (let i = 0; i < n; i++) {
      max = Math.max(max, num(r.rows[i][1]));
      min = Math.min(min, num(r.rows[i][1]));
    }
    if (max <= min) max = min + 1;
    const { LEFT, BOTTOM, TOP } = SqlPanel;
    const plotW = Math.max(1, w - LEFT - 8);
    const plotH = Math.max(1, hgt - BOTTOM - TOP);
    const y = (v: number) => TOP + (plotH * (max - v)) / (max - min);
    const bw = plotW / n;
    g.font = `11px ${color('--mono')}`;
    g.fillStyle = color('--border');
    g.fillRect(LEFT, y(0), plotW, 1);
    g.fillStyle = color('--fg-3');
    g.textAlign = 'right';
    g.textBaseline = 'middle';
    g.fillText(sqlCell(max), LEFT - 6, TOP);
    g.fillText(sqlCell(min < 0 ? min : 0), LEFT - 6, min < 0 ? y(min) : y(0));
    for (let i = 0; i < n; i++) {
      const v = num(r.rows[i][1]);
      const y0 = y(Math.max(0, v));
      const y1 = y(Math.min(0, v));
      g.fillStyle = i === hover ? color('--tk-word') : color('--accent');
      const pad = bw > 4 ? 1 : 0;
      g.fillRect(LEFT + i * bw + pad, y0, Math.max(1, bw - 2 * pad), Math.max(1, y1 - y0));
    }
    g.fillStyle = color('--fg-2');
    g.textBaseline = 'top';
    g.textAlign = 'left';
    g.fillText(sqlCell(r.rows[0][0]), LEFT, hgt - BOTTOM + 4);
    if (n > 1) {
      g.textAlign = 'right';
      g.fillText(sqlCell(r.rows[n - 1][0]), w - 8, hgt - BOTTOM + 4);
    }
  }

  private chartTip(e: MouseEvent) {
    const r = this.bars();
    if (!r) return;
    const n = Math.min(SqlPanel.MAX_BARS, r.rows.length);
    const x = e.offsetX - SqlPanel.LEFT;
    const i = x >= 0 ? Math.floor(x / (Math.max(1, this.chart.clientWidth - SqlPanel.LEFT - 8) / n)) : -1;
    const ok = i >= 0 && i < n;
    this.chart.title = ok ? `${sqlCell(r.rows[i][0])}: ${sqlCell(r.rows[i][1])}` : '';
    this.drawChart(ok ? i : -1);
  }

  private startResize(e: MouseEvent) {
    e.preventDefault();
    const startY = e.clientY;
    const startH = this.el.offsetHeight;
    const move = (ev: MouseEvent) => (this.el.style.height = `${Math.max(120, Math.min(window.innerHeight * 0.8, startH - (ev.clientY - startY)))}px`);
    const up = () => {
      window.removeEventListener('mousemove', move);
      window.removeEventListener('mouseup', up);
    };
    window.addEventListener('mousemove', move);
    window.addEventListener('mouseup', up);
  }
}
