// Worker thread of the ScanPool: scans file chunks for line and entry starts.
import { parentPort } from 'node:worker_threads';
import { LogFormat } from '../core/formats';
import { FormatSpec, formatFromSpec, scanBuffers, scanChunk } from '../core/scanChunk';

const port = parentPort!;
const formats = new Map<number, LogFormat>();
const decoders = new Map<string, TextDecoder>();

interface ScanRequest {
  id: number;
  specId: number;
  spec?: FormatSpec;
  encoding: string;
  buf: ArrayBuffer;
  offset: number;
  length: number;
  pos: number;
}

port.on('message', (m: ScanRequest) => {
  try {
    if (m.spec) {
      formats.set(m.specId, formatFromSpec(m.spec));
      if (formats.size > 16) formats.delete(formats.keys().next().value!);
    }
    const fmt = formats.get(m.specId);
    if (!fmt) throw new Error('unknown format');
    let dec = decoders.get(m.encoding);
    if (!dec) decoders.set(m.encoding, (dec = new TextDecoder(m.encoding)));
    const r = scanChunk(fmt, dec, new Uint8Array(m.buf, m.offset, m.length), m.pos);
    port.postMessage({ id: m.id, result: r }, scanBuffers(r));
  } catch (e) {
    port.postMessage({ id: m.id, error: (e as Error).message ?? String(e) });
  }
});
