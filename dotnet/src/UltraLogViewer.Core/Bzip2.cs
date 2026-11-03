namespace UltraLogViewer.Core;

/// <summary>
/// A small self-contained bzip2 decoder (the format is old and stable, and .NET has none built in). Reads
/// concatenated streams (pbzip2 output) and checks every block CRC. Randomised blocks, which no bzip2 since
/// 0.9.5 writes, are rejected. Output is produced lazily, one block (at most 900 kB before run-length expansion)
/// at a time, so memory stays small whatever the file size.
/// </summary>
public sealed class Bzip2Stream : Stream
{
    private const int MaxGroups = 6, GroupSize = 50, MaxAlpha = 258, MaxCodeLen = 23;
    private static readonly uint[] CrcTable = BuildCrcTable();

    private readonly Stream _in;
    private readonly bool _leaveOpen;
    private ulong _bits;
    private int _bitCount;
    private readonly byte[] _inBuf = new byte[64 * 1024];
    private int _inPos, _inLen;
    private bool _inEof;

    private int _blockSize; // 100k units from the stream header
    private int[] _tt = [];
    private int _tPos, _blockLeft;
    private uint _blockCrc, _crc;
    private int _last = -1, _run, _repeat;
    private bool _done;

    public Bzip2Stream(Stream input, bool leaveOpen = false)
    {
        _in = input;
        _leaveOpen = leaveOpen;
        if (!ReadStreamHeader(true)) throw new InvalidDataException("Not a bzip2 file.");
    }

    /// <summary>True if the bytes start like a bzip2 stream ("BZh1".."BZh9").</summary>
    public static bool IsBzip2(ReadOnlySpan<byte> head) => head.Length >= 4 && head[0] == 'B' && head[1] == 'Z' && head[2] == 'h' && head[3] >= '1' && head[3] <= '9';

    /// <summary>Compressed bytes consumed so far (for progress).</summary>
    public long CompressedPosition { get; private set; }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> dest)
    {
        var w = 0;
        while (w < dest.Length && !_done)
        {
            if (_repeat > 0)
            {
                var n = Math.Min(_repeat, dest.Length - w);
                for (var i = 0; i < n; i++) _crc = (_crc << 8) ^ CrcTable[(_crc >> 24) ^ (uint)_last];
                dest.Slice(w, n).Fill((byte)_last);
                w += n;
                _repeat -= n;
                continue;
            }
            if (_blockLeft == 0)
            {
                if (_tt.Length > 0 && ~_crc != _blockCrc) throw new InvalidDataException("bzip2 data is corrupt (block CRC mismatch).");
                if (!NextBlock()) _done = true;
                continue;
            }
            // Inverse BWT one byte at a time, undoing the initial run-length step (4 equal bytes + a count byte).
            var tt = _tt;
            var tPos = _tPos;
            var left = _blockLeft;
            var crc = _crc;
            var last = _last;
            var run = _run;
            while (left > 0 && w < dest.Length)
            {
                tPos = tt[tPos];
                var b = tPos & 0xff;
                tPos >>= 8;
                left--;
                if (run == 4)
                {
                    _repeat = b;
                    run = 0;
                    break;
                }
                if (b == last) run++;
                else
                {
                    run = 1;
                    last = b;
                }
                crc = (crc << 8) ^ CrcTable[(crc >> 24) ^ (uint)b];
                dest[w++] = (byte)b;
            }
            _tPos = tPos;
            _blockLeft = left;
            _crc = crc;
            _last = last;
            _run = run;
            if (_repeat > 0) _run = 0; // the repeated bytes never start a new run
        }
        return w;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen) _in.Dispose();
        base.Dispose(disposing);
    }

    // ------------------------------------------------------------------ bits

    private int ReadByteRaw()
    {
        if (_inPos == _inLen)
        {
            if (_inEof) return -1;
            _inLen = _in.Read(_inBuf, 0, _inBuf.Length);
            _inPos = 0;
            if (_inLen <= 0)
            {
                _inLen = 0;
                _inEof = true;
                return -1;
            }
        }
        CompressedPosition++;
        return _inBuf[_inPos++];
    }

    private uint Bits(int n)
    {
        while (_bitCount < n)
        {
            var b = ReadByteRaw();
            if (b < 0) throw new InvalidDataException("bzip2 data ends unexpectedly.");
            _bits = (_bits << 8) | (uint)b;
            _bitCount += 8;
        }
        _bitCount -= n;
        return (uint)(_bits >> _bitCount) & (uint)((1UL << n) - 1);
    }

    private bool Bit() => Bits(1) == 1;

    /// <summary>Reads "BZh[1-9]". At the start of a later stream (after the previous one's end), false means plain end of data.</summary>
    private bool ReadStreamHeader(bool first)
    {
        _bitCount = 0; // streams start on a byte boundary
        var b = ReadByteRaw();
        if (b < 0 && !first) return false;
        if (b != 'B' || ReadByteRaw() != 'Z' || ReadByteRaw() != 'h')
        {
            if (first) return false;
            throw new InvalidDataException("bzip2 data has trailing garbage.");
        }
        var level = ReadByteRaw() - '0';
        if (level < 1 || level > 9) throw new InvalidDataException("Bad bzip2 block size.");
        _blockSize = level * 100_000;
        return true;
    }

    // ----------------------------------------------------------------- block

    private bool NextBlock()
    {
        while (true)
        {
            var magic = ((ulong)Bits(24) << 24) | Bits(24);
            if (magic == 0x177245385090UL)
            {
                Bits(32); // combined stream CRC (every block CRC was already checked)
                if (!ReadStreamHeader(false)) return false;
                continue;
            }
            if (magic != 0x314159265359UL) throw new InvalidDataException("bzip2 data is corrupt (bad block header).");
            break;
        }
        _blockCrc = Bits(32);
        if (Bit()) throw new InvalidDataException("Randomised bzip2 blocks are not supported.");
        var origPtr = (int)Bits(24);

        // Symbol map: which byte values occur in the block.
        Span<byte> seqToUnseq = stackalloc byte[256];
        var inUse16 = Bits(16);
        var numInUse = 0;
        for (var i = 0; i < 16; i++)
        {
            if ((inUse16 & (0x8000 >> i)) == 0) continue;
            var bits = Bits(16);
            for (var j = 0; j < 16; j++)
                if ((bits & (0x8000 >> j)) != 0) seqToUnseq[numInUse++] = (byte)(i * 16 + j);
        }
        if (numInUse == 0) throw new InvalidDataException("bzip2 data is corrupt (empty symbol map).");
        var alphaSize = numInUse + 2;

        var nGroups = (int)Bits(3);
        var nSelectors = (int)Bits(15);
        if (nGroups < 2 || nGroups > MaxGroups || nSelectors < 1) throw new InvalidDataException("bzip2 data is corrupt (bad Huffman groups).");
        var selectors = new byte[nSelectors];
        Span<byte> pos = stackalloc byte[MaxGroups];
        for (var i = 0; i < nGroups; i++) pos[i] = (byte)i;
        for (var i = 0; i < nSelectors; i++)
        {
            var j = 0;
            while (Bit())
                if (++j >= nGroups) throw new InvalidDataException("bzip2 data is corrupt (bad selector).");
            var v = pos[j];
            for (; j > 0; j--) pos[j] = pos[j - 1];
            pos[0] = v;
            selectors[i] = v;
        }

        // Code lengths, then canonical decode tables per group.
        var limit = new int[MaxGroups, MaxCodeLen + 2];
        var baseT = new int[MaxGroups, MaxCodeLen + 2];
        var perm = new int[MaxGroups, MaxAlpha];
        var minLens = new int[MaxGroups];
        Span<byte> len = stackalloc byte[MaxAlpha];
        Span<int> count = stackalloc int[MaxCodeLen + 2];
        for (var t = 0; t < nGroups; t++)
        {
            var curr = (int)Bits(5);
            for (var i = 0; i < alphaSize; i++)
            {
                while (true)
                {
                    if (curr < 1 || curr > 20) throw new InvalidDataException("bzip2 data is corrupt (bad code length).");
                    if (!Bit()) break;
                    curr += Bit() ? -1 : 1;
                }
                len[i] = (byte)curr;
            }
            int minLen = 32, maxLen = 0;
            for (var i = 0; i < alphaSize; i++)
            {
                if (len[i] > maxLen) maxLen = len[i];
                if (len[i] < minLen) minLen = len[i];
            }
            minLens[t] = minLen;
            var pp = 0;
            for (var i = minLen; i <= maxLen; i++)
                for (var j = 0; j < alphaSize; j++)
                    if (len[j] == i) perm[t, pp++] = j;
            count.Clear();
            for (var i = 0; i < alphaSize; i++) count[len[i] + 1]++;
            for (var i = 1; i < count.Length; i++) count[i] += count[i - 1];
            var vec = 0;
            for (var i = 0; i < MaxCodeLen + 2; i++) limit[t, i] = -1;
            for (var i = minLen; i <= maxLen; i++)
            {
                vec += count[i + 1] - count[i];
                limit[t, i] = vec - 1;
                vec <<= 1;
            }
            baseT[t, minLen] = count[minLen];
            for (var i = minLen + 1; i <= maxLen; i++) baseT[t, i] = ((limit[t, i - 1] + 1) << 1) - count[i];
        }

        // Huffman + move-to-front + RUNA/RUNB decoding into tt (byte values in the low 8 bits).
        if (_tt.Length < _blockSize) _tt = new int[_blockSize];
        var tt = _tt;
        Span<int> unzftab = stackalloc int[256];
        unzftab.Clear();
        Span<byte> mtf = stackalloc byte[256];
        for (var i = 0; i < 256; i++) mtf[i] = (byte)i;
        var eob = numInUse + 1;
        var nblock = 0;
        var groupNo = -1;
        var groupPos = 0;
        var g = 0;
        int runLen = 0, runBit = 1;
        while (true)
        {
            if (groupPos == 0)
            {
                if (++groupNo >= nSelectors) throw new InvalidDataException("bzip2 data is corrupt (selectors exhausted).");
                groupPos = GroupSize;
                g = selectors[groupNo];
            }
            groupPos--;
            var zn = minLens[g];
            var zvec = (int)Bits(zn);
            while (zvec > limit[g, zn])
            {
                if (++zn > 20) throw new InvalidDataException("bzip2 data is corrupt (bad Huffman code).");
                zvec = (zvec << 1) | (int)Bits(1);
            }
            var idx = zvec - baseT[g, zn];
            if (idx < 0 || idx >= MaxAlpha) throw new InvalidDataException("bzip2 data is corrupt (bad Huffman code).");
            var sym = perm[g, idx];
            if (sym <= 1)
            {
                // RUNA / RUNB: a bijective base-2 run length of the symbol at the MTF front.
                runLen += sym == 0 ? runBit : runBit << 1;
                runBit <<= 1;
                if (runLen > _blockSize) throw new InvalidDataException("bzip2 data is corrupt (run too long).");
                continue;
            }
            if (runLen > 0)
            {
                var uc = seqToUnseq[mtf[0]];
                if (nblock + runLen > _blockSize) throw new InvalidDataException("bzip2 data is corrupt (block too long).");
                unzftab[uc] += runLen;
                tt.AsSpan(nblock, runLen).Fill(uc);
                nblock += runLen;
                runLen = 0;
                runBit = 1;
            }
            if (sym == eob) break;
            var nn = sym - 1;
            var v = mtf[nn];
            for (var k = nn; k > 0; k--) mtf[k] = mtf[k - 1];
            mtf[0] = v;
            var b = seqToUnseq[v];
            if (nblock >= _blockSize) throw new InvalidDataException("bzip2 data is corrupt (block too long).");
            unzftab[b]++;
            tt[nblock++] = b;
        }
        if (origPtr < 0 || origPtr >= Math.Max(1, nblock)) throw new InvalidDataException("bzip2 data is corrupt (bad origin pointer).");

        // Inverse BWT links: tt[i] keeps its byte in the low 8 bits and gets the next position in the high 24.
        Span<int> cftab = stackalloc int[257];
        cftab[0] = 0;
        for (var i = 0; i < 256; i++) cftab[i + 1] = cftab[i] + unzftab[i];
        for (var i = 0; i < nblock; i++)
        {
            var uc = tt[i] & 0xff;
            tt[cftab[uc]] |= i << 8;
            cftab[uc]++;
        }
        _tPos = nblock > 0 ? tt[origPtr] >> 8 : 0;
        _blockLeft = nblock;
        _crc = 0xffffffff;
        _last = -1;
        _run = 0;
        return true;
    }

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i << 24;
            for (var k = 0; k < 8; k++) c = (c & 0x80000000) != 0 ? (c << 1) ^ 0x04c11db7 : c << 1;
            t[i] = c;
        }
        return t;
    }
}
