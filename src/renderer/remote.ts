// Remote files over ssh in the window: "Open remote file…" (with a simple remote folder browser) and "Multi-host
// tail…", which opens one path or pattern on several hosts merged by time. The engine copies each remote file into a
// local file that the normal indexing and live tail read (core/remote.ts). Tabs keep the local copies' paths, while
// recent files and workspaces keep ssh:// URLs, which open the remote file again.

import { forHosts, joinRemote, looksRemote, parseRemote, remoteUrl, RemoteTarget, tryParseRemote } from '../core/remoteTarget';
import type { SourceInfo } from '../core/types';
import { engine } from './api';
import type { Settings } from './state';
import { h, modal, toast } from './util';

/** Remote paths as canonical ssh:// URLs (the form kept in recent files); other paths unchanged. */
export const normalizeRemote = (p: string) => (looksRemote(p) ? (tryParseRemote(p) ? remoteUrl(tryParseRemote(p)!) : p) : p);

/**
 * Replaces remote paths by their local copies (connecting to every host at once) and reports what failed. Null when
 * nothing could be opened.
 */
export async function localCopies(paths: string[], settings: Settings): Promise<string[] | null> {
  if (!paths.some(looksRemote)) return paths;
  const hosts = [...new Set(paths.map((p) => tryParseRemote(p)?.host).filter(Boolean))];
  if (hosts.length) toast(hosts.length === 1 ? `Connecting to ${hosts[0]}…` : `Connecting to ${hosts.length} hosts…`);
  const r = await engine.openRemote(paths, { ssh: settings.sshProgram });
  if (r.errors.length) toast(r.errors.slice(0, 4).join('\n') + (r.errors.length > 4 ? `\n… and ${r.errors.length - 4} more` : ''), true);
  return r.paths.length ? r.paths : null;
}

/** The ssh:// URL of a tab's path when it is the local copy of a remote file, else the path itself. */
export const sourceUrl = (sources: SourceInfo[], p: string) => sources.find((s) => s.path === p)?.remote?.url ?? p;

/** "Open remote file…": a URL or user@host:/path, with a Browse button that lists the remote folder. */
export function remoteDialog(settings: Settings, initial: string): Promise<string | null> {
  return new Promise((resolve) => {
    let done = false;
    modal(
      (box, close) => {
        const input = h('input', { type: 'text', spellcheck: false, class: 'mono', placeholder: 'ssh://user@host/var/log/app.log  or  user@host:/var/log/app.log' });
        input.value = initial;
        const list = h('div', { class: 'remote-list hidden' });
        const err = h('div', { class: 'err-text' });
        const finish = (url: string) => {
          done = true;
          resolve(url);
          close();
        };
        const browse = async (dir: RemoteTarget) => {
          err.textContent = '';
          browseBtn.disabled = true;
          try {
            const entries = await engine.listRemote(remoteUrl(dir), settings.sshProgram);
            input.value = `${remoteUrl(dir).replace(/\/+$/, '')}/`;
            list.innerHTML = '';
            for (const name of dir.path === '/' ? entries : ['../', ...entries]) {
              const item = h('div', { class: name.endsWith('/') ? 'item dir' : 'item', title: name }, name);
              item.onclick = () => {
                for (const x of list.querySelectorAll('.item.on')) x.classList.remove('on');
                item.classList.add('on');
                if (!name.endsWith('/')) input.value = remoteUrl({ ...dir, path: joinRemote(dir.path, name) });
              };
              item.ondblclick = () => {
                const p = joinRemote(dir.path, name.replace(/\/$/, ''));
                if (name.endsWith('/')) void browse({ ...dir, path: p });
                else finish(remoteUrl({ ...dir, path: p }));
              };
              list.append(item);
            }
            list.classList.remove('hidden');
          } catch (e) {
            err.textContent = (e as Error).message.replace(/^Error invoking remote method 'engine': Error: /, '');
          } finally {
            browseBtn.disabled = false;
          }
        };
        const browseBtn = h(
          'button',
          {
            class: 'btn',
            onclick: () => {
              let text = input.value.trim();
              // "host" or "user@host" alone browses the root folder
              if (!text.includes('/') && !text.includes(':')) text = `ssh://${text}/`;
              let t: RemoteTarget;
              try {
                t = parseRemote(text);
              } catch (e) {
                err.textContent = (e as Error).message;
                return;
              }
              // a file browses its folder
              void browse(t.path.endsWith('/') ? t : { ...t, path: joinRemote(t.path, '..') });
            },
          },
          'Browse',
        );
        const ok = () => {
          try {
            const t = parseRemote(input.value);
            if (t.path.endsWith('/')) throw new Error('Pick a file: this is a folder');
            finish(remoteUrl(t));
          } catch (e) {
            err.textContent = (e as Error).message;
          }
        };
        input.addEventListener('keydown', (e) => {
          if ((e as KeyboardEvent).key === 'Enter') {
            e.preventDefault();
            ok();
          }
        });
        box.append(
          h('h2', {}, 'Open remote file'),
          h('div', { class: 'hint' }, 'Your ssh config, keys and agent are used. The file is copied and followed live.'),
          h('div', { class: 'remote-row' }, input, browseBtn),
          list,
          err,
          h('div', { class: 'actions' }, h('button', { class: 'btn', onclick: close }, 'Cancel'), h('button', { class: 'btn primary', onclick: ok }, 'Open')),
        );
        box.classList.add('remote-dialog');
        setTimeout(() => {
          input.focus();
          input.select();
        });
      },
      () => !done && resolve(null),
    );
  });
}

/** "Multi-host tail…": hosts and a path or pattern; resolves with the ssh:// URLs to open merged. */
export function multiHostDialog(settings: Settings): Promise<string[] | null> {
  return new Promise((resolve) => {
    let done = false;
    modal(
      (box, close) => {
        const hosts = h('input', { type: 'text', spellcheck: false, placeholder: 'web1, web2, deploy@web3:2222' });
        hosts.value = settings.multiHostHosts ?? '';
        const file = h('input', { type: 'text', spellcheck: false, class: 'mono', placeholder: '/var/log/app/*.log' });
        file.value = settings.multiHostPath ?? '';
        const err = h('div', { class: 'err-text' });
        const ok = () => {
          try {
            const urls = forHosts(hosts.value, file.value.trim()).map(remoteUrl);
            settings.multiHostHosts = hosts.value.trim();
            settings.multiHostPath = file.value.trim();
            done = true;
            resolve(urls);
            close();
          } catch (e) {
            err.textContent = (e as Error).message;
          }
        };
        for (const i of [hosts, file])
          i.addEventListener('keydown', (e) => {
            if ((e as KeyboardEvent).key === 'Enter') {
              e.preventDefault();
              ok();
            }
          });
        box.append(
          h('h2', {}, 'Multi-host tail'),
          h('div', { class: 'hint' }, 'Hosts (as in your ssh config; separate with commas or spaces):'),
          hosts,
          h('div', { class: 'hint', style: 'margin-top:8px' }, 'File on each host (* and ? match several files):'),
          file,
          h('div', { class: 'hint', style: 'margin-top:6px' }, 'Each file is copied and followed live over ssh, merged by time and labelled by host.'),
          err,
          h('div', { class: 'actions' }, h('button', { class: 'btn', onclick: close }, 'Cancel'), h('button', { class: 'btn primary', onclick: ok }, 'Open')),
        );
        setTimeout(() => hosts.focus());
      },
      () => !done && resolve(null),
    );
  });
}
