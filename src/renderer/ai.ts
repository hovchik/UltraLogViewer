// AI in the renderer: the settings dialog, the consent preview shown before every request, the review of a proposed
// filter and the answer pane in the details pane. Requests themselves go through the main process (window.ulv.aiSend),
// which holds the keys and refuses while AI is off.

import { aiHost, AiSettings, DEFAULT_ANTHROPIC_MODEL, DEFAULT_OPENAI_MODEL, DEFAULT_OPENAI_URL } from '../core/aiClient';
import { AiPrompt, explainEntryPrompt, explainPatternPrompt, extractPrompt, filterPrompt, parseExtract, parseFilter, plainAnswer } from '../core/aiPrompts';
import { guessRegex } from '../core/aiRegexGuess';
import { LEVEL_NAMES, PatternStat, RowData } from '../core/types';
import type { Tab } from './state';
import { redact, REDACTION_SUMMARY } from '../core/aiRedaction';
import { h, MenuItem, modal, toast } from './util';

/** "? errors from the db logger": a quick-filter question for the AI. */
export const isAiQuestion = (text: string) => text.trimStart().startsWith('?');

/** AI › AI settings…: provider, model, endpoint and keys. Resolves true when saved. */
export function aiSettingsDialog(s: AiSettings, save: (s: AiSettings) => Promise<void>): Promise<boolean> {
  return new Promise((resolve) => {
    let done = false;
    void window.ulv.aiKeys().then((keys) =>
      modal(
        (box, close) => {
          const enabled = h('input', { type: 'checkbox', id: 'ai-enabled' }) as HTMLInputElement;
          enabled.checked = !!s.enabled;
          const anthropic = h('input', { type: 'radio', name: 'ai-provider', value: 'anthropic' }) as HTMLInputElement;
          const openai = h('input', { type: 'radio', name: 'ai-provider', value: 'openai' }) as HTMLInputElement;
          anthropic.checked = s.provider !== 'openai';
          openai.checked = s.provider === 'openai';
          const keyBox = (has: boolean, hint: string) => h('input', { type: 'password', placeholder: has ? 'A key is saved; type to replace it' : hint, autocomplete: 'off' }) as HTMLInputElement;
          const forget = (has: boolean) => {
            const c = h('input', { type: 'checkbox' }) as HTMLInputElement;
            return { c, el: has ? h('label', { class: 'row' }, c, ' Forget the saved key') : h('span') };
          };
          const aKey = keyBox(keys.anthropic, 'sk-ant-…');
          const aForget = forget(keys.anthropic);
          const aModel = h('input', { type: 'text', value: s.anthropicModel ?? DEFAULT_ANTHROPIC_MODEL, placeholder: DEFAULT_ANTHROPIC_MODEL }) as HTMLInputElement;
          const oUrl = h('input', { type: 'text', value: s.openaiBaseUrl ?? DEFAULT_OPENAI_URL, placeholder: DEFAULT_OPENAI_URL }) as HTMLInputElement;
          const oKey = keyBox(keys.openai, 'Optional');
          const oForget = forget(keys.openai);
          const oModel = h('input', { type: 'text', value: s.openaiModel ?? DEFAULT_OPENAI_MODEL, placeholder: DEFAULT_OPENAI_MODEL }) as HTMLInputElement;
          const aPanel = h('div', { class: 'form' }, h('label', {}, 'API key'), aKey, aForget.el, h('label', {}, 'Model'), aModel);
          const oPanel = h('div', { class: 'form' }, h('label', {}, 'Base URL'), oUrl, h('label', {}, 'API key'), oKey, oForget.el, h('label', {}, 'Model'), oModel);
          const sync = () => {
            aPanel.style.display = anthropic.checked ? '' : 'none';
            oPanel.style.display = openai.checked ? '' : 'none';
          };
          anthropic.addEventListener('change', sync);
          openai.addEventListener('change', sync);
          sync();
          const error = h('div', { class: 'err-text' });
          const ok = h('button', { class: 'btn primary' }, 'Save');
          ok.addEventListener('click', async () => {
            const url = oUrl.value.trim();
            if (openai.checked && !/^https?:\/\/[^\s/]+/i.test(url)) return void (error.textContent = 'Enter a base URL such as http://localhost:11434/v1');
            s.enabled = enabled.checked;
            s.provider = openai.checked ? 'openai' : 'anthropic';
            s.anthropicModel = aModel.value.trim() || DEFAULT_ANTHROPIC_MODEL;
            s.openaiBaseUrl = url || DEFAULT_OPENAI_URL;
            s.openaiModel = oModel.value.trim() || DEFAULT_OPENAI_MODEL;
            for (const [p, box, f] of [['anthropic', aKey, aForget.c], ['openai', oKey, oForget.c]] as const) {
              if (box.value.trim()) await window.ulv.aiSetKey(p, box.value);
              else if (f.checked) await window.ulv.aiSetKey(p, '');
            }
            await save(s);
            done = true;
            resolve(true);
            close();
          });
          box.classList.add('ai-settings');
          box.append(
            h('h2', {}, 'AI settings'),
            h('label', { class: 'row' }, enabled, ' Turn on AI features'),
            h('div', { class: 'hint' }, `Off by default. When on, the app asks before every request and shows exactly the text that will be sent, after masking ${REDACTION_SUMMARY}. Extract fields… works without AI too.`),
            h('div', { class: 'ai-sub' }, 'Provider'),
            h('label', { class: 'row' }, anthropic, ' Claude API (Anthropic)'),
            h('label', { class: 'row' }, openai, ' OpenAI-compatible endpoint (local: Ollama, LM Studio, llama.cpp, …)'),
            aPanel,
            oPanel,
            h('div', { class: 'hint' }, `${keys.protection} Keys are never written to settings or workspace files.`),
            error,
            h('div', { class: 'actions' }, h('button', { class: 'btn', onclick: close }, 'Cancel'), ok),
          );
        },
        () => !done && resolve(false),
      ),
    );
  });
}

/**
 * The consent preview: the fixed instructions (read only) and the masked, editable request text, exactly as they will
 * be sent. Resolves null on Cancel (and then nothing is sent).
 */
export function aiConsent(destination: string, system: string, user: string, masked: number): Promise<{ user: string; dontAsk: boolean } | null> {
  return new Promise((resolve) => {
    let done = false;
    modal(
      (box, close) => {
        const instr = h('textarea', { class: 'ai-instructions', readonly: true, rows: 5 }) as HTMLTextAreaElement;
        instr.value = system;
        const text = h('textarea', { class: 'ai-consent-text', rows: 14, spellcheck: false }) as HTMLTextAreaElement;
        text.value = user;
        const dontAsk = h('input', { type: 'checkbox' }) as HTMLInputElement;
        const send = h('button', { class: 'btn primary ai-send' }, 'Send');
        send.addEventListener('click', () => {
          done = true;
          resolve({ user: text.value, dontAsk: dontAsk.checked });
          close();
        });
        box.classList.add('ai-consent');
        box.append(
          h('h2', {}, 'Send to AI?'),
          h('div', {}, `This will be sent to ${destination}:`),
          h('div', { class: 'ai-sub' }, 'Instructions'),
          instr,
          h('div', { class: 'ai-sub' }, 'Your data (you can edit it)'),
          text,
          h('div', { class: 'hint' }, masked ? `${masked} value${masked === 1 ? ' was' : 's were'} masked (${REDACTION_SUMMARY}).` : `Nothing needed masking. Masked automatically: ${REDACTION_SUMMARY}.`),
          h('label', { class: 'row' }, dontAsk, " Don't ask again for this session (text is still masked automatically)"),
          h('div', { class: 'actions' }, h('button', { class: 'btn ai-cancel', onclick: close }, 'Cancel'), send),
        );
        setTimeout(() => send.focus());
      },
      () => !done && resolve(null),
    );
  });
}

/** Shows a proposed quick-filter query for review. Resolves the (possibly edited) query to run, or null. */
export function reviewFilter(question: string, query: string, explanation: string, validate: (q: string) => string | null): Promise<string | null> {
  return new Promise((resolve) => {
    let done = false;
    modal(
      (box, close) => {
        const input = h('input', { type: 'text', class: 'ai-query', spellcheck: false }) as HTMLInputElement;
        input.value = query;
        const error = h('div', { class: 'err-text' });
        const check = () => {
          const e = validate(input.value);
          error.textContent = e ? `Not a valid query: ${e}` : '';
          return !e;
        };
        input.addEventListener('input', check);
        const apply = () => {
          if (!check()) return;
          done = true;
          resolve(input.value);
          close();
        };
        input.addEventListener('keydown', (e) => e.key === 'Enter' && apply());
        box.append(
          h('h2', {}, 'AI filter'),
          h('div', { class: 'muted' }, `“${question}”`),
          h('div', { class: 'ai-sub' }, 'Proposed quick filter'),
          input,
          h('div', { style: 'margin-top:6px' }, explanation),
          error,
          h('div', { class: 'hint' }, 'Nothing runs until you apply it. You can edit the query first.'),
          h('div', { class: 'actions' }, h('button', { class: 'btn', onclick: close }, 'Cancel'), h('button', { class: 'btn primary ai-apply', onclick: apply }, 'Apply filter')),
        );
        check();
      },
      () => !done && resolve(null),
    );
  });
}

/** The answer pane at the top of the details pane: title, Copy / Cancel / close, a spinner and the answer text. */
export class AiPane {
  readonly el = h('div', { class: 'ai-pane hidden' });
  private title = h('span', { class: 'ai-title' });
  private spinner = h('div', { class: 'ai-spinner' });
  private text = h('div', { class: 'ai-text' });
  private copy = h('button', { class: 'mini' }, 'Copy');
  private cancel = h('button', { class: 'mini ai-cancel-run' }, 'Cancel');
  private onCancel: (() => void) | null = null;

  constructor() {
    const close = h('button', { class: 'mini', title: 'Close the answer' }, '✕');
    this.copy.addEventListener('click', () => void navigator.clipboard.writeText(this.text.textContent ?? '').then(() => toast('Answer copied')));
    this.cancel.addEventListener('click', () => this.onCancel?.());
    close.addEventListener('click', () => {
      this.onCancel?.();
      this.hide();
    });
    this.el.append(h('div', { class: 'ai-head' }, this.title, h('span', { class: 'sp' }), this.copy, this.cancel, close), this.spinner, this.text);
  }

  busy(title: string, onCancel: () => void) {
    this.onCancel = onCancel;
    this.title.textContent = title;
    this.text.textContent = '';
    this.el.classList.remove('hidden');
    this.el.classList.add('running');
  }

  answer(title: string, text: string) {
    this.onCancel = null;
    this.title.textContent = title;
    // textContent: the answer is never interpreted as HTML.
    this.text.textContent = plainAnswer(text);
    this.el.classList.remove('hidden', 'running');
  }

  hide() {
    this.onCancel = null;
    this.el.classList.add('hidden');
    this.el.classList.remove('running');
  }

  get running() {
    return this.el.classList.contains('running') && !this.el.classList.contains('hidden');
  }
}

/** Sends prompts after consent, one running request per tab (cancelled when the tab closes). */
export class AiRequests {
  private noConsent = false;
  private nextId = 1;
  private running = new Map<unknown, number>();
  /** The request the busy pane belongs to. */
  private paneId = 0;

  constructor(
    private settings: () => AiSettings | undefined,
    readonly pane: AiPane,
  ) {}

  abort(owner: unknown) {
    const id = this.running.get(owner);
    if (id === undefined) return;
    this.running.delete(owner);
    void window.ulv.aiAbort(id);
  }

  /** Masks the prompt, asks for consent (unless skipped this session), sends it. Null when cancelled or failed (toasted). */
  async ask(owner: unknown, prompt: AiPrompt, busyTitle: string): Promise<string | null> {
    const s = this.settings();
    const masked = redact(prompt.user);
    let user = masked.text;
    if (!this.noConsent) {
      const host = aiHost({ provider: s?.provider ?? 'anthropic', baseUrl: s?.openaiBaseUrl ?? DEFAULT_OPENAI_URL });
      const dest = s?.provider === 'openai' ? `${host}, model ${s.openaiModel ?? DEFAULT_OPENAI_MODEL}` : `Anthropic (Claude API, ${host}), model ${s?.anthropicModel ?? DEFAULT_ANTHROPIC_MODEL}`;
      const ok = await aiConsent(dest, prompt.system, user, masked.count);
      if (!ok) return null;
      user = ok.user;
      this.noConsent = ok.dontAsk;
    }
    this.abort(owner);
    const id = this.nextId++;
    this.running.set(owner, id);
    this.paneId = id;
    this.pane.busy(busyTitle, () => this.abort(owner));
    try {
      const r = await window.ulv.aiSend(id, { system: prompt.system, user });
      if (r.ok && r.text) return r.text;
      toast(r.error ?? 'AI request failed', !r.cancelled);
      return null;
    } catch (e) {
      toast(`AI request failed: ${(e as Error).message}`, true);
      return null;
    } finally {
      if (this.running.get(owner) === id) this.running.delete(owner);
      if (this.paneId === id && this.pane.running) this.pane.hide();
    }
  }
}

/** What the AI features need from the app. */
export interface AiHost {
  settings(): AiSettings | undefined;
  /** Opens the AI settings; resolves whether AI is on afterwards. */
  openSettings(): Promise<boolean>;
  rows(t: Tab, start: number, count: number): Promise<RowData[]>;
  formatLabel(t: Tab): string;
  filtered(t: Tab): boolean;
  showDetail(): void;
  /** Opens the existing custom format editor, prefilled. */
  editFormat(sample: string[], name: string, pattern: string): void;
  validateQuery(t: Tab, q: string): string | null;
  applyQuery(t: Tab, q: string): void;
}

const firstLine = (s: string) => s.split('\n')[0].replace(/\r$/, '');

/** "Extract fields…" (with an offline guess), "Explain this entry / pattern" and a filter from a question. */
export class AiFeatures {
  readonly pane = new AiPane();
  readonly requests: AiRequests;

  constructor(private host: AiHost) {
    this.requests = new AiRequests(() => host.settings(), this.pane);
  }

  /** AI is on and configured; otherwise offers the settings. Nothing is sent when this resolves false. */
  private async ensure(): Promise<boolean> {
    if (!this.host.settings()?.enabled) {
      const open = await new Promise<boolean>((resolve) => {
        let done = false;
        modal(
          (box, close) => {
            const go = () => {
              done = true;
              resolve(true);
              close();
            };
            box.append(
              h('h2', {}, 'AI is off'),
              h('div', {}, 'AI features are off, so nothing is sent anywhere. Open the AI settings to turn them on?'),
              h('div', { class: 'actions' }, h('button', { class: 'btn', onclick: close }, 'Cancel'), h('button', { class: 'btn primary', onclick: go }, 'AI settings…')),
            );
          },
          () => !done && resolve(false),
        );
      });
      if (!open || !(await this.host.openSettings())) return false;
    }
    const s = this.host.settings();
    if (s?.provider !== 'openai' && !(await window.ulv.aiKeys()).anthropic) {
      toast('Add your Claude API key under AI › AI settings…', true);
      return false;
    }
    return true;
  }

  /** Row-menu items: "Extract fields…" for plain-text rows (also without AI), "Explain this entry". */
  rowItems(t: Tab, row: RowData): MenuItem[] {
    const items: MenuItem[] = [];
    if (t.info.sources[row.source]?.formatId === 'plain' || Object.keys(row.fields).every((k) => k === 'message'))
      items.push({ label: 'Extract fields…', action: () => void this.extract(t, row) });
    items.push({ label: 'Explain this entry', action: () => void this.explainEntry(t, row) });
    return items;
  }

  async extract(t: Tab, row: RowData) {
    const line = firstLine(row.text);
    const more = await this.host.rows(t, row.row + 1, 8);
    const sample = [line, ...more.map((r) => firstLine(r.text)).filter((l) => l.trim()).slice(0, 4)];
    let name = 'Extracted';
    let pattern: string;
    if (!this.host.settings()?.enabled) {
      pattern = guessRegex(line);
      toast("AI is off, so the fields are guessed from the line's tokens. Check them, then save.");
    } else {
      if (!(await this.ensure())) return;
      const answer = await this.requests.ask(t, extractPrompt(sample), 'Extracting fields…');
      if (answer === null) return;
      // Checked against the real line, not the masked one the model saw: this is the line the format must parse.
      const r = parseExtract(answer, line);
      if (r.ok) ({ name, pattern } = r.value);
      else {
        pattern = guessRegex(line);
        toast(`The AI suggestion was not usable (${r.error}), so this is a guess from the line's tokens`, true);
      }
    }
    this.host.editFormat(sample, name, pattern);
  }

  async explainEntry(t: Tab, row: RowData) {
    if (!(await this.ensure())) return;
    const start = Math.max(0, row.row - 20);
    const [before, after] = await Promise.all([this.host.rows(t, start, row.row - start), this.host.rows(t, row.row + 1, 20)]);
    const prompt = explainEntryPrompt(this.host.formatLabel(t), before.map((r) => r.text), row.text, after.map((r) => r.text), this.host.filtered(t));
    this.host.showDetail();
    const answer = await this.requests.ask(t, prompt, `Explaining line ${row.line}…`);
    if (answer !== null) this.pane.answer(`AI · line ${row.line}`, answer);
  }

  async explainPattern(t: Tab, p: PatternStat) {
    if (!(await this.ensure())) return;
    this.host.showDetail();
    const answer = await this.requests.ask(t, explainPatternPrompt(this.host.formatLabel(t), p.template, p.count, [p.sample]), 'Explaining the pattern…');
    if (answer !== null) this.pane.answer('AI · pattern', answer);
  }

  /** Enter on "? question" in the quick filter. */
  async askFilter(t: Tab, text: string) {
    const question = text.trimStart().slice(1).trim();
    if (!question) return toast('Type a question after the ?, e.g. ? errors from the db logger in the last hour');
    if (!(await this.ensure())) return;
    const fields = (t.fieldStats?.fields ?? []).map((f) => ({ key: f.key, top: f.top.map(([v]) => v) }));
    const levels = LEVEL_NAMES.map((level, i) => ({ level, count: t.info.levelCounts[i] ?? 0 })).filter((l, i) => i > 0 && l.count > 0);
    const prompt = filterPrompt(question, fields, levels, t.info.timeMin, t.info.timeMax, new Date());
    this.host.showDetail();
    const answer = await this.requests.ask(t, prompt, 'Writing a filter…');
    if (answer === null) return;
    const r = parseFilter(answer);
    if (!r.ok) return toast(`The AI answer was not usable: ${r.error}`, true);
    const q = await reviewFilter(question, r.value.query, r.value.explanation, (s) => this.host.validateQuery(t, s));
    if (q !== null) this.host.applyQuery(t, q);
  }
}
