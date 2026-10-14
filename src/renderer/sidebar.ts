// Left panel: levels, field values, filter rules, time range, highlight rules and bookmarks.

import { validatePattern } from '../core/query';
import { ColumnDef, FilterRule, Level, LEVEL_NAMES } from '../core/types';
import { formatTime } from '../core/timestamp';
import type { Bookmark, HighlightRule, Settings, Tab } from './state';
import { uid } from './state';
import { ask, fmtNum, h, toast } from './util';

export interface SidebarHost {
  settings(): Settings;
  saveSettings(): void;
  applyFilter(): void;
  highlightsChanged(): void;
  jumpToBookmark(b: Bookmark): void;
  removeBookmark(b: Bookmark): void;
  bookmarkNote(b: Bookmark): void;
  /** Effective columns of the tab (including extra field columns). */
  columns(tab: Tab): ColumnDef[];
  isColumnShown(key: string): boolean;
  toggleColumn(key: string): void;
  /** Click on a field value (include) or right-click (exclude). */
  filterByValue(key: string, value: string, mode: 'include' | 'exclude'): void;
  rescanFields(): void;
  clearAllFilters(): void;
}

const LEVEL_COLORS = ['var(--fg-3)', 'var(--trace)', 'var(--debug)', 'var(--accent)', 'var(--warn)', 'var(--error)', 'var(--fatal-bg)'];
const LEVEL_LABELS = ['Other', 'Trace', 'Debug', 'Info', 'Warning', 'Error', 'Fatal'];

const toLocalInput = (ms: number | null | undefined) => (ms == null ? '' : formatTime(ms).slice(0, 19).replace(' ', 'T'));

export class Sidebar {
  constructor(private el: HTMLElement, private host: SidebarHost) {}

  render(tab: Tab | null) {
    this.lastTab = tab;
    const scroll = this.el.scrollTop;
    this.el.innerHTML = '';
    if (!tab) {
      this.el.append(this.highlightsSection());
      return;
    }
    this.fieldsEl = this.fieldsSection(tab);
    this.el.append(this.levelsSection(tab), this.fieldsEl, this.filtersSection(tab), this.timeSection(tab), this.highlightsSection(), this.bookmarksSection(tab));
    this.el.scrollTop = scroll;
  }

  // ---------------------------------------------------------------- levels

  private levelsSection(tab: Tab) {
    const f = tab.filter;
    const counts = tab.info.levelCounts;
    const sec = h('div', { class: 'sec' }, h('h3', {}, 'Levels', h('button', { class: 'mini', onclick: () => { f.levels = f.levels.map(() => true); this.host.applyFilter(); } }, 'All')));
    const order = [Level.Fatal, Level.Error, Level.Warn, Level.Info, Level.Debug, Level.Trace, Level.None];
    for (const l of order) {
      if (!counts[l] && f.levels[l]) continue;
      const row = h(
        'div',
        { class: f.levels[l] ? 'lvl' : 'lvl off', title: LEVEL_NAMES[l], onclick: () => { f.levels[l] = !f.levels[l]; this.host.applyFilter(); } },
        h('span', { class: 'sw', style: `background:${LEVEL_COLORS[l]}` }),
        LEVEL_LABELS[l],
        h('button', { class: 'lvl-only', title: 'Show only this level and above', onclick: (e: Event) => { e.stopPropagation(); f.levels = f.levels.map((_, i) => i >= l || (l === Level.None && i === 0)); this.host.applyFilter(); } }, '≥ only'),
        h('span', { class: 'n' }, fmtNum(counts[l] ?? 0)),
      );
      sec.append(row);
    }
    return sec;
  }

  // ---------------------------------------------------------------- fields

  private fieldsEl: HTMLElement | null = null;

  /** Re-renders only the Fields section (stats arrive asynchronously). */
  renderFields(tab: Tab) {
    if (tab !== this.lastTab || !this.fieldsEl?.isConnected) return;
    const next = this.fieldsSection(tab);
    this.fieldsEl.replaceWith(next);
    this.fieldsEl = next;
  }

  private fieldsSection(tab: Tab) {
    const st = tab.fieldStats;
    const sec = h(
      'div',
      { class: 'sec fields-sec' },
      h('h3', {}, 'Fields', h('button', { class: 'mini', title: 'Count the values again', onclick: () => this.host.rescanFields() }, '↻')),
    );
    if (!st) {
      sec.append(h('div', { class: 'empty-hint' }, 'Counting values…'));
      return sec;
    }
    sec.append(h('div', { class: 'empty-hint' }, `Top values in ${fmtNum(st.scanned)} of ${fmtNum(st.viewCount)} entries${st.sampled ? ' (sample)' : ''}`));
    if (!st.fields.length) sec.append(h('div', { class: 'empty-hint' }, 'No parsed fields in this view.'));
    const expanded = (tab.expandedFields ??= new Set());
    for (const f of st.fields) {
      const shown = this.host.isColumnShown(f.key);
      const open = expanded.has(f.key);
      sec.append(
        h(
          'div',
          {
            class: open ? 'fld open' : 'fld',
            title: `${f.key}: ${fmtNum(f.present)} entries have a value`,
            onclick: () => {
              if (open) expanded.delete(f.key);
              else expanded.add(f.key);
              this.renderFields(tab);
            },
          },
          h('span', { class: 'tw' }, open ? '▾' : '▸'),
          h('span', { class: 'name' }, f.key),
          h('span', { class: 'n' }, `${fmtNum(f.distinct)}${f.distinctCapped ? '+' : ''} ${f.distinct === 1 && !f.distinctCapped ? 'value' : 'values'}`),
          h(
            'button',
            { class: shown ? 'mini on' : 'mini', title: shown ? 'Hide this column' : 'Show as a column', onclick: (e: Event) => { e.stopPropagation(); this.host.toggleColumn(f.key); } },
            shown ? '✓ column' : '+ column',
          ),
        ),
      );
      if (!open) continue;
      for (const [v, c] of f.top) {
        const pct = f.present ? (c / f.present) * 100 : 0;
        sec.append(
          h(
            'div',
            {
              class: 'fv',
              title: `${v}\nClick: only this value · Right-click: exclude it`,
              onclick: () => this.host.filterByValue(f.key, v, 'include'),
              oncontextmenu: (e: Event) => { e.preventDefault(); this.host.filterByValue(f.key, v, 'exclude'); },
            },
            h('span', { class: 'v' }, v),
            h('span', { class: 'n' }, fmtNum(c)),
            h('i', { class: 'bar', style: `width:${pct.toFixed(1)}%` }),
          ),
        );
      }
    }
    return sec;
  }

  // --------------------------------------------------------------- filters

  private filtersSection(tab: Tab) {
    const f = tab.filter;
    const settings = this.host.settings();
    const presets = settings.presets ?? [];
    const presetSel = h('select', { title: 'Filter presets' }, h('option', { value: '' }, 'Presets…'), ...presets.map((p, i) => h('option', { value: String(i) }, p.name)));
    presetSel.onchange = () => {
      const p = presets[Number(presetSel.value)];
      if (!p) return;
      f.rules = p.rules.map((r) => ({ ...r, id: uid() }));
      f.query = p.query;
      (document.getElementById('quick') as HTMLInputElement).value = p.query;
      this.host.applyFilter();
    };
    const sec = h(
      'div',
      { class: 'sec' },
      h(
        'h3',
        {},
        'Filters',
        h(
          'span',
          {},
          presetSel,
          ' ',
          h('button', { class: 'mini', title: 'Save current filters as a preset', onclick: () => this.savePreset(tab) }, 'Save'),
          ' ',
          h('button', { class: 'mini clear-all', title: 'Clear all filters (levels, rules, query, time, group repeats, Clear)', onclick: () => this.host.clearAllFilters() }, 'Clear all'),
        ),
      ),
    );
    if (!f.rules.length) sec.append(h('div', { class: 'empty-hint' }, 'Add include (+) or exclude (−) rules. Entries must match any include and no exclude.'));
    const columns = this.host.columns(tab).filter((c) => c.key !== '__source');
    const titleOf = (key: string) => columns.find((c) => c.key === key)?.title ?? key;
    for (const r of f.rules) {
      const options = columns.map((c) => h('option', { value: c.key, selected: r.column === c.key }, c.title));
      if (r.column && !columns.some((c) => c.key === r.column)) options.push(h('option', { value: r.column, selected: true }, r.column));
      const colSel = h('select', { class: 'mini', title: 'Match in column' }, h('option', { value: '' }, 'any'), ...options);
      const label = r.patternMatch
        ? `Pattern ≈ ${r.text.length > 60 ? r.text.slice(0, 60) + '…' : r.text}`
        : r.exact && r.column
          ? `${titleOf(r.column)} = ${r.text}`
          : r.regex
            ? `/${r.text}/`
            : r.text;
      colSel.onchange = () => {
        r.column = colSel.value || undefined;
        this.host.applyFilter();
      };
      sec.append(
        h(
          'div',
          { class: r.enabled ? 'rule' : 'rule off' },
          h('input', { type: 'checkbox', checked: r.enabled, onchange: () => { r.enabled = !r.enabled; this.host.applyFilter(); } }),
          h('span', { class: `mode ${r.mode === 'include' ? 'inc' : 'exc'}`, title: r.mode }, r.mode === 'include' ? '+' : '−'),
          h('span', { class: 'txt', title: r.patternMatch ? `Pattern ≈ ${r.text}` : r.exact ? `${label} (whole value)` : r.text }, label),
          h('span', { class: 'col' }, r.caseSensitive && !r.patternMatch ? 'Aa' : ''),
          r.exact || r.patternMatch ? null : colSel,
          h('button', { class: 'mini danger', title: 'Remove', onclick: () => { f.rules = f.rules.filter((x) => x !== r); this.host.applyFilter(); } }, '×'),
        ),
      );
    }
    // add form
    const text = h('input', { type: 'text', placeholder: 'text or regex', spellcheck: false });
    const mode = h('select', {}, h('option', { value: 'include' }, '+ include'), h('option', { value: 'exclude' }, '− exclude'));
    const regex = h('input', { type: 'checkbox' });
    const cs = h('input', { type: 'checkbox' });
    const add = () => {
      if (!text.value) return;
      const rule: FilterRule = { id: uid(), text: text.value, mode: mode.value as FilterRule['mode'], regex: regex.checked, caseSensitive: cs.checked, wholeWord: false, enabled: true };
      const err = validatePattern(rule);
      if (err) return toast(err, true);
      f.rules.push(rule);
      this.host.applyFilter();
    };
    text.addEventListener('keydown', (e) => e.key === 'Enter' && add());
    const ctx = h('input', { type: 'number', min: 0, max: 50, value: String(f.context ?? 0), style: 'width:52px', title: 'Show N entries around each match' });
    ctx.onchange = () => {
      f.context = Math.max(0, Math.min(50, Number(ctx.value) || 0));
      this.host.applyFilter();
    };
    const bmOnly = h('input', { type: 'checkbox', checked: !!f.bookmarksOnly });
    bmOnly.onchange = () => {
      f.bookmarksOnly = bmOnly.checked;
      this.host.applyFilter();
    };
    sec.append(
      h(
        'div',
        { class: 'form' },
        h('div', { class: 'row' }, mode, text),
        h('div', { class: 'row' }, h('label', {}, regex, 'Regex'), h('label', {}, cs, 'Match case'), h('span', { style: 'flex:1' }), h('button', { class: 'mini', onclick: add }, 'Add')),
        h('div', { class: 'row' }, h('label', { title: 'Lines of context around matches (like grep -C)' }, 'Context', ctx), h('label', {}, bmOnly, 'Bookmarks only')),
      ),
    );
    return sec;
  }

  private async savePreset(tab: Tab) {
    const name = await ask('Save filter preset', { placeholder: 'Preset name' });
    if (!name) return;
    const s = this.host.settings();
    s.presets = (s.presets ?? []).filter((p) => p.name !== name);
    s.presets.push({ name, rules: tab.filter.rules.map((r) => ({ ...r })), query: tab.filter.query ?? '' });
    this.host.saveSettings();
    this.render(tab);
    toast(`Saved preset “${name}”`);
  }

  // ------------------------------------------------------------------ time

  private timeSection(tab: Tab) {
    const f = tab.filter;
    const from = h('input', { type: 'datetime-local', step: 1, value: toLocalInput(f.timeFrom) });
    const to = h('input', { type: 'datetime-local', step: 1, value: toLocalInput(f.timeTo) });
    const apply = () => {
      f.timeFrom = from.value ? new Date(from.value).getTime() : null;
      f.timeTo = to.value ? new Date(to.value).getTime() + 999 : null;
      this.host.applyFilter();
    };
    from.onchange = apply;
    to.onchange = apply;
    const info = tab.info;
    const span = info.timeMin != null && info.timeMax != null ? `${formatTime(info.timeMin)} → ${formatTime(info.timeMax)}` : 'No timestamps detected';
    return h(
      'div',
      { class: 'sec' },
      h('h3', {}, 'Time range', h('button', { class: 'mini', onclick: () => { f.timeFrom = f.timeTo = null; this.host.applyFilter(); } }, 'Clear')),
      h('div', { class: 'empty-hint' }, span),
      h('div', { class: 'form' }, h('div', { class: 'row' }, h('span', { style: 'width:34px' }, 'From'), from), h('div', { class: 'row' }, h('span', { style: 'width:34px' }, 'To'), to)),
      h('div', { class: 'empty-hint' }, 'Tip: drag across the timeline to select a range.'),
    );
  }

  // ------------------------------------------------------------ highlights

  private highlightsSection() {
    const s = this.host.settings();
    const list = (s.highlights ??= []);
    const sec = h('div', { class: 'sec' }, h('h3', {}, 'Highlights'));
    const changed = () => {
      this.host.saveSettings();
      this.host.highlightsChanged();
    };
    for (const r of list) {
      const color = h('input', { type: 'color', value: r.color, class: 'hl-sw' });
      color.oninput = () => {
        r.color = color.value;
        changed();
      };
      sec.append(
        h(
          'div',
          { class: r.enabled ? 'rule' : 'rule off' },
          h('input', { type: 'checkbox', checked: r.enabled, onchange: () => { r.enabled = !r.enabled; changed(); this.rerender(); } }),
          color,
          h('span', { class: 'txt', title: r.text, style: `color:${r.color}` }, r.regex ? `/${r.text}/` : r.text),
          h('button', { class: r.row ? 'mini on' : 'mini', title: 'Color the whole row', onclick: () => { r.row = !r.row; changed(); this.rerender(); } }, 'row'),
          h('button', { class: 'mini danger', title: 'Remove', onclick: () => { s.highlights = list.filter((x) => x !== r); changed(); this.rerender(); } }, '×'),
        ),
      );
    }
    const text = h('input', { type: 'text', placeholder: 'word or regex to highlight', spellcheck: false });
    const color = h('input', { type: 'color', value: '#e8590c' });
    const regex = h('input', { type: 'checkbox' });
    const row = h('input', { type: 'checkbox' });
    const add = () => {
      if (!text.value) return;
      const rule: HighlightRule = { id: uid(), text: text.value, regex: regex.checked, caseSensitive: false, wholeWord: false, enabled: true, color: color.value, row: row.checked };
      const err = validatePattern(rule);
      if (err) return toast(err, true);
      list.push(rule);
      changed();
      this.rerender();
    };
    text.addEventListener('keydown', (e) => e.key === 'Enter' && add());
    sec.append(h('div', { class: 'form' }, h('div', { class: 'row' }, color, text), h('div', { class: 'row' }, h('label', {}, regex, 'Regex'), h('label', {}, row, 'Whole row'), h('span', { style: 'flex:1' }), h('button', { class: 'mini', onclick: add }, 'Add'))));
    return sec;
  }

  private lastTab: Tab | null = null;
  private rerender() {
    this.render(this.lastTab);
  }

  // ------------------------------------------------------------- bookmarks

  private bookmarksSection(tab: Tab) {
    const sec = h('div', { class: 'sec' }, h('h3', {}, `Bookmarks (${tab.bookmarks.size})`));
    if (!tab.bookmarks.size) sec.append(h('div', { class: 'empty-hint' }, 'Ctrl+B or double-click a row to bookmark it. F2 / Shift+F2 jump between bookmarks.'));
    const list = [...tab.bookmarks.values()].sort((a, b) => a.source - b.source || a.line - b.line);
    for (const b of list) {
      sec.append(
        h(
          'div',
          { class: 'bm', title: b.preview, onclick: () => this.host.jumpToBookmark(b) },
          h('span', { class: 'ln' }, `${tab.info.merged ? `${tab.info.sources[b.source]?.displayName ?? ''}:` : ''}${b.line}`),
          h('span', { class: 't' }, b.note ? h('span', { class: 'note' }, b.note) : b.preview),
          h('button', { class: 'mini', title: 'Edit note', onclick: (e: Event) => { e.stopPropagation(); this.host.bookmarkNote(b); } }, '✎'),
          h('button', { class: 'mini danger', title: 'Remove', onclick: (e: Event) => { e.stopPropagation(); this.host.removeBookmark(b); } }, '×'),
        ),
      );
    }
    return sec;
  }
}
