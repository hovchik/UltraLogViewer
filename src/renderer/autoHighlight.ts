// Auto-highlight: selecting text in the details pane, or double-clicking a word in the grid, marks every occurrence of
// it on screen (the visible grid rows and the details pane) until the selection clears. Only what is in the DOM is
// searched, so it costs the same on a ten-row file and a ten-gigabyte one. Drawn with the CSS Custom Highlight API,
// so the rows' markup is never touched.

/** Occurrences marked at most (a screen never needs more). */
const MAX_RANGES = 2000;

/** The text to highlight for a selection: 2 to 200 characters on one line, else null. */
export function highlightTerm(sel: string): string | null {
  const t = sel.trim();
  return t.length >= 2 && t.length <= 200 && !t.includes('\n') ? t : null;
}

/** [start, end) of every case-insensitive occurrence of `term` in `text`, without overlaps. */
export function occurrences(text: string, term: string, limit = MAX_RANGES): [number, number][] {
  const out: [number, number][] = [];
  if (!term) return out;
  const hay = text.toLowerCase();
  const needle = term.toLowerCase();
  // lowercasing can change lengths (rare letters); fall back to exact matching then
  const same = hay.length === text.length && needle.length === term.length;
  const h = same ? hay : text;
  const n = same ? needle : term;
  for (let i = h.indexOf(n); i >= 0 && out.length < limit; i = h.indexOf(n, i + n.length)) out.push([i, i + n.length]);
  return out;
}

export class AutoHighlight {
  private term: string | null = null;
  /** Set by a double-click in the grid: a single click there clears it (a detail-pane selection clears itself). */
  private byGrid = false;
  private pending = false;

  /** `grid`: the rows container; `detail`: the details pane, whose text selection drives the highlight. */
  constructor(private grid: HTMLElement, private detail: HTMLElement) {
    const watch = new MutationObserver(() => this.schedule());
    watch.observe(grid, { childList: true, subtree: true });
    watch.observe(detail, { childList: true, subtree: true });
    document.addEventListener('selectionchange', () => this.onSelection());
    grid.addEventListener('mousedown', (e) => {
      if (e.detail === 1 && this.byGrid) this.set(null);
    });
  }

  get current(): string | null {
    return this.term;
  }

  /** Highlights `term` everywhere on screen (null clears). */
  set(term: string | null, byGrid = false) {
    this.term = term;
    this.byGrid = !!term && byGrid;
    this.apply();
  }

  private onSelection() {
    const sel = document.getSelection();
    const inDetail = !!sel && sel.rangeCount > 0 && this.detail.contains(sel.anchorNode) && this.detail.contains(sel.focusNode);
    if (inDetail && !sel!.isCollapsed) {
      const t = highlightTerm(sel!.toString());
      if (t !== this.term) this.set(t);
    } else if (!this.byGrid && this.term) this.set(null);
  }

  private schedule() {
    if (this.pending || !this.term) return;
    this.pending = true;
    requestAnimationFrame(() => {
      this.pending = false;
      this.apply();
    });
  }

  private apply() {
    if (typeof CSS === 'undefined' || !CSS.highlights) return;
    const term = this.term;
    if (!term) {
      CSS.highlights.delete('auto-hl');
      return;
    }
    const ranges: Range[] = [];
    // each cell, expanded line, field value and the entry text are searched as a whole, across their inline markup
    const blocks = [...this.grid.querySelectorAll('.c, .rl'), ...this.detail.querySelectorAll('pre, td')];
    for (const block of blocks) {
      if (ranges.length >= MAX_RANGES) break;
      const nodes: { node: Text; start: number }[] = [];
      let text = '';
      const walker = document.createTreeWalker(block, NodeFilter.SHOW_TEXT);
      for (let n = walker.nextNode() as Text | null; n; n = walker.nextNode() as Text | null) {
        nodes.push({ node: n, start: text.length });
        text += n.data;
      }
      if (text.length < term.length) continue;
      const locate = (pos: number, end: boolean): [Text, number] => {
        for (let i = nodes.length - 1; i >= 0; i--) {
          const { node, start } = nodes[i];
          if (end ? pos > start : pos >= start) return [node, Math.min(node.data.length, pos - start)];
        }
        return [nodes[0].node, 0];
      };
      for (const [s, e] of occurrences(text, term, MAX_RANGES - ranges.length)) {
        const r = document.createRange();
        r.setStart(...locate(s, false));
        r.setEnd(...locate(e, true));
        ranges.push(r);
      }
    }
    CSS.highlights.set('auto-hl', new Highlight(...ranges));
  }
}
