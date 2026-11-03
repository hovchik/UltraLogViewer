// Worker thread of the ScanPool: scans file chunks for line and entry starts, and reads and tests entry batches.
import * as fs from 'node:fs';
import { parentPort } from 'node:worker_threads';
import { EntryJob, evaluate, resultBuffers, splitEntries } from '../core/entryScan';
import { LogFormat } from '../core/formats';
import { FormatSpec, formatFromSpec, scanBuffers, scanChunk } from '../core/scanChunk';

const port = parentPort!;
const formats = new Map<number, LogFormat>();
const decoders = new Map<string, TextDecoder>();
const textDecoders = new Map<string, TextDecoder>();

interface Request {
  id: number;
  specId: number;
  spec?: FormatSpec;
  // a chunk to index
  encoding?: string;
  buf?: ArrayBuffer;
  offset?: number;
  length?: number;
  pos?: number;
  // or a batch of entries
  job?: EntryJob;
}

/**
 * Reads a job's entry texts from the file. The file is opened per job and closed right away, so a worker never holds
 * a log file open (that would block rotation on Windows), and a file replaced since it was indexed is refused: the
 * engine then reads the batch itself through the handle it indexed.
 */
function readJob(job: EntryJob): string[] {
  let dec = textDecoders.get(job.encoding);
  if (!dec) textDecoders.set(job.encoding, (dec = new TextDecoder(job.encoding, { ignoreBOM: true })));
  const fd = fs.openSync(job.path, 'r');
  try {
    if (job.ino && fs.fstatSync(fd).ino !== job.ino) throw new Error('file replaced');
    const texts: string[] = [];
    let e = 0;
    for (let r = 0; r < job.runEntries.length; r++) {
      const start = job.runs[2 * r];
      const buf = Buffer.allocUnsafe(job.runs[2 * r + 1] - start);
      let got = 0;
      while (got < buf.length) {
        const n = fs.readSync(fd, buf, got, buf.length - got, start + got);
        if (n <= 0) break;
        got += n;
      }
      const len = job.runEntries[r];
      for (const t of splitEntries(dec.decode(buf.subarray(0, got)), job.counts.subarray(e, e + len))) texts.push(t);
      e += len;
    }
    return texts;
  } finally {
    fs.closeSync(fd);
  }
}

port.on('message', (m: Request) => {
  try {
    if (m.spec) {
      formats.set(m.specId, formatFromSpec(m.spec));
      if (formats.size > 16) formats.delete(formats.keys().next().value!);
    }
    const fmt = formats.get(m.specId);
    if (!fmt) throw new Error('unknown format');
    if (m.job) {
      const r = evaluate(m.job, fmt, readJob(m.job));
      port.postMessage({ id: m.id, result: r }, resultBuffers(r));
      return;
    }
    let dec = decoders.get(m.encoding!);
    if (!dec) decoders.set(m.encoding!, (dec = new TextDecoder(m.encoding)));
    const r = scanChunk(fmt, dec, new Uint8Array(m.buf!, m.offset, m.length), m.pos!);
    port.postMessage({ id: m.id, result: r }, scanBuffers(r));
  } catch (e) {
    port.postMessage({ id: m.id, error: (e as Error).message ?? String(e) });
  }
});
