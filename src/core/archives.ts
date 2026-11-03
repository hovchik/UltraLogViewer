// Compressed inputs: gzip, bzip2 and zip archives. A zip opens as a merged view of its text members, each addressed
// as "archive.zip::member/path.log" so it can be opened, saved in a workspace and reopened like a file. The zip
// reader is a small central-directory parser (stored and deflated members, zip64) on top of zlib's raw inflate.

import * as fs from 'node:fs';
import * as path from 'node:path';
import * as zlib from 'node:zlib';
import { Bzip2Decoder, isBzip2 } from './bzip2';

export const MEMBER_SEPARATOR = '::';

/** Member extensions that are never logs (skipped when a zip is expanded). */
const BINARY_EXTENSIONS = new Set(
  '.png .jpg .jpeg .gif .bmp .ico .svg .webp .pdf .zip .gz .tgz .bz2 .xz .7z .rar .jar .war .class .exe .dll .so .dylib .bin .dat .db .sqlite .hprof .dmp .mdmp .core .pyc .woff .woff2 .ttf'.split(' '),
);

/** Splits "archive.zip::member" into its parts; null when the path is a plain file. */
export function splitMember(p: string): { archive: string; member: string } | null {
  const i = p.indexOf(MEMBER_SEPARATOR);
  if (i <= 0) return null;
  const archive = p.slice(0, i);
  try {
    if (!fs.statSync(archive).isFile()) return null;
    if (fs.statSync(p, { throwIfNoEntry: false })?.isFile()) return null; // a plain file may have "::" in its name
  } catch {
    return null;
  }
  return { archive, member: p.slice(i + MEMBER_SEPARATOR.length) };
}

export const memberPath = (archive: string, member: string) => archive + MEMBER_SEPARATOR + member;

/** Display name of a path: the file name, or the member's file name for a zip member. */
export function displayName(p: string): string {
  const m = splitMember(p);
  const name = m ? m.member : p;
  return name.split(/[\\/]/).pop() ?? name;
}

const isZipHead = (b: Uint8Array) => b.length >= 4 && b[0] === 0x50 && b[1] === 0x4b && b[2] === 3 && b[3] === 4;
const isGzipHead = (b: Uint8Array) => b.length >= 2 && b[0] === 0x1f && b[1] === 0x8b;

function readAt(fd: number, pos: number, len: number): Buffer {
  const b = Buffer.alloc(len);
  let got = 0;
  while (got < len) {
    const n = fs.readSync(fd, b, got, len - got, pos + got);
    if (n <= 0) break;
    got += n;
  }
  return b.subarray(0, got);
}

function head(file: string, n: number): Buffer {
  const fd = fs.openSync(file, 'r');
  try {
    return readAt(fd, 0, n);
  } finally {
    fs.closeSync(fd);
  }
}

export function isZipFile(file: string): boolean {
  try {
    return isZipHead(head(file, 4));
  } catch {
    return false;
  }
}

export interface ZipEntry {
  name: string;
  method: number;
  compressedSize: number;
  size: number;
  localOffset: number;
  encrypted: boolean;
}

/** Reads a zip's central directory. */
export function zipEntries(file: string): ZipEntry[] {
  const fd = fs.openSync(file, 'r');
  try {
    const size = fs.fstatSync(fd).size;
    // End of central directory: the last 22 bytes, plus a comment of up to 64 KB.
    const tailLen = Math.min(size, 22 + 0xffff);
    const tail = readAt(fd, size - tailLen, tailLen);
    let eocd = -1;
    for (let i = tail.length - 22; i >= 0; i--) {
      if (tail.readUInt32LE(i) === 0x06054b50) {
        eocd = i;
        break;
      }
    }
    if (eocd < 0) throw new Error(`${path.basename(file)} is not a zip archive`);
    let count = tail.readUInt16LE(eocd + 10);
    let cdSize = tail.readUInt32LE(eocd + 12);
    let cdOffset = tail.readUInt32LE(eocd + 16);
    if (count === 0xffff || cdSize === 0xffffffff || cdOffset === 0xffffffff) {
      // zip64: the locator just before the end record points at the zip64 end record.
      const loc = eocd - 20;
      if (loc < 0 || tail.readUInt32LE(loc) !== 0x07064b50) throw new Error('Bad zip64 archive');
      const recAt = Number(tail.readBigUInt64LE(loc + 8));
      if (recAt + 56 > size) throw new Error('Bad zip64 archive');
      const rec = readAt(fd, recAt, 56);
      if (rec.length < 56 || rec.readUInt32LE(0) !== 0x06064b50) throw new Error('Bad zip64 archive');
      count = Number(rec.readBigUInt64LE(32));
      cdSize = Number(rec.readBigUInt64LE(40));
      cdOffset = Number(rec.readBigUInt64LE(48));
    }
    // The directory must lie inside the file: a forged size would otherwise allocate gigabytes for a tiny file.
    if (!(cdOffset >= 0 && cdSize >= 0 && cdOffset + cdSize <= size && count <= cdSize / 46)) throw new Error(`${path.basename(file)} has a bad zip central directory`);
    const cd = readAt(fd, cdOffset, cdSize);
    const out: ZipEntry[] = [];
    let p = 0;
    for (let i = 0; i < count && p + 46 <= cd.length; i++) {
      if (cd.readUInt32LE(p) !== 0x02014b50) throw new Error('Bad zip central directory');
      const flags = cd.readUInt16LE(p + 8);
      const nameLen = cd.readUInt16LE(p + 28), extraLen = cd.readUInt16LE(p + 30), commentLen = cd.readUInt16LE(p + 32);
      const nameBytes = cd.subarray(p + 46, p + 46 + nameLen);
      const e: ZipEntry = {
        name: flags & 0x800 ? nameBytes.toString('utf-8') : nameBytes.toString('latin1'),
        method: cd.readUInt16LE(p + 10),
        compressedSize: cd.readUInt32LE(p + 20),
        size: cd.readUInt32LE(p + 24),
        localOffset: cd.readUInt32LE(p + 42),
        encrypted: (flags & 1) !== 0,
      };
      // zip64 extra field: the 64-bit values of the fields that are 0xffffffff, in this order.
      let x = p + 46 + nameLen;
      const xEnd = x + extraLen;
      while (x + 4 <= xEnd) {
        const id = cd.readUInt16LE(x), len = cd.readUInt16LE(x + 2);
        if (id === 1) {
          let q = x + 4;
          if (e.size === 0xffffffff) (e.size = Number(cd.readBigUInt64LE(q))), (q += 8);
          if (e.compressedSize === 0xffffffff) (e.compressedSize = Number(cd.readBigUInt64LE(q))), (q += 8);
          if (e.localOffset === 0xffffffff) e.localOffset = Number(cd.readBigUInt64LE(q));
        }
        x += 4 + len;
      }
      out.push(e);
      p = xEnd + commentLen;
    }
    return out;
  } finally {
    fs.closeSync(fd);
  }
}

/** Text members of a zip in name order (directories, empty and binary members left out). */
export function logMembers(file: string): string[] {
  return zipEntries(file)
    .filter((e) => e.size > 0 && !e.name.endsWith('/') && !e.name.startsWith('__MACOSX/') && !BINARY_EXTENSIONS.has(path.extname(e.name).toLowerCase()))
    .map((e) => e.name)
    .sort((a, b) => a.toLowerCase().localeCompare(b.toLowerCase()));
}

/** Replaces each zip file by its text members, so a support bundle opens as one merged timeline. */
export function expandArchives(paths: string[]): string[] {
  const out: string[] = [];
  for (const p of paths) {
    if (!splitMember(p) && isZipFile(p)) {
      const members = logMembers(p);
      if (!members.length) throw new Error(`${path.basename(p)} has no text files`);
      out.push(...members.map((m) => memberPath(p, m)));
    } else out.push(p);
  }
  return out;
}

export interface Decompressed {
  kind: 'gzip' | 'bzip2' | 'zip';
  /** Progress: position() of total (compressed bytes, or uncompressed for a zip member). */
  total: number;
  position(): number;
  chunks: AsyncIterable<Buffer>;
}

const yieldNow = () => new Promise<void>((r) => setImmediate(r));

/** Opens a decompressing reader over a compressed input, or returns null for a plain file. */
export function openDecompressed(file: string): Decompressed | null {
  const m = splitMember(file);
  if (m) return openZipMember(m.archive, m.member);
  const h = head(file, 4);
  if (isGzipHead(h)) {
    const input = fs.createReadStream(file, { highWaterMark: 1 << 20 });
    const gunzip = zlib.createGunzip({ chunkSize: 1 << 20 });
    input.on('error', (e) => gunzip.destroy(e));
    // Stopped early (tab closed, or an error): the file stream would otherwise stay open, holding the file.
    gunzip.on('close', () => input.destroy());
    input.pipe(gunzip);
    return { kind: 'gzip', total: fs.statSync(file).size, position: () => input.bytesRead, chunks: gunzip };
  }
  if (isBzip2(h)) {
    const fd = fs.openSync(file, 'r');
    let dec: Bzip2Decoder;
    try {
      dec = new Bzip2Decoder((buf) => fs.readSync(fd, buf, 0, buf.length, null));
    } catch (e) {
      fs.closeSync(fd);
      throw e;
    }
    // Decoding runs on this thread: yield between megabytes so the index and the UI requests interleave.
    async function* gen() {
      try {
        for (;;) {
          const out = Buffer.allocUnsafe(1 << 20);
          const n = dec.read(out);
          if (n === 0) return;
          yield out.subarray(0, n);
          await yieldNow();
        }
      } finally {
        fs.closeSync(fd);
      }
    }
    return { kind: 'bzip2', total: fs.statSync(file).size, position: () => dec.consumed, chunks: gen() };
  }
  if (isZipHead(h)) {
    // A zip opened directly (not expanded): read its first text member.
    const first = logMembers(file)[0];
    if (!first) throw new Error(`${path.basename(file)} has no text files`);
    return openZipMember(file, first);
  }
  return null;
}

function openZipMember(archive: string, member: string): Decompressed {
  const e = zipEntries(archive).find((x) => x.name === member);
  if (!e) throw new Error(`${member} is not in ${path.basename(archive)}`);
  if (e.encrypted) throw new Error(`${member} is encrypted`);
  if (e.method !== 0 && e.method !== 8) throw new Error(`${member} uses an unsupported zip compression method (${e.method})`);
  const fd = fs.openSync(archive, 'r');
  let local: Buffer;
  try {
    local = readAt(fd, e.localOffset, 30);
  } finally {
    fs.closeSync(fd);
  }
  if (local.length < 30 || local.readUInt32LE(0) !== 0x04034b50) throw new Error('Bad zip local header');
  const start = e.localOffset + 30 + local.readUInt16LE(26) + local.readUInt16LE(28);
  let out = 0;
  const raw = fs.createReadStream(archive, { start, end: start + e.compressedSize - 1, highWaterMark: 1 << 20 });
  let src: NodeJS.ReadableStream = raw;
  if (e.method === 8) {
    const inflate = zlib.createInflateRaw({ chunkSize: 1 << 20 });
    raw.on('error', (err) => inflate.destroy(err));
    inflate.on('close', () => raw.destroy());
    src = raw.pipe(inflate);
  }
  async function* gen() {
    for await (const c of src as AsyncIterable<Buffer>) {
      out += c.length;
      yield c;
    }
  }
  return { kind: 'zip', total: e.size, position: () => out, chunks: gen() };
}
