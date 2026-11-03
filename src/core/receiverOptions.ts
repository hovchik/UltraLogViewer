// Receiver choices and checks shared by the main process and the renderer (so no Node modules here).

export type ReceiverProtocol = 'syslog-udp' | 'syslog-tcp' | 'tcp-lines' | 'log4j-xml';

export const PROTOCOLS: [ReceiverProtocol, string][] = [
  ['syslog-udp', 'Syslog over UDP'],
  ['syslog-tcp', 'Syslog over TCP (octet counting or newline)'],
  ['tcp-lines', 'Text lines over TCP (Serilog, NLog, log4j2 JSON or pattern)'],
  ['log4j-xml', 'log4j / log4net XML over TCP'],
];

const V4 = /^(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(\.(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)){3}$/;
const V6 = /^(?=.*:)[0-9a-fA-F:.]{2,45}$/;

export const isIPv4 = (s: string) => V4.test(s);
export const isIPv6 = (s: string) => V6.test(s) && (s.match(/::/g)?.length ?? 0) <= 1 && !/:::/.test(s) && s.split(':').every((g) => g.length <= 4 || V4.test(g));

const unbracket = (bind: string) => bind.trim().replace(/^\[(.*)\]$/, '$1');

/** True for 127.0.0.0/8, ::1 and "localhost": the only binds that need no confirmation. */
export function isLoopback(bind: string): boolean {
  const b = unbracket(bind).toLowerCase();
  if (b === 'localhost') return true;
  if (isIPv4(b)) return b.startsWith('127.');
  if (isIPv6(b)) return /^[0:]*:0*1$/.test(b) || /^::ffff:127\./.test(b);
  return false;
}

/** An IP address to bind to, or null. Host names other than localhost are not accepted. */
export function parseBind(bind: string): string | null {
  const b = unbracket(bind);
  if (b.toLowerCase() === 'localhost') return '127.0.0.1';
  return isIPv4(b) || isIPv6(b) ? b : null;
}

export const COMMON_CHANNELS = ['Application', 'System', 'Security', 'Setup'];

/** A Windows event log channel name wevtutil will take as a channel and not as an option. */
export const validChannel = (c: string) => /^[A-Za-z0-9][\w .%/-]{0,254}$/.test(c) && !c.includes('..');
