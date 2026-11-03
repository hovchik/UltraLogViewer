// AI requests from the main process: the API keys live here (Electron safeStorage, outside settings.json), and the
// renderer only ever sends the prompt the user approved in the consent preview. Nothing is sent while AI is off.

import { ipcMain, safeStorage } from 'electron';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { AiError, aiConfig, AiProvider, AiSettings, sendAi } from '../core/aiClient';

const keyFile = (dir: string, provider: AiProvider) => path.join(dir, `ai-key-${provider === 'openai' ? 'openai' : 'anthropic'}.bin`);

/** safeStorage is real encryption except on Linux without a keyring, where Electron falls back to a fixed key. */
function encrypted(): boolean {
  if (!safeStorage.isEncryptionAvailable()) return false;
  return process.platform !== 'linux' || safeStorage.getSelectedStorageBackend() !== 'basic_text';
}

export function protectionText(): string {
  return encrypted()
    ? 'Saved encrypted with the system keychain (Electron safeStorage), outside the settings file.'
    : 'Saved in a file only your user account can read (not encrypted: no system keychain is available), outside the settings file.';
}

function loadKey(dir: string, provider: AiProvider): string | null {
  try {
    const buf = fs.readFileSync(keyFile(dir, provider));
    // First byte: 1 = safeStorage, 0 = plain (owner-only file).
    return buf[0] === 1 ? safeStorage.decryptString(buf.subarray(1)) : buf.subarray(1).toString('utf-8');
  } catch {
    return null;
  }
}

function saveKey(dir: string, provider: AiProvider, key: string) {
  fs.mkdirSync(dir, { recursive: true });
  const file = keyFile(dir, provider);
  const data = encrypted() ? Buffer.concat([Buffer.from([1]), safeStorage.encryptString(key.trim())]) : Buffer.concat([Buffer.from([0]), Buffer.from(key.trim(), 'utf-8')]);
  fs.rmSync(file, { force: true });
  // Created owner-only (ignored on Windows, where the user profile is private and the key is encrypted).
  fs.writeFileSync(file, data, { mode: 0o600, flag: 'wx' });
}

const asProvider = (p: unknown): AiProvider => (p === 'openai' ? 'openai' : 'anthropic');

export function registerAi(userData: () => string, readAiSettings: () => AiSettings | undefined) {
  const running = new Map<number, AbortController>();
  ipcMain.handle('ai:keys', () => ({
    anthropic: fs.existsSync(keyFile(userData(), 'anthropic')),
    openai: fs.existsSync(keyFile(userData(), 'openai')),
    protection: protectionText(),
  }));
  ipcMain.handle('ai:set-key', (_e, provider: unknown, key: unknown) => {
    if (typeof key === 'string' && key.trim()) saveKey(userData(), asProvider(provider), key);
    else fs.rmSync(keyFile(userData(), asProvider(provider)), { force: true });
  });
  ipcMain.handle('ai:send', async (_e, id: unknown, prompt: { system?: unknown; user?: unknown }) => {
    const s = readAiSettings();
    // The main process enforces the opt-in too: a renderer bug cannot send anything while AI is off.
    if (!s?.enabled) return { ok: false, error: 'AI is off. Turn it on under AI › AI settings…' };
    const config = aiConfig(s, loadKey(userData(), s.provider === 'openai' ? 'openai' : 'anthropic'));
    if (config.provider === 'anthropic' && !config.key) return { ok: false, error: 'Add your Claude API key under AI › AI settings…' };
    const ctl = new AbortController();
    running.set(Number(id), ctl);
    try {
      const text = await sendAi(config, { system: String(prompt?.system ?? ''), user: String(prompt?.user ?? '') }, { signal: ctl.signal });
      return { ok: true, text };
    } catch (e) {
      if ((e as Error).name === 'AbortError') return { ok: false, cancelled: true, error: 'AI request cancelled' };
      return { ok: false, error: e instanceof AiError ? e.message : `AI request failed: ${(e as Error).message}` };
    } finally {
      running.delete(Number(id));
    }
  });
  ipcMain.handle('ai:abort', (_e, id: unknown) => running.get(Number(id))?.abort());
}
