// Bottom pane: the selected entry in full, with parsed fields and pretty-printed JSON/XML.

import type { RowData } from '../core/types';
import { formatDuration, formatTime } from '../core/timestamp';
import type { Tab } from './state';
import { esc, h, prettyEntry, toast } from './util';

export class DetailView {
  constructor(
    private el: HTMLElement,
    private onResize: (height: number) => void,
    /** Right-click on a parsed field row. */
    private onFieldMenu: (e: MouseEvent, row: RowData, key: string) => void,
  ) {}

  render(tab: Tab | null, row: RowData | undefined, selectionEnd?: RowData) {
    this.el.innerHTML = '';
    const resizer = h('div', { class: 'resizer' });
    resizer.addEventListener('mousedown', (e) => this.startResize(e));
    this.el.append(resizer);
    if (!tab || !row) {
      this.el.append(h('div', { class: 'placeholder' }, 'Select a row to see the full entry.'));
      return;
    }
    const src = tab.info.sources[row.source];
    const bar = h('div', { class: 'bar' });
    bar.append(
      h('span', {}, 'Line ', h('b', {}, String(row.line)), row.lineCount > 1 ? ` (+${row.lineCount - 1} lines)` : ''),
    );
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
    pre.innerHTML = prettyEntry(row.text);
    const body = h('div', { class: 'body' }, table.rows.length ? fields : null, pre);
    this.el.append(bar, body);
  }

  private startResize(e: MouseEvent) {
    e.preventDefault();
    const startY = e.clientY;
    const startH = this.el.offsetHeight;
    const move = (ev: MouseEvent) => {
      const hgt = Math.max(80, Math.min(window.innerHeight * 0.7, startH - (ev.clientY - startY)));
      this.el.style.height = `${hgt}px`;
    };
    const up = () => {
      window.removeEventListener('mousemove', move);
      window.removeEventListener('mouseup', up);
      this.onResize(this.el.offsetHeight);
    };
    window.addEventListener('mousemove', move);
    window.addEventListener('mouseup', up);
  }
}
