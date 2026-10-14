// Command palette (Ctrl+Shift+P / F1): every menu action, searchable by its menu path.

import { acceleratorLabel, PaletteCommand, rankCommands } from './paletteRank';
import { h, modal } from './util';

export function openPalette(commands: PaletteCommand[], run: (c: PaletteCommand) => void, mac = false) {
  modal((box, close) => {
    box.classList.add('palette');
    const input = h('input', { type: 'text', placeholder: 'Type a command…', spellcheck: false, autocomplete: 'off' });
    const list = h('div', { class: 'pal-list', role: 'listbox' });
    let shown: PaletteCommand[] = [];
    let hot = 0;
    const choose = (c: PaletteCommand | undefined) => {
      if (!c) return;
      close();
      run(c);
    };
    const paint = () => {
      for (const [i, el] of [...list.children].entries()) el.classList.toggle('hot', i === hot);
      (list.children[hot] as HTMLElement | undefined)?.scrollIntoView({ block: 'nearest' });
    };
    const update = () => {
      shown = rankCommands(commands, input.value);
      hot = 0;
      list.innerHTML = '';
      for (const c of shown) {
        list.append(
          h(
            'div',
            { class: 'pal-item', role: 'option', onmousedown: (e: Event) => e.preventDefault(), onclick: () => choose(c) },
            h('span', { class: 'chk' }, c.checked ? '✓' : ''),
            h('span', { class: 'lbl' }, c.label),
            h('span', { class: 'key' }, acceleratorLabel(c.accelerator, mac)),
          ),
        );
      }
      if (!shown.length) list.append(h('div', { class: 'pal-empty' }, 'No matching commands'));
      paint();
    };
    input.addEventListener('input', update);
    input.addEventListener('keydown', (e) => {
      if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
        e.preventDefault();
        if (!shown.length) return;
        hot = (hot + (e.key === 'ArrowDown' ? 1 : -1) + shown.length) % shown.length;
        paint();
      } else if (e.key === 'Enter') {
        e.preventDefault();
        choose(shown[hot]);
      }
    });
    box.append(input, list);
    update();
  });
}
