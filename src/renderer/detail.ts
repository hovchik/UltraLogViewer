// Details pane (docked at the bottom or on the right): the selected entry in full, with parsed
// fields and pretty-printed JSON/XML.

import { fold } from '../core/folding';
import type { PatternStat, RowData } from '../core/types';
import { formatDuration, formatTime } from '../core/timestamp';
import type { Tab, ViewSettings } from './state';
import { esc, fmtNum, h, prettyEntry, toast } from './util';

/** The raw entry as HTML, with runs of framework frames folded into clickable placeholders. */
export function foldedEntryHtml(text: string, view: ViewSettings, unfolded: boolean): string {
  if (!view.foldFrames || unfolded) return prettyEntry(text);
  const lines = fold(text.split('\n'), view.frameworkPackages);
  if (!lines.some((l) => l.folded)) return prettyEntry(text);
  const out: string[] = [];
  let seg: string[] = [];
  const flush = () => {
    if (seg.length) out.push(prettyEntry(seg.join('\n')));
    seg = [];
  };
  for (const l of lines) {
    if (!l.folded) {
      seg.push(l.line);
      continue;
    }
    flush();
    out.push(`<span class="fold" title="Click to show all frames of this entry">${esc(l.line)}</span>`);
  }
  flush();
  return out.join('\n');
}

export class DetailView {
  constructor(
    private el: HTMLElement,
    /** Called after a drag with the new height (bottom dock) or width (right dock). */
    private onResize: (size: number, dock: 'bottom' | 'right') => void,
    /** Right-click on a parsed field row. */
    private onFieldMenu: (e: MouseEvent, row: RowData, key: string) => void,
    private dock: () => 'bottom' | 'right',
    private onToggleDock: () => void,
    private view: () => ViewSettings,
    /** Click on a "… N framework frames" placeholder. */
    private onUnfold: (key: string) => void,
  ) {}

  /** The selected pattern of the patterns view: its template and sample message. */
  renderPattern(p: PatternStat | undefined, scanned: number) {
    this.el.innerHTML = '';
    const resizer = h('div', { class: 'resizer' });
    resizer.addEventListener('mousedown', (e) => this.startResize(e));
    this.el.append(resizer);
    if (!p) {
      this.el.append(h('div', { class: 'bar' }, h('span', { class: 'sp' }), this.dockButton()), h('div', { class: 'placeholder' }, 'Select a pattern to see a sample message.'));
      return;
    }
    const pct = scanned ? ((p.count / scanned) * 100).toFixed(1) : '0.0';
    const bar = h(
      'div',
      { class: 'bar' },
      h('span', {}, 'Pattern ', h('b', {}, `${fmtNum(p.count)} entries`), ` (${pct}%)`),
      h('span', { class: 'sp' }),
      h('button', { class: 'mini', onclick: () => navigator.clipboard.writeText(p.sample).then(() => toast('Sample copied')) }, 'Copy sample'),
      this.dockButton(),
    );
    const tpl = h('div', { class: 'fields' }, h('table', {}, h('tr', {}, h('td', { class: 'k' }, 'template'), h('td', {}, p.template))));
    const pre = h('pre', { class: 'pattern-sample' });
    pre.textContent = p.sample;
    this.el.append(bar, h('div', { class: 'body' }, tpl, pre));
  }

  private dockButton() {
    const right = this.dock() === 'right';
    return h('button', { class: 'mini dock-btn', title: right ? 'Dock bottom' : 'Dock right', onclick: () => this.onToggleDock() }, '⇆');
  }

  render(tab: Tab | null, row: RowData | undefined, selectionEnd?: RowData) {
    this.el.innerHTML = '';
    const resizer = h('div', { class: 'resizer' });
    resizer.addEventListener('mousedown', (e) => this.startResize(e));
    this.el.append(resizer);
    if (!tab || !row) {
      this.el.append(h('div', { class: 'bar' }, h('span', { class: 'sp' }), this.dockButton()), h('div', { class: 'placeholder' }, 'Select a row to see the full entry.'));
      return;
    }
    const src = tab.info.sources[row.source];
    const bar = h('div', { class: 'bar' });
    bar.append(
      h('span', {}, 'Line ', h('b', {}, String(row.line)), row.lineCount > 1 ? ` (+${row.lineCount - 1} lines)` : ''),
    );
    if (row.repeat > 1) bar.append(h('span', { class: 'repeated' }, 'Repeated ', h('b', {}, fmtNum(row.repeat)), ' times'));
    if (tab.info.merged && src) bar.append(h('span', {}, 'Source ', h('b', {}, src.displayName)));
    if (row.time !== null) bar.append(h('span', {}, 'Time ', h('b', {}, formatTime(row.time))));
    if (selectionEnd && selectionEnd.row !== row.row && row.time !== null && selectionEnd.time !== null) {
      bar.append(h('span', { title: 'Time between the first and last selected rows' }, 'Δ selection ', h('b', {}, formatDuration(Math.abs(selectionEnd.time - row.time)))));
    }
    const note = tab.bookmarks.get(row.key)?.note;
    if (note) bar.append(h('span', {}, '🔖 ', h('i', {}, note)));
    bar.append(
      h('span', { class: 'sp' }),
      h('button', { class: 'mini', onclick: () => navigator.clipboard.writeText(row.text).then(() => toast('Entry copied')) }, 'Copy entry'),
      this.dockButton(),
    );
    const fields = h('div', { class: 'fields' });
    const table = h('table');
    // Format columns first, then the extra fields in entry order.
    const order = tab.info.columns.map((c) => c.key).filter((k) => k in row.fields);
    for (const k of Object.keys(row.fields)) if (!order.includes(k)) order.push(k);
    for (const k of order) {
      const v = row.fields[k];
      if (!v || k === 'message') continue;
      const tr = document.createElement('tr');
      tr.title = 'Right-click to filter by this value';
      tr.innerHTML = `<td class="k">${esc(k)}</td><td>${esc(v.length > 300 ? v.slice(0, 300) + '…' : v)}</td>`;
      tr.addEventListener('contextmenu', (e) => {
        e.preventDefault();
        this.onFieldMenu(e, row, k);
      });
      table.append(tr);
    }
    fields.append(table);
    const pre = h('pre');
    pre.innerHTML = foldedEntryHtml(row.text, this.view(), tab.unfolded.has(row.key));
    pre.addEventListener('click', (e) => {
      if ((e.target as HTMLElement).closest('.fold')) this.onUnfold(row.key);
    });
    const body = h('div', { class: 'body' }, table.rows.length ? fields : null, pre);
    this.el.append(bar, body);
  }

  private startResize(e: MouseEvent) {
    e.preventDefault();
    const dock = this.dock();
    const startX = e.clientX;
    const startY = e.clientY;
    const startH = this.el.offsetHeight;
    const startW = this.el.offsetWidth;
    const move = (ev: MouseEvent) => {
      if (dock === 'right') {
        const w = Math.max(200, Math.min(window.innerWidth * 0.8, startW - (ev.clientX - startX)));
        this.el.style.width = `${w}px`;
      } else {
        const hgt = Math.max(80, Math.min(window.innerHeight * 0.7, startH - (ev.clientY - startY)));
        this.el.style.height = `${hgt}px`;
      }
    };
    const up = () => {
      window.removeEventListener('mousemove', move);
      window.removeEventListener('mouseup', up);
      this.onResize(dock === 'right' ? this.el.offsetWidth : this.el.offsetHeight, dock);
    };
    window.addEventListener('mousemove', move);
    window.addEventListener('mouseup', up);
  }
}
