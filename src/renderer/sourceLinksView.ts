// Open source location: references such as Foo.java:42, File.cs:line 42, at x (file.ts:10:5) and Python's
// File "x.py", line 3 in the details pane become links that open in the configured editor, and the dialog that sets the
// editor and the source roots (Edit › Editor and source roots…).

import { EDITORS, findSourceRefs, NO_MATCH_HINT, SourceRef } from '../core/sourceLinks';
import type { Settings } from './state';
import { h, modal, toast } from './util';

/**
 * Wraps the source locations in the text of `root` in links (one text node at a time; folded-frame placeholders are
 * left alone). Returns how many links were made.
 */
export function linkifySources(root: HTMLElement): number {
  const texts: Text[] = [];
  const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, {
    acceptNode: (n) => (n.parentElement?.closest('.fold, .src-link') ? NodeFilter.FILTER_REJECT : NodeFilter.FILTER_ACCEPT),
  });
  for (let n = walker.nextNode() as Text | null; n; n = walker.nextNode() as Text | null) texts.push(n);
  let made = 0;
  for (const node of texts) {
    const refs = findSourceRefs(node.data);
    if (!refs.length) continue;
    const frag = document.createDocumentFragment();
    let at = 0;
    for (const r of refs) {
      frag.append(node.data.slice(at, r.start));
      const a = h('span', { class: 'src-link', title: `Open ${r.path}:${r.line} in your editor` }, node.data.slice(r.start, r.start + r.length));
      a.dataset.ref = JSON.stringify(r);
      frag.append(a);
      at = r.start + r.length;
      made++;
    }
    frag.append(node.data.slice(at));
    node.replaceWith(frag);
  }
  return made;
}

/** The location a link made by linkifySources stands for. */
export function refOfLink(el: HTMLElement): SourceRef | null {
  try {
    return el.dataset.ref ? (JSON.parse(el.dataset.ref) as SourceRef) : null;
  } catch {
    return null;
  }
}

/** Opens a location in the editor; says how to set the source roots when no root has the file. */
export async function openSourceRef(ref: SourceRef, settings: Settings) {
  const name = `${ref.path.replace(/\\/g, '/').split('/').pop()}:${ref.line}`;
  const roots = settings.sourceRoots ?? [];
  let r: { ok: boolean; error?: string };
  try {
    r = await window.ulv.openSource(ref, { roots, editor: settings.editor ?? 'vscode', command: settings.editorCommand ?? '' });
  } catch (e) {
    return toast(`Could not start the editor: ${(e as Error).message}`, true);
  }
  if (r.ok) return toast(`Opening ${name}`);
  if (r.error) return toast(r.error, true);
  toast(roots.length ? `${name} was not found under your source roots. ${NO_MATCH_HINT}` : `${name} is not on this computer as written. ${NO_MATCH_HINT}`, true);
}

/** Edit › Editor and source roots…: the editor preset or custom command, and the folders relative paths resolve under. */
export function editSourceSettings(settings: Settings, save: () => void) {
  modal((box, close) => {
    const editor = h('select', {}) as HTMLSelectElement;
    for (const e of EDITORS) editor.append(h('option', { value: e.id }, e.label));
    editor.value = settings.editor ?? 'vscode';
    const command = h('input', { type: 'text', placeholder: 'e.g. code -g "{path}:{line}:{col}"', value: settings.editorCommand ?? '' }) as HTMLInputElement;
    const cmdRow = h('label', {}, 'Command ', command);
    const sync = () => (cmdRow.style.display = editor.value === 'custom' ? '' : 'none');
    editor.addEventListener('change', sync);
    sync();
    const roots = h('textarea', { class: 'src-roots', placeholder: 'One folder per line, e.g.\n/home/me/src/shop\nC:\\work\\shop' }) as HTMLTextAreaElement;
    roots.value = (settings.sourceRoots ?? []).join('\n');
    const add = h('button', { class: 'btn' }, 'Add folder…');
    add.addEventListener('click', async () => {
      const dir = await window.ulv.folderDialog();
      if (dir) roots.value = [...roots.value.split('\n').filter((l) => l.trim()), dir].join('\n');
    });
    const ok = h('button', { class: 'btn primary' }, 'Save');
    ok.addEventListener('click', () => {
      settings.editor = editor.value;
      settings.editorCommand = command.value.trim();
      settings.sourceRoots = roots.value.split('\n').map((l) => l.trim()).filter(Boolean);
      save();
      close();
      toast('Source links will open in ' + (EDITORS.find((e) => e.id === editor.value)?.label ?? 'your editor'));
    });
    box.append(
      h('h2', {}, 'Editor and source roots'),
      h(
        'div',
        { class: 'form' },
        h('label', {}, 'Open source links in ', editor),
        cmdRow,
        h('div', { class: 'muted' }, 'A command can use {path}, {line} and {col}.'),
        h('div', { style: 'margin-top:8px' }, 'Source roots: relative paths in stack traces (Foo.java:42, src/app.ts:10) are looked up under these folders, first match wins.'),
        roots,
        h('div', { class: 'row' }, add),
      ),
      h('div', { class: 'actions' }, h('button', { class: 'btn', onclick: close }, 'Cancel'), ok),
    );
  });
}
