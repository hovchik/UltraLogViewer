// File › Listen… and File › Open Windows event log…: the dialogs, the live line at the top of the Sources list (what
// a capture listens on, its counts, Stop), and format detection once an empty capture gets its first lines. The
// receivers themselves run in the main process; a tab only holds its capture id.

import { COMMON_CHANNELS, isLoopback, parseBind, PROTOCOLS, ReceiverProtocol, validChannel } from '../core/receiverOptions';
import { parsePoint } from '../core/timeWindow';
import type { Settings, Tab } from './state';
import { h, modal, toast } from './util';

export interface ReceiverSettings {
  protocol?: ReceiverProtocol;
  bind?: string;
  port?: number;
  maxMessageKb?: number;
  /** Size at which a capture file is rotated, in MB. */
  capMb?: number;
}

export interface ReceiversHost {
  settings(): Settings;
  saveSettings(): void;
  tabs(): Tab[];
  /** Opens the capture file as a live, followed tab and activates it. */
  openCapture(path: string, format: string, capture: NonNullable<Tab['capture']>): Promise<Tab>;
  reformat(t: Tab): Promise<void>;
}

const ulv = () => window.ulv;

/** The status line and Stop button for a capture tab, at the top of the Sources list. */
export function captureBox(tab: Tab): HTMLElement {
  const c = tab.capture!;
  const stop = h('button', { class: 'mini capture-stop', disabled: !c.running }, 'Stop') as HTMLButtonElement;
  stop.addEventListener('click', async () => {
    await ulv().captureStop(c.id);
    const s = (await ulv().captureStatus([c.id]))[c.id];
    if (s) Object.assign(c, s);
    syncBoxes(tab);
    toast('Stopped: the capture stays open');
  });
  return h('div', { class: 'capture', 'data-capture': String(c.id) }, stop, h('div', { class: 'capture-status' }, c.status));
}

function syncBoxes(tab: Tab) {
  const c = tab.capture;
  if (!c) return;
  for (const el of document.querySelectorAll<HTMLElement>(`.capture[data-capture="${c.id}"]`)) {
    el.querySelector('.capture-status')!.textContent = c.status;
    (el.querySelector('.capture-stop') as HTMLButtonElement).disabled = !c.running;
  }
}

export class Receivers {
  private timer: ReturnType<typeof setInterval> | null = null;

  constructor(private host: ReceiversHost) {}

  /** Once a second while captures are open: their counts, and format detection once lines arrived. */
  private ensureTimer() {
    this.timer ??= setInterval(() => void this.tick(), 1000);
  }

  async tick() {
    const tabs = this.host.tabs().filter((t) => t.capture);
    if (!tabs.length) return;
    const st = await ulv().captureStatus(tabs.map((t) => t.capture!.id));
    for (const t of tabs) {
      const s = st[t.capture!.id];
      if (s) Object.assign(t.capture!, s);
      syncBoxes(t);
      if (t.detectOnData && !t.info.indexing && (t.info.sources[0]?.entries ?? 0) > 0) {
        t.detectOnData = false;
        // The file was empty when it was opened, so it was taken for plain text: look again now that there is data.
        if (!t.formatId && t.info.sources[0].formatId === 'plain') await this.host.reformat(t);
      }
    }
  }

  forget(t: Tab) {
    if (t.capture) void ulv().captureStop(t.capture.id, true);
  }

  /** Starts a receiver and opens its capture as a live tab. */
  async start(o: { protocol: ReceiverProtocol; bind: string; port: number; maxMessage: number; capMb: number }): Promise<Tab | null> {
    const r = await ulv().receiverStart(o);
    if (r.error || r.id === undefined) {
      toast(`Could not listen on ${o.protocol === 'syslog-udp' ? 'udp' : 'tcp'}://${o.bind}:${o.port}: ${r.error}`, true);
      return null;
    }
    // Syslog is always written as RFC 5424 and XML events as JSON; plain lines are detected once the first arrive.
    const format = o.protocol.startsWith('syslog') ? 'syslog5424' : o.protocol === 'log4j-xml' ? 'json' : '';
    const tab = await this.host.openCapture(r.path!, format, { id: r.id, status: `Listening on ${r.endpoint}\n0 messages`, running: true });
    tab.detectOnData = !format;
    this.ensureTimer();
    void this.tick();
    toast(`Listening on ${r.endpoint}`);
    return tab;
  }

  listen() {
    const s: ReceiverSettings = (this.host.settings().receiver ??= {});
    void ulv().receiversDir().then((dir) =>
      modal((box, close) => {
        const protocol = h('select', { id: 'receiver-protocol' }, ...PROTOCOLS.map(([v, label]) => h('option', { value: v }, label))) as HTMLSelectElement;
        protocol.value = s.protocol ?? 'syslog-udp';
        const input = (id: string, value: string) => h('input', { type: 'text', id, value, spellcheck: false }) as HTMLInputElement;
        const port = input('receiver-port', String(s.port ?? 5514));
        const bind = input('receiver-bind', s.bind ?? '127.0.0.1');
        const max = input('receiver-max', String(s.maxMessageKb ?? 64));
        const cap = input('receiver-cap', String(s.capMb ?? 1024));
        const warning = h('div', { id: 'receiver-warning', class: 'hint', style: 'color:#E8590C' }, '⚠ Not a loopback address: other computers can send to this port. Use it only on a network you trust, and check your firewall.');
        const sync = () => (warning.style.display = isLoopback(bind.value) ? 'none' : '');
        bind.addEventListener('input', sync);
        sync();
        const error = h('div', { class: 'err-text' });
        const ok = async () => {
          const p = Number(port.value);
          const m = Number(max.value);
          const c = Number(cap.value);
          const msg = !Number.isInteger(p) || p < 1 || p > 65535 ? 'Enter a port from 1 to 65535'
            : !parseBind(bind.value) ? 'Enter an IP address to listen on, such as 127.0.0.1 (this computer only) or 0.0.0.0 (all networks)'
            : !Number.isInteger(m) || m < 1 || m > 16384 ? 'Enter a maximum message size from 1 to 16384 KB'
            : !Number.isInteger(c) || c < 1 ? 'Enter the size at which to rotate the capture, in MB'
            : '';
          if (msg) return void (error.textContent = msg);
          const chosen = bind.value.trim();
          // a public bind is never remembered: it is chosen each time
          Object.assign(s, { protocol: protocol.value as ReceiverProtocol, port: p, bind: isLoopback(chosen) ? chosen : '127.0.0.1', maxMessageKb: m, capMb: c });
          this.host.saveSettings();
          close();
          await this.start({ protocol: s.protocol!, bind: chosen, port: p, maxMessage: m * 1024, capMb: c });
        };
        box.addEventListener('keydown', (e) => e.key === 'Enter' && (e.target as HTMLElement).tagName === 'INPUT' && void ok());
        box.classList.add('receiver-dialog');
        box.append(
          h('h2', {}, 'Listen for logs'),
          h('div', { class: 'form' },
            h('label', {}, 'Protocol'), protocol,
            h('label', {}, 'Port'), port,
            h('label', {}, 'Bind address'), bind,
            warning,
            h('label', {}, 'Max message (KB)'), max,
            h('label', {}, 'Rotate capture at (MB)'), cap,
          ),
          h('div', { class: 'hint' }, `What arrives is written to a capture file in ${dir} (readable only by you) and opened as a live tab. Closing the tab stops listening.`),
          error,
          h('div', { class: 'actions' }, h('button', { class: 'btn', onclick: close }, 'Cancel'), h('button', { class: 'btn primary', id: 'receiver-ok', onclick: () => void ok() }, 'Listen')),
        );
      }),
    );
  }

  openEventLog() {
    modal((box, close) => {
      const OTHER = 'Other channel…';
      const pick = h('select', {}, ...[...COMMON_CHANNELS, OTHER].map((c) => h('option', { value: c }, c))) as HTMLSelectElement;
      const other = h('input', { type: 'text', placeholder: 'Microsoft-Windows-PowerShell/Operational', disabled: true, spellcheck: false }) as HTMLInputElement;
      pick.addEventListener('change', () => (other.disabled = pick.value !== OTHER));
      const from = h('input', { type: 'text', value: '-1h', placeholder: '2024-05-01 10:00, 10:30, -2h; empty = everything', spellcheck: false }) as HTMLInputElement;
      const until = h('input', { type: 'text', placeholder: 'empty = now', spellcheck: false }) as HTMLInputElement;
      const follow = h('input', { type: 'checkbox', checked: true }) as HTMLInputElement;
      until.addEventListener('input', () => (follow.disabled = !!until.value.trim()));
      const error = h('div', { class: 'err-text' });
      const ok = async () => {
        const now = Date.now();
        const channel = pick.value === OTHER ? other.value.trim() : pick.value;
        const point = (b: HTMLInputElement) => (b.value.trim() ? parsePoint(b.value, now) : null);
        const msg = !validChannel(channel) ? 'Enter a channel name, such as Application or Microsoft-Windows-PowerShell/Operational'
          : from.value.trim() && point(from) === null ? 'From: enter a time such as 2024-05-01 10:00, 10:30 or -2h'
          : until.value.trim() && point(until) === null ? 'Until: enter a time such as 2024-05-01 11:00'
          : '';
        if (msg) return void (error.textContent = msg);
        close();
        await this.startEventLog(channel, point(from), point(until), follow.checked && !until.value.trim());
      };
      box.append(
        h('h2', {}, 'Open Windows event log'),
        h('div', { class: 'form' }, h('label', {}, 'Channel'), pick, other, h('label', {}, 'From'), from, h('label', {}, 'Until'), until),
        h('label', { class: 'row' }, follow, ' Keep following new events'),
        h('div', { class: 'hint' }, 'Events are exported with wevtutil to a capture file (one JSON line each) and opened as a tab. The Security log needs an administrator.'),
        error,
        h('div', { class: 'actions' }, h('button', { class: 'btn', onclick: close }, 'Cancel'), h('button', { class: 'btn primary', onclick: () => void ok() }, 'Open')),
      );
    });
  }

  async startEventLog(channel: string, from: number | null, until: number | null, follow: boolean): Promise<Tab | null> {
    const r = await ulv().eventLogStart(channel, from, until, follow, this.host.settings().receiver?.capMb ?? 1024);
    if (r.error || r.id === undefined) {
      toast(`Could not read the ${channel} log: ${r.error}`, true);
      return null;
    }
    const tab = await this.host.openCapture(r.path!, 'winevent', { id: r.id, status: `Windows event log ${channel}\nreading`, running: true });
    this.ensureTimer();
    const error = await ulv().eventLogFirst(r.id);
    if (error) toast(`Could not read the ${channel} log: ${error}`, true);
    await this.tick();
    return tab;
  }
}
