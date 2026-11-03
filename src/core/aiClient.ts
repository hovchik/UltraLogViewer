// The two AI wire formats (Claude Messages API and OpenAI-compatible chat completions) over plain fetch, no SDK.
// Runs in the main process only: the renderer never sees the key. Keep in sync with dotnet/.../AiClient.cs.

import { cap, type AiPrompt } from './aiPrompts';
import { redact } from './aiRedaction';

/** "anthropic" (Claude API) or "openai" (any OpenAI-compatible endpoint, e.g. Ollama at http://localhost:11434/v1). */
export type AiProvider = 'anthropic' | 'openai';

/** Where AI requests go. `key` is never logged, saved in settings or put in an error message. */
export interface AiConfig {
  provider: AiProvider;
  model: string;
  baseUrl: string;
  key: string | null;
}

/** AI settings as stored in settings.json (no key). Off by default. */
export interface AiSettings {
  enabled?: boolean;
  provider?: AiProvider;
  anthropicModel?: string;
  openaiBaseUrl?: string;
  openaiModel?: string;
}

export const DEFAULT_ANTHROPIC_MODEL = 'claude-opus-5-5';
export const ANTHROPIC_URL = 'https://api.anthropic.com';
export const DEFAULT_OPENAI_URL = 'http://localhost:11434/v1';
export const DEFAULT_OPENAI_MODEL = 'llama3.1';
export const DEFAULT_TIMEOUT_MS = 90_000;
export const ANTHROPIC_VERSION = '2026-10-21';
export const FALLBACK_BETA = 'server-side-fallback-2026-07-01';

/** Settings with defaults filled in, and the key the main process loaded. */
export function aiConfig(s: AiSettings | undefined, key: string | null): AiConfig {
  return s?.provider === 'openai'
    ? { provider: 'openai', model: s.openaiModel || DEFAULT_OPENAI_MODEL, baseUrl: s.openaiBaseUrl || DEFAULT_OPENAI_URL, key }
    : { provider: 'anthropic', model: s?.anthropicModel || DEFAULT_ANTHROPIC_MODEL, baseUrl: ANTHROPIC_URL, key };
}

/** "api.anthropic.com" or the endpoint's host, for the consent dialog. */
export function aiHost(c: Pick<AiConfig, 'provider' | 'baseUrl'>): string {
  try {
    return new URL(c.provider === 'anthropic' ? ANTHROPIC_URL : c.baseUrl).host;
  } catch {
    return c.baseUrl;
  }
}

/** A failed AI request, with a message meant for a toast. */
export class AiError extends Error {
  constructor(
    message: string,
    readonly status = 0,
  ) {
    super(message);
  }
}

/** Models that take the server-side refusal fallback (`fallbacks: "default"`). */
export const usesFallback = (model: string) => ['claude-opus-5-5', 'claude-opus-5', 'claude-fable-5-1', 'claude-sonnet-5-5'].includes(model);

export function buildRequest(c: AiConfig, p: AiPrompt): { url: string; headers: Record<string, string>; body: string } {
  if (c.provider === 'anthropic') {
    const body: Record<string, unknown> = { model: c.model, max_tokens: 16000, system: p.system, messages: [{ role: 'user', content: p.user }] };
    const headers: Record<string, string> = { 'content-type': 'application/json', 'x-api-key': c.key ?? '', 'anthropic-version': ANTHROPIC_VERSION };
    if (usesFallback(c.model)) {
      // On a safety decline the API re-runs the request on a fallback model instead of just stopping.
      body.fallbacks = 'default';
      headers['anthropic-beta'] = FALLBACK_BETA;
    }
    return { url: `${ANTHROPIC_URL}/v1/messages`, headers, body: JSON.stringify(body) };
  }
  const headers: Record<string, string> = { 'content-type': 'application/json' };
  if (c.key) headers.authorization = `Bearer ${c.key}`;
  const body = { model: c.model, stream: false, messages: [{ role: 'system', content: p.system }, { role: 'user', content: p.user }] };
  return { url: `${c.baseUrl.replace(/\/+$/, '')}/chat/completions`, headers, body: JSON.stringify(body) };
}

export function statusMessage(status: number): string {
  if (status === 400) return 'The AI service rejected the request (400)';
  if (status === 401) return 'The API key was rejected (401). Check it under AI › AI settings…';
  if (status === 403) return 'The API key may not use this model (403)';
  if (status === 404) return 'Model or endpoint not found (404). Check the model name and base URL';
  if (status === 413) return 'The request is too large for the AI service (413)';
  if (status === 429) return 'Rate limited by the AI service (429). Try again in a moment';
  if (status === 529 || status === 503) return `The AI service is overloaded (${status}). Try again in a moment`;
  if (status >= 500) return `The AI service had an error (${status})`;
  return `The AI service answered with HTTP ${status}`;
}

/** The answer text of a response, or an AiError that says what went wrong. */
export function parseResponse(provider: AiProvider, status: number, body: string): string {
  let json: any = null; // untyped JSON from two different APIs
  try {
    json = JSON.parse(body);
  } catch {
    // reported below
  }
  if (status < 200 || status > 299) {
    const e = json?.error;
    const detail = e && typeof e === 'object' ? e.message : typeof e === 'string' ? e : null;
    // The server's message is masked too, in case it echoes a key or other data back.
    throw new AiError(statusMessage(status) + (typeof detail === 'string' && detail.trim() ? `: ${cap(redact(detail).text, 200)}` : ''), status);
  }
  if (json === null || typeof json !== 'object') throw new AiError('The AI service sent an answer that is not valid JSON', status);
  let text: string | null = null;
  if (provider === 'anthropic') {
    if (json.stop_reason === 'refusal') throw new AiError('The model declined to answer this request', status);
    // Only text blocks: thinking blocks (empty by default) are not part of the answer.
    if (Array.isArray(json.content)) text = json.content.filter((b: { type?: string }) => b?.type === 'text').map((b: { text?: string }) => b.text ?? '').join('');
  } else {
    const c = json.choices?.[0]?.message?.content;
    text = typeof c === 'string' ? c : null;
  }
  if (!text || !text.trim()) throw new AiError('The AI service sent an empty answer', status);
  return text.trim();
}

/** Node's fetch hides the socket error in `cause.code`. */
function networkMessage(e: unknown): string {
  const code = (e as { cause?: { code?: string } })?.cause?.code ?? '';
  if (code === 'ENOTFOUND' || code === 'EAI_AGAIN') return 'the name could not be resolved (offline?)';
  if (code === 'ECONNREFUSED' || code === 'ECONNRESET' || code === 'EHOSTUNREACH' || code === 'ENETUNREACH' || code === 'UND_ERR_SOCKET') return 'the connection failed';
  if (/CERT|SSL|TLS/i.test(code)) return 'the secure connection failed';
  return (e as { cause?: Error })?.cause?.message ?? (e as Error)?.message ?? String(e);
}

/**
 * Sends one prompt and returns the answer text. Throws AiError, or an AbortError (name "AbortError") when `signal`
 * fires; a timeout is an AiError.
 */
export async function sendAi(c: AiConfig, p: AiPrompt, opts: { timeoutMs?: number; signal?: AbortSignal; fetch?: typeof fetch } = {}): Promise<string> {
  const timeoutMs = opts.timeoutMs ?? DEFAULT_TIMEOUT_MS;
  const ctl = new AbortController();
  let timedOut = false;
  const timer = setTimeout(() => {
    timedOut = true;
    ctl.abort();
  }, timeoutMs);
  const onAbort = () => ctl.abort();
  if (opts.signal?.aborted) ctl.abort();
  opts.signal?.addEventListener('abort', onAbort);
  const req = buildRequest(c, p);
  try {
    const res = await (opts.fetch ?? fetch)(req.url, { method: 'POST', headers: req.headers, body: req.body, signal: ctl.signal });
    const body = await res.text();
    return parseResponse(c.provider, res.status, body);
  } catch (e) {
    if (e instanceof AiError) throw e;
    if (timedOut) throw new AiError(`No answer from ${aiHost(c)} after ${Math.round(timeoutMs / 1000)} s`);
    if (ctl.signal.aborted) {
      const err = new Error('AI request cancelled');
      err.name = 'AbortError';
      throw err;
    }
    // The message names the host and the socket error; it never contains the key (that is a header).
    throw new AiError(`Could not reach ${aiHost(c)}: ${networkMessage(e)}`);
  } finally {
    clearTimeout(timer);
    opts.signal?.removeEventListener('abort', onAbort);
  }
}
