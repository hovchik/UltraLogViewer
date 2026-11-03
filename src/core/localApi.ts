// The local HTTP API, off unless the user turns it on. It listens on 127.0.0.1 only and every request needs the token
// (`Authorization: Bearer …` or `?token=`). A web page cannot use it: requests whose Host is not 127.0.0.1/localhost
// (DNS rebinding) and requests with a foreign Origin are refused, and there are no CORS headers, so a browser never
// lets a page read an answer.

import * as http from 'node:http';
import { randomBytes, timingSafeEqual } from 'node:crypto';
import type { AddressInfo } from 'node:net';
import { OpenRequest, requestFromJson } from './openRequest';
import type { RowData } from './types';

export const DEFAULT_API_PORT = 7700;
export const MAX_API_ROWS = 5000;
const MAX_BODY = 1 << 20;

/** An open tab as the API lists it. */
export interface ApiTab {
  id: number;
  title: string;
  paths: string[];
  total: number;
  shown: number;
  filter: string;
  ready: boolean;
  active: boolean;
}

/** Outcome of a call that changes something: an HTTP status, an error text, and the tab it concerns. */
export interface ApiResult {
  status: number;
  error?: string;
  tab?: number;
}

/** What the app does for the API. */
export interface LocalApiHost {
  open(r: OpenRequest): Promise<ApiResult>;
  tabs(): Promise<ApiTab[]>;
  /** Applies the filter, levels, search and "go to" of the request to an open tab. */
  update(tab: number, r: OpenRequest): Promise<ApiResult>;
  /** Rows of the tab's current view; null when there is no such tab. */
  rows(tab: number, from: number, count: number): Promise<RowData[] | null>;
}

export interface LocalApi {
  port: number;
  close(): Promise<void>;
}

/** A new random token (256 bits, hex). */
export const newApiToken = () => randomBytes(32).toString('hex');

/** Host must name the loopback address: a DNS-rebinding page's requests carry its own host name. */
export function hostAllowed(host: string | undefined): boolean {
  if (!host) return false;
  const name = host.startsWith('[') ? host.slice(0, host.indexOf(']') + 1) : host.split(':')[0];
  return name === '127.0.0.1' || name.toLowerCase() === 'localhost';
}

const LEVEL_LABELS = ['', 'TRACE', 'DEBUG', 'INFO', 'WARN', 'ERROR', 'FATAL'];

/** One row as a JSON line: view position, file, line, time (ISO, UTC), level, fields and the text. */
export const rowJson = (r: RowData) =>
  JSON.stringify({ row: r.row, source: r.source, line: r.line, time: r.time != null ? new Date(r.time).toISOString() : null, level: LEVEL_LABELS[r.level] ?? '', fields: r.fields, text: r.text });

/** Listens on `port`, or the next free one of the 9 after it, or any free port. */
export async function startLocalApi(host: LocalApiHost, token: string, port = DEFAULT_API_PORT, now: () => number = Date.now): Promise<LocalApi> {
  if (token.length < 16) throw new Error('The API token is too short');
  const tokenBytes = Buffer.from(token);
  let bound = 0;

  const tokenOk = (req: http.IncomingMessage, query: URLSearchParams) => {
    const auth = req.headers.authorization;
    const given = auth && /^bearer /i.test(auth) ? auth.slice(7).trim() : query.get('token');
    if (given == null) return false;
    const b = Buffer.from(given);
    return b.length === tokenBytes.length && timingSafeEqual(b, tokenBytes);
  };

  const handle = async (req: http.IncomingMessage, body: string): Promise<[number, string, string?]> => {
    const err = (status: number, error: string): [number, string] => [status, JSON.stringify({ error })];
    // node keeps the first of repeated Host headers: refuse them outright, a second one could be what a proxy saw
    const names = req.rawHeaders.filter((_, i) => i % 2 === 0).map((n) => n.toLowerCase());
    for (const h of ['host', 'authorization', 'origin']) if (names.filter((n) => n === h).length > 1) return err(400, `Repeated ${h} header`);
    if (!hostAllowed(req.headers.host)) return err(403, 'Host must be 127.0.0.1 or localhost');
    const origin = req.headers.origin;
    const site = req.headers['sec-fetch-site'];
    if ((origin !== undefined && origin !== `http://127.0.0.1:${bound}` && origin.toLowerCase() !== `http://localhost:${bound}`) || site === 'cross-site' || site === 'same-site')
      return err(403, 'Requests from web pages are not allowed');
    const url = new URL(req.url ?? '/', 'http://127.0.0.1');
    if (!tokenOk(req, url.searchParams)) return err(401, 'Missing or wrong token');
    const reply = (r: ApiResult): [number, string] => (r.error !== undefined ? err(r.status, r.error) : [r.status, JSON.stringify({ ok: true, tab: r.tab ?? null })]);
    const method = req.method ?? 'GET';
    const p = url.pathname;
    if (p === '/health') return method === 'GET' ? [200, JSON.stringify({ ok: true, app: 'UltraLogViewer' })] : err(405, 'Method not allowed');
    if (p === '/tabs') return method === 'GET' ? [200, JSON.stringify(await host.tabs())] : err(405, 'Method not allowed');
    if (p === '/open') {
      if (method !== 'POST') return err(405, 'Method not allowed');
      const r = requestFromJson(body, now());
      if (r.errors.length) return err(400, r.errors.join('; '));
      if (!r.paths.length) return err(400, 'No paths');
      return reply(await host.open(r));
    }
    const m = /^\/tabs\/(\d{1,9})\/(filter|goto|rows)$/.exec(p);
    if (!m) return err(404, 'No such endpoint');
    const id = Number(m[1]);
    const intParam = (k: string, d: number) => (/^-?\d+$/.test(url.searchParams.get(k) ?? '') ? Number(url.searchParams.get(k)) : d);
    if (m[2] === 'rows') {
      if (method !== 'GET') return err(405, 'Method not allowed');
      const rows = await host.rows(id, Math.max(0, intParam('from', 0)), Math.min(MAX_API_ROWS, Math.max(0, intParam('count', 100))));
      if (!rows) return err(404, `No tab ${id}`);
      return [200, rows.map((r) => rowJson(r) + '\n').join(''), 'application/x-ndjson; charset=utf-8'];
    }
    if (method !== 'POST') return err(405, 'Method not allowed');
    const r = requestFromJson(body, now());
    if (r.errors.length) return err(400, r.errors.join('; '));
    if (m[2] === 'goto') {
      if (!r.goto) return err(400, 'Give "goto": a time, a line or "file:line"');
      r.filter = r.search = r.levels = null;
    } else r.goto = null;
    return reply(await host.update(id, r));
  };

  const server = http.createServer({ maxHeaderSize: 16 * 1024 }, (req, res) => {
    const send = (status: number, text: string, type = 'application/json; charset=utf-8') => {
      res.writeHead(status, { 'Content-Type': type, 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff', Connection: 'close', 'Content-Length': Buffer.byteLength(text) });
      res.end(text);
    };
    if (req.headers['transfer-encoding']) {
      send(411, JSON.stringify({ error: 'Send a Content-Length' }));
      req.destroy();
      return;
    }
    const chunks: Buffer[] = [];
    let size = 0;
    req.on('data', (c: Buffer) => {
      size += c.length;
      if (size > MAX_BODY) {
        send(413, JSON.stringify({ error: 'Body too large' }));
        req.destroy();
      } else chunks.push(c);
    });
    req.on('end', () => {
      if (size > MAX_BODY) return;
      handle(req, Buffer.concat(chunks).toString('utf-8')).then(
        ([status, text, type]) => send(status, text, type),
        (e) => send(500, JSON.stringify({ error: (e as Error).message })),
      );
    });
  });
  server.requestTimeout = 30_000;

  const listen = (p: number) =>
    new Promise<boolean>((resolve, reject) => {
      const onError = (e: NodeJS.ErrnoException) => (e.code === 'EADDRINUSE' || e.code === 'EACCES' ? resolve(false) : reject(e));
      server.once('error', onError);
      server.listen(p, '127.0.0.1', () => {
        server.off('error', onError);
        resolve(true);
      });
    });
  const tries = port > 0 && port < 65536 ? [...Array.from({ length: 10 }, (_, i) => port + i).filter((p) => p < 65536), 0] : [0];
  for (const p of tries) {
    if (await listen(p)) {
      bound = (server.address() as AddressInfo).port;
      return {
        port: bound,
        close: () => new Promise((resolve) => {
          server.close(() => resolve());
          server.closeAllConnections?.();
        }),
      };
    }
  }
  throw new Error('No free port for the local API');
}
