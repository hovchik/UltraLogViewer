// Dropdown of suggestions under a text box (used by the quick filter). The caller decides what to
// suggest and what accepting an item does; this class only handles showing, moving and picking.

import { h } from './util';

export interface Suggestion {
  label: string;
  /** Shown dimmed on the right, e.g. a count. */
  hint?: string;
  accept(): void;
}

export class Autocomplete {
  private items: Suggestion[] = [];
  private index = -1;

  constructor(private el: HTMLElement, private provide: () => Suggestion[]) {
    // Clicking an item must not blur the input.
    el.addEventListener('mousedown', (e) => e.preventDefault());
  }

  get open() {
    return !this.el.classList.contains('hidden');
  }

  /** Recomputes the suggestions and shows them (or hides the dropdown when there are none). */
  update() {
    this.items = this.provide();
    this.index = -1;
    this.render();
  }

  close() {
    this.items = [];
    this.index = -1;
    this.el.classList.add('hidden');
  }

  move(dir: 1 | -1) {
    if (!this.items.length) return;
    this.index = this.index < 0 ? (dir > 0 ? 0 : this.items.length - 1) : (this.index + dir + this.items.length) % this.items.length;
    this.render();
    (this.el.children[this.index] as HTMLElement | undefined)?.scrollIntoView({ block: 'nearest' });
  }

  /** Accepts the highlighted item; false when nothing is highlighted. */
  acceptCurrent(): boolean {
    const it = this.items[this.index];
    if (!it) return false;
    it.accept();
    return true;
  }

  private render() {
    this.el.innerHTML = '';
    if (!this.items.length) {
      this.el.classList.add('hidden');
      return;
    }
    this.items.forEach((it, i) => {
      this.el.append(
        h(
          'div',
          {
            class: i === this.index ? 'ac-item hot' : 'ac-item',
            onclick: () => it.accept(),
            onmousemove: () => {
              if (this.index === i) return;
              this.index = i;
              for (const [k, c] of [...this.el.children].entries()) c.classList.toggle('hot', k === i);
            },
          },
          h('span', { class: 'lbl' }, it.label),
          it.hint ? h('span', { class: 'n' }, it.hint) : null,
        ),
      );
    });
    this.el.classList.remove('hidden');
  }
}
