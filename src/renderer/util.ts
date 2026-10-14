export const $ = <T extends HTMLElement = HTMLElement>(sel: string, root: ParentNode = document) => root.querySelector(sel) as T;

export function esc(s: string): string {
  return s.replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]!);
}

export function h<K extends keyof HTMLElementTagNameMap>(tag: K, attrs: Record<string, unknown> = {}, ...children: (Node | string | null | undefined | false)[]): HTMLElementTagNameMap[K] {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs)) {
    if (v === undefined || v === null || v === false) continue;
    if (k.startsWith('on') && typeof v === 'function') el.addEventListener(k.slice(2).toLowerCase(), v as EventListener);
    else if (k === 'class') el.className = String(v);
    else if (k === 'style') el.setAttribute('style', String(v));
    else if (k in el && typeof v !== 'string') (el as unknown as Record<string, unknown>)[k] = v;
    else el.setAttribute(k, v === true ? '' : String(v));
  }
  for (const c of children) if (c !== null && c !== undefined && c !== false) el.append(c);
  return el;
}

export function debounce<A extends unknown[]>(fn: (...a: A) => void, ms: number) {
  let t: ReturnType<typeof setTimeout> | undefined;
  return (...a: A) => {
    clearTimeout(t);
    t = setTimeout(() => fn(...a), ms);
  };
}

export const fmtNum = (n: number) => n.toLocaleString('en-US');

export function fmtBytes(n: number): string {
  const u = ['B', 'KB', 'MB', 'GB', 'TB'];
  let i = 0;
  while (n >= 1024 && i < u.length - 1) {
    n /= 1024;
    i++;
  }
  return `${n.toFixed(i ? 1 : 0)} ${u[i]}`;
}

let toastTimer: ReturnType<typeof setTimeout> | undefined;
export function toast(msg: string, error = false) {
  const t = $('#toast');
  t.textContent = msg;
  t.className = error ? 'toast err' : 'toast';
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => t.classList.add('hidden'), error ? 5000 : 2200);
}

// ------------------------------------------------------------------- modals

export function modal(build: (box: HTMLElement, close: () => void) => void, onClose?: () => void) {
  const m = $('#modal');
  m.innerHTML = '';
  const box = h('div', { class: 'box' });
  const close = () => {
    m.classList.add('hidden');
    m.innerHTML = '';
    onClose?.();
  };
  m.onclick = (e) => e.target === m && close();
  box.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') {
      e.stopPropagation();
      close();
    }
  });
  build(box, close);
  m.append(box);
  m.classList.remove('hidden');
  (box.querySelector('input,textarea,button') as HTMLElement | null)?.focus();
}

/** A replacement for window.prompt (which Electron does not support). */
export function ask(title: string, opts: { value?: string; hint?: string; placeholder?: string; multiline?: boolean } = {}): Promise<string | null> {
  return new Promise((resolve) => {
    let done = false;
    modal(
      (box, close) => {
        const input = opts.multiline
          ? h('textarea', { rows: 6, placeholder: opts.placeholder ?? '' })
          : h('input', { type: 'text', placeholder: opts.placeholder ?? '', spellcheck: false });
        input.value = opts.value ?? '';
        const ok = () => {
          done = true;
          resolve(input.value);
          close();
        };
        input.addEventListener('keydown', (e) => {
          const ke = e as KeyboardEvent;
          if (ke.key === 'Enter' && (!opts.multiline || ke.ctrlKey || ke.metaKey)) {
            e.preventDefault();
            ok();
          }
        });
        box.append(h('h2', {}, title));
        if (opts.hint) box.append(h('div', { class: 'hint' }, opts.hint));
        box.append(
          input,
          h('div', { class: 'actions' }, h('button', { class: 'btn', onclick: close }, 'Cancel'), h('button', { class: 'btn primary', onclick: ok }, 'OK')),
        );
        setTimeout(() => {
          input.focus();
          input.select();
        });
      },
      () => !done && resolve(null),
    );
  });
}

// ------------------------------------------------------------ context menu

export type MenuItem =
  | 'sep'
  | { label: string; shortcut?: string; checked?: boolean; disabled?: boolean; action?: () => void; submenu?: MenuItem[] };

let closeOpenMenu: (() => void) | null = null;

export function closeContextMenu() {
  closeOpenMenu?.();
}

/** Drops leading, trailing and doubled separators. */
function tidy(items: MenuItem[]): MenuItem[] {
  const out: MenuItem[] = [];
  for (const it of items) {
    if (it === 'sep' && (!out.length || out[out.length - 1] === 'sep')) continue;
    out.push(it);
  }
  while (out[out.length - 1] === 'sep') out.pop();
  return out;
}

/** A small popup menu at (x, y). Closes on click outside, Esc, scroll or window blur. */
export function contextMenu(x: number, y: number, items: MenuItem[]) {
  closeContextMenu();
  const close = () => {
    root.remove();
    document.removeEventListener('mousedown', outside, true);
    window.removeEventListener('keydown', onKey, true);
    window.removeEventListener('blur', close);
    window.removeEventListener('resize', close);
    document.removeEventListener('wheel', close, true);
    if (closeOpenMenu === close) closeOpenMenu = null;
  };
  const build = (list: MenuItem[]): HTMLElement => {
    const el = h('div', { class: 'ctxmenu', role: 'menu' });
    // Keep keyboard focus where it was (usually the grid).
    el.addEventListener('mousedown', (e) => e.preventDefault());
    for (const it of tidy(list)) {
      if (it === 'sep') {
        el.append(h('div', { class: 'sep' }));
        continue;
      }
      const cls = ['item'];
      if (it.disabled) cls.push('disabled');
      if (it.submenu) cls.push('has-sub');
      const row = h(
        'div',
        { class: cls.join(' '), role: 'menuitem' },
        h('span', { class: 'chk' }, it.checked ? '✓' : ''),
        h('span', { class: 'lbl' }, it.label),
        h('span', { class: 'key' }, it.submenu ? '›' : it.shortcut ?? ''),
      );
      if (it.submenu && !it.disabled) {
        const sub = build(it.submenu);
        row.append(sub);
        row.addEventListener('mouseenter', () => {
          const r = row.getBoundingClientRect();
          row.classList.toggle('flip', r.right + sub.offsetWidth + 4 > window.innerWidth);
          sub.style.top = `${Math.min(-4, window.innerHeight - r.top - sub.offsetHeight - 4)}px`;
        });
      } else if (!it.disabled && it.action) {
        const action = it.action;
        row.addEventListener('click', (e) => {
          e.stopPropagation();
          close();
          action();
        });
      }
      el.append(row);
    }
    return el;
  };
  const root = build(items);
  root.classList.add('root');
  const outside = (e: MouseEvent) => {
    if (!root.contains(e.target as Node)) close();
  };
  const onKey = (e: KeyboardEvent) => {
    const rows = [...root.children].filter((c) => c.classList.contains('item') && !c.classList.contains('disabled')) as HTMLElement[];
    const cur = rows.findIndex((r) => r.classList.contains('hot'));
    const hot = (i: number) => rows.forEach((r, k) => r.classList.toggle('hot', k === i));
    if (e.key === 'Escape') close();
    else if (e.key === 'ArrowDown') hot(rows.length ? (cur + 1) % rows.length : -1);
    else if (e.key === 'ArrowUp') hot(rows.length ? (cur - 1 + rows.length) % rows.length : -1);
    else if (e.key === 'Enter' && cur >= 0) rows[cur].click();
    else return;
    e.preventDefault();
    e.stopPropagation();
  };
  document.body.append(root);
  root.style.left = `${Math.max(0, Math.min(x, window.innerWidth - root.offsetWidth - 4))}px`;
  root.style.top = `${Math.max(0, Math.min(y, window.innerHeight - root.offsetHeight - 4))}px`;
  document.addEventListener('mousedown', outside, true);
  window.addEventListener('keydown', onKey, true);
  window.addEventListener('blur', close);
  window.addEventListener('resize', close);
  document.addEventListener('wheel', close, true);
  closeOpenMenu = close;
}

// ------------------------------------------------------------ pretty print

function highlightJson(json: string): string {
  return esc(json).replace(/(&quot;(?:\\.|[^&\\]|&(?!quot;))*?&quot;)(\s*:)?|\b(true|false|null)\b|(-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)/g, (m, s, colon, b, n) => {
    if (s) return colon ? `<span class="json-k">${s}</span>${colon}` : `<span class="json-s">${s}</span>`;
    if (b) return `<span class="json-b">${b}</span>`;
    if (n) return `<span class="json-n">${n}</span>`;
    return m;
  });
}

function prettyXml(xml: string): string | null {
  if (!/^<[\w?!]/.test(xml) || !/>$/.test(xml)) return null;
  const tokens = xml.replace(/>\s*</g, '><').split(/(?=<)|(?<=>)/);
  let depth = 0;
  const out: string[] = [];
  for (const t of tokens) {
    if (!t.trim()) continue;
    if (/^<\//.test(t)) depth = Math.max(0, depth - 1);
    out.push('  '.repeat(depth) + t);
    if (/^<[^/!?][^>]*[^/]>$/.test(t) && !/^<[^>]+>[^<]+<\//.test(t)) depth++;
  }
  return out.length > 1 ? out.join('\n') : null;
}

/** Renders entry text as HTML, pretty-printing embedded JSON or XML. */
export function prettyEntry(text: string): string {
  const parts: string[] = [];
  let rest = text;
  for (let guard = 0; guard < 5; guard++) {
    const i = rest.search(/[{[]/);
    if (i < 0) break;
    const open = rest[i];
    const close = open === '{' ? '}' : ']';
    const j = rest.lastIndexOf(close);
    if (j <= i) break;
    const candidate = rest.slice(i, j + 1);
    try {
      const v = JSON.parse(candidate);
      if (typeof v !== 'object' || v === null) throw new Error();
      parts.push(esc(rest.slice(0, i)), '\n', highlightJson(JSON.stringify(v, null, 2)), '\n');
      rest = rest.slice(j + 1);
      continue;
    } catch {
      break;
    }
  }
  const xi = rest.indexOf('<');
  const xj = rest.lastIndexOf('>');
  if (xi >= 0 && xj > xi) {
    const pretty = prettyXml(rest.slice(xi, xj + 1));
    if (pretty) {
      parts.push(esc(rest.slice(0, xi)), '\n', esc(pretty), '\n', esc(rest.slice(xj + 1)));
      return parts.join('');
    }
  }
  parts.push(esc(rest));
  return parts.join('');
}
