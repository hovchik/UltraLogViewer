// A network listener that writes what it receives to a CaptureFile: syslog over UDP or TCP (each message normalised to
// one RFC 5424 line), plain text lines over TCP, or log4j/log4net XML events (one JSON line each). Receiving never waits
// for the disk; stopping (or closing the tab) frees the port at once.

import * as dgram from 'node:dgram';
import * as net from 'node:net';
import { StringDecoder } from 'node:string_decoder';
import { CaptureFile } from './captureFile';
import { log4jToJson, Log4jXmlSplitter } from './log4jXml';
import { isIPv6, parseBind, ReceiverProtocol } from './receiverOptions';
import { cleanLine, StreamFramer, syslogToLine } from './syslog';

export { PROTOCOLS, isLoopback, parseBind } from './receiverOptions';
export type { ReceiverProtocol } from './receiverOptions';

export interface ReceiverOptions {
  protocol: ReceiverProtocol;
  bind?: string;
  port?: number;
  /** Longest message or line kept, in bytes; the rest is cut off. */
  maxMessage?: number;
  /** Size at which the capture file is rotated. */
  capBytes?: number;
}

export const MAX_CLIENTS = 64;

const scheme = (p: ReceiverProtocol) => (p === 'syslog-udp' ? 'udp' : 'tcp');

export class LogReceiver {
  readonly capture: CaptureFile;
  readonly options: Required<ReceiverOptions>;
  port = 0;
  endpoint = '';
  messages = 0;
  truncated = 0;
  private stopped = false;
  private udp: dgram.Socket | null = null;
  private server: net.Server | null = null;
  private clients = new Set<net.Socket>();
  private senders = new Set<string>();

  private constructor(o: Required<ReceiverOptions>, dir: string) {
    this.options = o;
    this.capture = CaptureFile.create(dir, `${o.protocol}-${o.port}`, o.capBytes);
  }

  /** Binds (rejects with EADDRINUSE when the port is taken) and starts receiving. */
  static async start(opts: ReceiverOptions, dir: string): Promise<LogReceiver> {
    const o: Required<ReceiverOptions> = { bind: '127.0.0.1', port: 5514, maxMessage: 64 * 1024, capBytes: 1024 ** 3, ...opts };
    const addr = parseBind(o.bind);
    if (!addr) throw new Error(`Not an IP address: ${o.bind}`);
    if (!Number.isInteger(o.port) || o.port < 0 || o.port > 65535) throw new Error(`Not a port: ${o.port}`);
    if (o.maxMessage < 256 || o.maxMessage > 16 * 1024 * 1024) throw new Error('The maximum message size must be between 256 bytes and 16 MB');
    let port: number;
    let bound: { udp?: dgram.Socket; server?: net.Server };
    if (o.protocol === 'syslog-udp') {
      // a large receive buffer absorbs bursts while the handler hands datagrams to the queue
      const s = dgram.createSocket({ type: isIPv6(addr) ? 'udp6' : 'udp4', recvBufferSize: 4 * 1024 * 1024 });
      await new Promise<void>((resolve, reject) => {
        s.once('error', reject);
        s.bind(o.port, addr, () => {
          s.off('error', reject);
          resolve();
        });
      });
      port = s.address().port;
      bound = { udp: s };
    } else {
      const server = net.createServer({ pauseOnConnect: false });
      server.maxConnections = MAX_CLIENTS;
      await new Promise<void>((resolve, reject) => {
        server.once('error', reject);
        server.listen({ port: o.port, host: addr, exclusive: true }, () => {
          server.off('error', reject);
          resolve();
        });
      });
      port = (server.address() as net.AddressInfo).port;
      bound = { server };
    }
    let r: LogReceiver;
    try {
      r = new LogReceiver({ ...o, port }, dir);
    } catch (e) {
      bound.udp?.close();
      bound.server?.close();
      throw e;
    }
    r.port = port;
    r.endpoint = `${scheme(o.protocol)}://${isIPv6(addr) ? `[${addr}]` : addr}:${port}`;
    if (bound.udp) r.listenUdp(bound.udp);
    else r.listenTcp(bound.server!);
    return r;
  }

  get path() {
    return this.capture.path;
  }
  get running() {
    return !this.stopped;
  }
  /** Connected TCP clients, or for UDP the number of different senders seen. */
  get clientCount() {
    return this.options.protocol === 'syslog-udp' ? this.senders.size : this.clients.size;
  }

  get status(): string {
    const n = this.messages;
    const c = this.clientCount;
    const who = this.options.protocol === 'syslog-udp' ? (c === 1 ? 'sender' : 'senders') : c === 1 ? 'client' : 'clients';
    let s = `${n.toLocaleString('en-US')} ${n === 1 ? 'message' : 'messages'} · ${c} ${who}`;
    if (this.truncated) s += ` · ${this.truncated.toLocaleString('en-US')} cut to ${this.options.maxMessage.toLocaleString('en-US')} bytes`;
    if (this.capture.dropped) s += ` · ${this.capture.dropped.toLocaleString('en-US')} dropped`;
    if (this.capture.error) s += ` · write error: ${this.capture.error}`;
    return `${this.running ? `Listening on ${this.endpoint}` : `Stopped (${this.endpoint})`}\n${s}`;
  }

  private add(line: string) {
    if (line && this.capture.writeLine(line)) this.messages++;
  }

  private listenUdp(s: dgram.Socket) {
    this.udp = s;
    s.on('error', () => {
      // an ICMP error for an earlier datagram: keep listening
    });
    s.on('message', (msg, rinfo) => {
      if (this.senders.size < 1000) this.senders.add(rinfo.address);
      let n = msg.length;
      if (n > this.options.maxMessage) {
        n = this.options.maxMessage;
        this.truncated++;
      }
      while (n > 0 && (msg[n - 1] === 10 || msg[n - 1] === 13 || msg[n - 1] === 0)) n--;
      if (n === 0) return;
      this.add(syslogToLine(msg.toString('utf-8', 0, n), new Date(), rinfo.address));
    });
  }

  private listenTcp(server: net.Server) {
    this.server = server;
    server.on('error', () => {
      // accept errors (too many open files…): keep the listener
    });
    server.on('connection', (sock) => {
      if (this.stopped || this.clients.size >= MAX_CLIENTS) return void sock.destroy();
      this.clients.add(sock);
      const p = this.options.protocol;
      const sender = sock.remoteAddress?.replace(/^::ffff:/, '') ?? '-';
      const framer = p === 'log4j-xml' ? null : new StreamFramer(p === 'syslog-tcp', this.options.maxMessage);
      const xml = framer ? null : new Log4jXmlSplitter(this.options.maxMessage);
      const decoder = new StringDecoder('utf-8');
      let counted = 0;
      const countTruncated = () => {
        const t = (framer?.truncated ?? 0) + (xml?.truncated ?? 0);
        this.truncated += t - counted;
        counted = t;
      };
      const onMessage = (m: string) => {
        if (p === 'syslog-tcp') this.add(syslogToLine(m, new Date(), sender));
        else {
          const line = cleanLine(m);
          if (line.trim()) this.add(line);
        }
      };
      sock.on('data', (chunk: Buffer) => {
        if (framer) framer.feed(chunk, onMessage);
        else for (const ev of xml!.feed(decoder.write(chunk))) this.add(log4jToJson(ev) ?? cleanLine(ev));
        countTruncated();
      });
      const done = () => {
        if (!this.clients.delete(sock)) return;
        framer?.end(onMessage);
        countTruncated();
      };
      sock.on('end', done);
      sock.on('close', done);
      sock.on('error', () => sock.destroy());
    });
  }

  /** Stops listening, disconnects the clients and closes the capture after writing what was received. Resolves once the port is free. */
  async stop(): Promise<void> {
    if (this.stopped) return;
    this.stopped = true;
    const closing: Promise<void>[] = [];
    if (this.udp) {
      const u = this.udp;
      closing.push(new Promise((r) => u.close(() => r())));
    }
    if (this.server) {
      const s = this.server;
      closing.push(new Promise((r) => s.close(() => r())));
      for (const c of this.clients) c.destroy();
      this.clients.clear();
    }
    await Promise.all(closing);
    await this.capture.close();
  }
}
