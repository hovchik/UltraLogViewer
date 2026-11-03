// Pinned rows: entries kept in a compact strip above the grid (a request start, the first error), so they stay in
// sight while scrolling. Click one to jump to it, × to unpin. Saved per file next to the bookmarks, and in workspaces.

import type { RowData } from '../core/types';
import type { WorkspacePin } from './workspace';
import { engine } from './api';
import { Pin, Settings, Tab } from './state';
import { h, toast } from './util';

export interface PinsHost {
  active(): Tab | null;
  settings(): Settings;
  saveSettings(): void;
  /** A tab opened on a time window: its line numbers are not the file's, so its pins are not saved. */
  isPartial(t: Tab): boolean;
  /** Selects a view row and centres it. */
  select(row: number): void;
}

const firstLine = (s: string) => {
  const i = s.indexOf('\n');
  return (i < 0 ? s : s.slice(0, i)).slice(0, 200);
};

/** Pins in strip order: by view row (hidden ones last), then by file and line. */
export function orderedPins(t: Tab): Pin[] {
  return [...(t.pins?.values() ?? [])].sort((a, b) => (a.row ?? Infinity) - (b.row ?? Infinity) || a.source - b.source || a.line - b.line);
}

export class Pins {
  constructor(private el: HTMLElement, private host: PinsHost) {}

  has(t: Tab, key: string): boolean {
    return !!t.pins?.has(key);
  }

  toggle(row: RowData) {
    const t = this.host.active();
    if (!t) return;
    const pins = (t.pins ??= new Map());
    if (pins.has(row.key)) pins.delete(row.key);
    else pins.set(row.key, { key: row.key, line: row.line, source: row.source, preview: firstLine(row.fields.message || row.text), row: row.row });
    this.save(t);
    this.render();
  }

  unpin(t: Tab, key: string) {
    t.pins?.delete(key);
    this.save(t);
    this.render();
  }

  async jump(t: Tab, pin: Pin) {
    const row = await engine.rowForKey(t.id, pin.key);
    if (row === null) return toast('That pinned entry is hidden by the current filter');
    this.host.select(row);
  }

  /** Refreshes the view rows of a tab's pins (after a filter change). */
  async resolveRows(t: Tab) {
    if (!t.pins?.size) return;
    await Promise.all([...t.pins.values()].map(async (p) => (p.row = await engine.rowForKey(t.id, p.key))));
    if (t === this.host.active()) this.render();
  }

  private save(t: Tab) {
    if (this.host.isPartial(t)) return;
    const s = this.host.settings();
    s.pins ??= {};
    t.info.sources.forEach((src, i) => {
      const lines = [...(t.pins?.values() ?? [])].filter((p) => p.source === i).map((p) => p.line);
      if (lines.length) s.pins![src.path] = [...new Set(lines)].sort((a, b) => a - b);
      else delete s.pins![src.path];
    });
    this.host.saveSettings();
  }

  /** Brings back the saved pins of a tab's files (once its document is indexed). */
  async restore(t: Tab) {
    if (this.host.isPartial(t)) return;
    const saved = this.host.settings().pins ?? {};
    for (const [i, src] of t.info.sources.entries()) {
      for (const line of saved[src.path] ?? []) await this.add(t, i, line);
    }
    await this.resolveRows(t);
    if (t === this.host.active()) this.render();
  }

  private async add(t: Tab, source: number, line: number) {
    const key = await engine.keyForLine(t.id, source, line);
    if (!key || t.pins?.has(key)) return;
    const rows = await engine.rows(t.id, (await engine.rowForKey(t.id, key, true)) ?? 0, 1);
    const r = rows[0]?.key === key ? rows[0] : undefined;
    (t.pins ??= new Map()).set(key, { key, line, source, preview: r ? firstLine(r.fields.message || r.text) : '' });
  }

  /** Workspace: the pins of a tab, by file path. */
  workspacePins(t: Tab): WorkspacePin[] {
    return [...(t.pins?.values() ?? [])].map((p) => ({ path: t.info.sources[p.source]?.path ?? t.paths[p.source] ?? t.paths[0], line: p.line }));
  }

  /** Workspace: adds the saved pins to a tab whose document is ready, and remembers them for its files. */
  async applyWorkspacePins(t: Tab, pins: WorkspacePin[]) {
    for (const p of pins) {
      // As for bookmarks: saved paths are source paths (a zip member is `archive.zip::member`); a single-file tab takes
      // older entries too, and a pin of a file that was skipped (missing) is dropped rather than put on another file.
      let source = t.info.sources.findIndex((src) => src.path === p.path);
      if (source < 0) source = t.info.sources.length === 1 ? 0 : t.paths.indexOf(p.path);
      if (source >= 0) await this.add(t, source, p.line);
    }
    if (pins.length) this.save(t);
  }

  render() {
    const t = this.host.active();
    const pins = t ? orderedPins(t) : [];
    this.el.classList.toggle('hidden', !pins.length);
    this.el.innerHTML = '';
    if (!t) return;
    for (const pin of pins) {
      const where = t.info.merged ? `${t.info.sources[pin.source]?.displayName ?? '?'}:${pin.line}` : String(pin.line);
      const gone = pin.row === null;
      const el = h(
        'div',
        { class: gone ? 'pin gone' : 'pin', title: gone ? 'Hidden by the current filter' : 'Click to jump to this entry' },
        h('span', { class: 'pi' }, '📌'),
        h('span', { class: 'ln' }, where),
        h('span', { class: 't' }, pin.preview),
        h('button', { class: 'x', title: 'Unpin' }, '×'),
      );
      el.addEventListener('mousedown', (e) => {
        e.preventDefault(); // keep the keyboard focus in the grid
        if (e.button !== 0) return;
        if ((e.target as HTMLElement).closest('.x')) this.unpin(t, pin.key);
        else void this.jump(t, pin);
      });
      this.el.append(el);
    }
  }
}
