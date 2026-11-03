// A small self-contained bzip2 decoder (Node has none built in, and the format is old and stable). Reads
// concatenated streams (pbzip2 output) and checks every block CRC. Randomised blocks, which no bzip2 since 0.9.5
// writes, are rejected. Output is produced on demand, one block (at most 900 kB before run-length expansion) at a
// time, so memory stays small whatever the file size.

const MAX_GROUPS = 6;
const GROUP_SIZE = 50;
const MAX_ALPHA = 258;
const MAX_CODE_LEN = 23;

const CRC_TABLE = (() => {
  const t = new Uint32Array(256);
  for (let i = 0; i < 256; i++) {
    let c = i << 24;
    for (let k = 0; k < 8; k++) c = c & 0x80000000 ? (c << 1) ^ 0x04c11db7 : c << 1;
    t[i] = c >>> 0;
  }
  return t;
})();

/** True if the bytes start like a bzip2 stream ("BZh1".."BZh9"). */
export function isBzip2(head: Uint8Array): boolean {
  return head.length >= 4 && head[0] === 0x42 && head[1] === 0x5a && head[2] === 0x68 && head[3] >= 0x31 && head[3] <= 0x39;
}

const corrupt = (why: string) => new Error(`bzip2 data is corrupt (${why})`);

export class Bzip2Decoder {
  private inBuf = Buffer.alloc(64 * 1024);
  private inPos = 0;
  private inLen = 0;
  private inEof = false;
  private bits = 0;
  private bitCount = 0;
  /** Compressed bytes consumed so far (for progress). */
  consumed = 0;

  private blockSize = 0;
  private tt = new Int32Array(0);
  private tPos = 0;
  private blockLeft = 0;
  private blockCrc = 0;
  private crc = 0;
  private last = -1;
  private run = 0;
  private repeat = 0;
  private done = false;
  private started = false;

  /** `fill` reads compressed bytes into the buffer and returns how many (0 at the end). */
  constructor(private fill: (buf: Buffer) => number) {
    if (!this.streamHeader(true)) throw new Error('Not a bzip2 file.');
  }

  /** Fills `out` with decompressed bytes; returns how many (0 at the end). */
  read(out: Uint8Array): number {
    let w = 0;
    while (w < out.length && !this.done) {
      if (this.repeat > 0) {
        const n = Math.min(this.repeat, out.length - w);
        let crc = this.crc;
        for (let i = 0; i < n; i++) crc = ((crc << 8) ^ CRC_TABLE[((crc >>> 24) ^ this.last) & 0xff]) >>> 0;
        this.crc = crc;
        out.fill(this.last, w, w + n);
        w += n;
        this.repeat -= n;
        continue;
      }
      if (this.blockLeft === 0) {
        if (this.started && ~this.crc >>> 0 !== this.blockCrc) throw corrupt('block CRC mismatch');
        if (!this.nextBlock()) this.done = true;
        continue;
      }
      // Inverse BWT one byte at a time, undoing the initial run-length step (4 equal bytes + a count byte).
      const tt = this.tt;
      let tPos = this.tPos, left = this.blockLeft, crc = this.crc, last = this.last, run = this.run;
      while (left > 0 && w < out.length) {
        tPos = tt[tPos];
        const b = tPos & 0xff;
        tPos >>>= 8;
        left--;
        if (run === 4) {
          this.repeat = b;
          run = 0;
          break;
        }
        if (b === last) run++;
        else {
          run = 1;
          last = b;
        }
        crc = ((crc << 8) ^ CRC_TABLE[((crc >>> 24) ^ b) & 0xff]) >>> 0;
        out[w++] = b;
      }
      this.tPos = tPos;
      this.blockLeft = left;
      this.crc = crc;
      this.last = last;
      this.run = run;
    }
    return w;
  }

  // ------------------------------------------------------------------ bits

  private byte(): number {
    if (this.inPos === this.inLen) {
      if (this.inEof) return -1;
      this.inLen = this.fill(this.inBuf);
      this.inPos = 0;
      if (this.inLen <= 0) {
        this.inLen = 0;
        this.inEof = true;
        return -1;
      }
    }
    this.consumed++;
    return this.inBuf[this.inPos++];
  }

  /** n ≤ 24 bits. */
  private getBits(n: number): number {
    while (this.bitCount < n) {
      const b = this.byte();
      if (b < 0) throw new Error('bzip2 data ends unexpectedly');
      this.bits = ((this.bits << 8) | b) & 0xffffffff;
      this.bitCount += 8;
    }
    this.bitCount -= n;
    return (this.bits >>> this.bitCount) & ((1 << n) - 1);
  }

  private bit(): boolean {
    return this.getBits(1) === 1;
  }

  /** Reads "BZh[1-9]"; for a later stream, false means a plain end of data. */
  private streamHeader(first: boolean): boolean {
    this.bitCount = 0; // streams start on a byte boundary
    const b = this.byte();
    if (b < 0 && !first) return false;
    if (b !== 0x42 || this.byte() !== 0x5a || this.byte() !== 0x68) {
      if (first) return false;
      throw new Error('bzip2 data has trailing garbage');
    }
    const level = this.byte() - 0x30;
    if (level < 1 || level > 9) throw corrupt('bad block size');
    this.blockSize = level * 100_000;
    return true;
  }

  // ----------------------------------------------------------------- block

  private nextBlock(): boolean {
    for (;;) {
      const hi = this.getBits(24), lo = this.getBits(24);
      if (hi === 0x177245 && lo === 0x385090) {
        this.getBits(16);
        this.getBits(16); // combined stream CRC (every block CRC was already checked)
        if (!this.streamHeader(false)) return false;
        continue;
      }
      if (hi !== 0x314159 || lo !== 0x265359) throw corrupt('bad block header');
      break;
    }
    this.blockCrc = ((this.getBits(16) << 16) | this.getBits(16)) >>> 0;
    if (this.bit()) throw new Error('Randomised bzip2 blocks are not supported');
    const origPtr = this.getBits(24);

    // Symbol map: which byte values occur in the block.
    const seqToUnseq = new Uint8Array(256);
    const inUse16 = this.getBits(16);
    let numInUse = 0;
    for (let i = 0; i < 16; i++) {
      if (!(inUse16 & (0x8000 >> i))) continue;
      const bits = this.getBits(16);
      for (let j = 0; j < 16; j++) if (bits & (0x8000 >> j)) seqToUnseq[numInUse++] = i * 16 + j;
    }
    if (numInUse === 0) throw corrupt('empty symbol map');
    const alphaSize = numInUse + 2;

    const nGroups = this.getBits(3);
    const nSelectors = this.getBits(15);
    if (nGroups < 2 || nGroups > MAX_GROUPS || nSelectors < 1) throw corrupt('bad Huffman groups');
    const selectors = new Uint8Array(nSelectors);
    const pos = [0, 1, 2, 3, 4, 5];
    for (let i = 0; i < nSelectors; i++) {
      let j = 0;
      while (this.bit()) if (++j >= nGroups) throw corrupt('bad selector');
      const v = pos[j];
      for (; j > 0; j--) pos[j] = pos[j - 1];
      pos[0] = v;
      selectors[i] = v;
    }

    // Code lengths, then canonical decode tables per group.
    const W = MAX_CODE_LEN + 2;
    const limit = new Int32Array(MAX_GROUPS * W).fill(-1);
    const base = new Int32Array(MAX_GROUPS * W);
    const perm = new Int32Array(MAX_GROUPS * MAX_ALPHA);
    const minLens = new Int32Array(MAX_GROUPS);
    const len = new Uint8Array(MAX_ALPHA);
    const count = new Int32Array(W);
    for (let t = 0; t < nGroups; t++) {
      let curr = this.getBits(5);
      for (let i = 0; i < alphaSize; i++) {
        for (;;) {
          if (curr < 1 || curr > 20) throw corrupt('bad code length');
          if (!this.bit()) break;
          curr += this.bit() ? -1 : 1;
        }
        len[i] = curr;
      }
      let minLen = 32, maxLen = 0;
      for (let i = 0; i < alphaSize; i++) {
        if (len[i] > maxLen) maxLen = len[i];
        if (len[i] < minLen) minLen = len[i];
      }
      minLens[t] = minLen;
      let pp = 0;
      for (let i = minLen; i <= maxLen; i++) for (let j = 0; j < alphaSize; j++) if (len[j] === i) perm[t * MAX_ALPHA + pp++] = j;
      count.fill(0);
      for (let i = 0; i < alphaSize; i++) count[len[i] + 1]++;
      for (let i = 1; i < W; i++) count[i] += count[i - 1];
      let vec = 0;
      for (let i = minLen; i <= maxLen; i++) {
        vec += count[i + 1] - count[i];
        limit[t * W + i] = vec - 1;
        vec <<= 1;
      }
      base[t * W + minLen] = count[minLen];
      for (let i = minLen + 1; i <= maxLen; i++) base[t * W + i] = ((limit[t * W + i - 1] + 1) << 1) - count[i];
    }

    // Huffman + move-to-front + RUNA/RUNB decoding into tt (byte values in the low 8 bits).
    if (this.tt.length < this.blockSize) this.tt = new Int32Array(this.blockSize);
    const tt = this.tt;
    const unzftab = new Int32Array(256);
    const mtf = new Uint8Array(256);
    for (let i = 0; i < 256; i++) mtf[i] = i;
    const eob = numInUse + 1;
    let nblock = 0, groupNo = -1, groupPos = 0, g = 0, runLen = 0, runBit = 1;
    for (;;) {
      if (groupPos === 0) {
        if (++groupNo >= nSelectors) throw corrupt('selectors exhausted');
        groupPos = GROUP_SIZE;
        g = selectors[groupNo];
      }
      groupPos--;
      let zn = minLens[g];
      let zvec = this.getBits(zn);
      while (zvec > limit[g * W + zn]) {
        if (++zn > 20) throw corrupt('bad Huffman code');
        zvec = (zvec << 1) | this.getBits(1);
      }
      const idx = zvec - base[g * W + zn];
      if (idx < 0 || idx >= MAX_ALPHA) throw corrupt('bad Huffman code');
      const sym = perm[g * MAX_ALPHA + idx];
      if (sym <= 1) {
        // RUNA / RUNB: a bijective base-2 run length of the symbol at the MTF front.
        runLen += sym === 0 ? runBit : runBit * 2;
        runBit *= 2;
        if (runLen > this.blockSize) throw corrupt('run too long');
        continue;
      }
      if (runLen > 0) {
        const uc = seqToUnseq[mtf[0]];
        if (nblock + runLen > this.blockSize) throw corrupt('block too long');
        unzftab[uc] += runLen;
        tt.fill(uc, nblock, nblock + runLen);
        nblock += runLen;
        runLen = 0;
        runBit = 1;
      }
      if (sym === eob) break;
      const nn = sym - 1;
      const v = mtf[nn];
      mtf.copyWithin(1, 0, nn);
      mtf[0] = v;
      const b = seqToUnseq[v];
      if (nblock >= this.blockSize) throw corrupt('block too long');
      unzftab[b]++;
      tt[nblock++] = b;
    }
    if (origPtr < 0 || origPtr >= Math.max(1, nblock)) throw corrupt('bad origin pointer');

    // Inverse BWT links: tt[i] keeps its byte in the low 8 bits and gets the next position in the high 24.
    const cftab = new Int32Array(257);
    for (let i = 0; i < 256; i++) cftab[i + 1] = cftab[i] + unzftab[i];
    for (let i = 0; i < nblock; i++) {
      const uc = tt[i] & 0xff;
      tt[cftab[uc]] |= i << 8;
      cftab[uc]++;
    }
    this.tPos = nblock > 0 ? tt[origPtr] >>> 8 : 0;
    this.blockLeft = nblock;
    this.crc = 0xffffffff;
    this.last = -1;
    this.run = 0;
    this.started = true;
    return true;
  }
}
