// Command palette filtering and ranking (pure, so it can be unit tested).

/** One leaf menu action, as main reports it. */
export interface PaletteCommand {
  /** Menu item id, passed back to main to run it. */
  id: string;
  /** Path label, e.g. "View › Time column › Delta from previous row". */
  label: string;
  accelerator?: string;
  checked?: boolean;
  enabled: boolean;
}

const isWordChar = (c: string | undefined) => !!c && /[\p{L}\p{N}]/u.test(c);

/** True when `word` occurs in `label` at a word start. */
function atWordStart(label: string, word: string): boolean {
  for (let i = label.indexOf(word); i >= 0; i = label.indexOf(word, i + 1)) if (!isWordChar(label[i - 1])) return true;
  return false;
}

/**
 * Commands whose label contains every typed word (case-insensitive substrings), disabled ones hidden. Ranked:
 * label starts with the query, then every word at a word start, then the rest; menu order breaks ties.
 */
export function rankCommands<T extends PaletteCommand>(commands: T[], query: string): T[] {
  const q = query.trim().toLowerCase().replace(/\s+/g, ' ');
  const words = q ? q.split(' ') : [];
  const scored: { c: T; rank: number; i: number }[] = [];
  commands.forEach((c, i) => {
    if (!c.enabled) return;
    const label = c.label.toLowerCase();
    if (!words.every((w) => label.includes(w))) return;
    const rank = !q ? 0 : label.startsWith(q) ? 0 : words.every((w) => atWordStart(label, w)) ? 1 : 2;
    scored.push({ c, rank, i });
  });
  scored.sort((a, b) => a.rank - b.rank || a.i - b.i);
  return scored.map((s) => s.c);
}

/** Electron accelerator text for display: "CmdOrCtrl+Shift+P" → "Ctrl+Shift+P" (or ⌘ on macOS). */
export function acceleratorLabel(acc: string | undefined, mac = false): string {
  if (!acc) return '';
  return acc.replace(/CmdOrCtrl|CommandOrControl/g, mac ? '⌘' : 'Ctrl').replace(/\bCommand\b|\bCmd\b/g, '⌘');
}
