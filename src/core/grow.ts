// Growable typed arrays: the index for a multi-gigabyte file is tens of millions of numbers,
// far too many for plain JS arrays.

type TypedCtor<T> = { new (n: number): T };

class Grow<T extends Float64Array | Uint32Array | Uint8Array> {
  data: T;
  length = 0;
  constructor(private ctor: TypedCtor<T>, initial = 1024) {
    this.data = new ctor(initial);
  }
  push(v: number) {
    if (this.length === this.data.length) {
      const next = new this.ctor(this.data.length * 2);
      next.set(this.data as never);
      this.data = next;
    }
    this.data[this.length++] = v;
  }
  get(i: number) {
    return this.data[i];
  }
  set(i: number, v: number) {
    this.data[i] = v;
  }
  truncate(n: number) {
    if (n < this.length) this.length = n;
  }
  last() {
    return this.data[this.length - 1];
  }
  /** A view of the used part (no copy). */
  view(): T {
    return this.data.subarray(0, this.length) as T;
  }
}

export class GrowF64 extends Grow<Float64Array> {
  constructor(initial?: number) {
    super(Float64Array, initial);
  }
}
export class GrowU32 extends Grow<Uint32Array> {
  constructor(initial?: number) {
    super(Uint32Array, initial);
  }
}
export class GrowU8 extends Grow<Uint8Array> {
  constructor(initial?: number) {
    super(Uint8Array, initial);
  }
}

/** First index in [lo, hi) where pred is true, assuming pred is monotone. */
export function lowerBound(lo: number, hi: number, pred: (i: number) => boolean): number {
  while (lo < hi) {
    const mid = (lo + hi) >>> 1;
    if (pred(mid)) hi = mid;
    else lo = mid + 1;
  }
  return lo;
}
