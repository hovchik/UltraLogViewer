using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace UltraLogViewer.Core;

/// <summary>What identifies the file and the settings an index was built with.</summary>
public sealed class IndexCacheKey
{
    public string Path { get; init; } = "";
    public long Size { get; init; }
    public long MtimeTicks { get; init; }
    public int HeadLen { get; init; }
    public byte[] HeadHash { get; init; } = [];
    public int TailLen { get; init; }
    /// <summary>Hash of the last <see cref="TailLen"/> bytes before <see cref="Size"/>.</summary>
    public byte[] TailHash { get; init; } = [];
    /// <summary>The forced format and encoding ("" = detect) and a fingerprint of the custom formats.</summary>
    public string FormatOption { get; init; } = "";
    public string EncodingOption { get; init; } = "";
    public string CustomKey { get; init; } = "";
}

/// <summary>A saved index: the key it is valid for, the detection results and the index arrays.</summary>
public sealed class IndexCacheEntry
{
    public required IndexCacheKey Key { get; init; }
    public string FormatId { get; init; } = "";
    public string EncodingName { get; init; } = "";
    public int DataStart { get; init; }
    public bool DetectAuto { get; init; }
    public int DetectMatched { get; init; }
    public int DetectSampled { get; init; }
    public long DataEnd { get; init; }
    public long[] LineStarts { get; init; } = [];
    public int[] EntryLines { get; init; } = [];
    public byte[] Levels { get; init; } = [];
    public long[] Times { get; init; } = [];
}

/// <summary>
/// On-disk cache of file indexes, so reopening a big file is instant and a grown file only indexes the new bytes.
/// One file per indexed path, in a compact binary format: a versioned header, then line starts and entry lines as
/// varint deltas, levels as bytes and times as zigzag varint deltas. A cache file that fails any check is ignored.
/// The total size is capped by deleting the least recently used files.
/// </summary>
public static class IndexCache
{
    /// <summary>Bump whenever the scan rules or the layout change: older files are then ignored (and age out).</summary>
    public const int Version = 1;
    public const int HashBytes = 64 * 1024;
    private static readonly byte[] Magic = "ULVIDX"u8.ToArray();
    private const int SectionTable = 5 * 8;

    public static string FileFor(string dir, string path) =>
        System.IO.Path.Combine(dir, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..32].ToLowerInvariant() + ".idx");

    /// <summary>SHA-256 of [offset, offset + len) of an open file.</summary>
    public static byte[] HashRange(SafeFileHandle h, long offset, int len)
    {
        var buf = new byte[len];
        var got = 0;
        while (got < len)
        {
            var n = RandomAccess.Read(h, buf.AsSpan(got), offset + got);
            if (n <= 0) break;
            got += n;
        }
        return SHA256.HashData(buf.AsSpan(0, got));
    }

    /// <summary>Fingerprint of the custom formats, which auto-detection and custom format ids depend on.</summary>
    public static string CustomFormatsKey(IEnumerable<CustomFormat> formats)
    {
        var sb = new StringBuilder();
        foreach (var f in formats) sb.Append(f.Name).Append('\u0001').Append(f.Pattern).Append('\u0002');
        return sb.Length == 0 ? "" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }

    // ------------------------------------------------------------------ write

    public static void Write(string file, IndexCacheEntry header, ReadOnlySpan<long> lineStarts, ReadOnlySpan<int> entryLines, ReadOnlySpan<byte> levels, ReadOnlySpan<long> times)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        var tmp = file + "." + Environment.ProcessId + ".tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                var w = new VarWriter(fs);
                w.Bytes(Magic);
                w.U(Version);
                var k = header.Key;
                w.Str(k.Path);
                w.U((ulong)k.Size);
                w.U((ulong)k.MtimeTicks);
                w.U((ulong)k.HeadLen);
                w.Blob(k.HeadHash);
                w.U((ulong)k.TailLen);
                w.Blob(k.TailHash);
                w.Str(k.FormatOption);
                w.Str(k.EncodingOption);
                w.Str(k.CustomKey);
                w.Str(header.FormatId);
                w.Str(header.EncodingName);
                w.U((ulong)header.DataStart);
                w.U(header.DetectAuto ? 1UL : 0);
                w.U((ulong)header.DetectMatched);
                w.U((ulong)header.DetectSampled);
                w.U((ulong)header.DataEnd);
                w.U((ulong)lineStarts.Length);
                w.U((ulong)entryLines.Length);
                // Four sections, located by a table at the end so they can be decoded in parallel.
                Span<long> sections = stackalloc long[5];
                sections[0] = w.Position;
                long prev = 0;
                foreach (var v in lineStarts)
                {
                    w.U((ulong)(v - prev));
                    prev = v;
                }
                var prevLine = 0;
                sections[1] = w.Position;
                foreach (var v in entryLines)
                {
                    w.U((ulong)(v - prevLine));
                    prevLine = v;
                }
                sections[2] = w.Position;
                w.Bytes(levels);
                sections[3] = w.Position;
                prev = 0;
                foreach (var v in times)
                {
                    var d = unchecked(v - prev); // wraps for the "no time" marker; decoding wraps back
                    w.U((ulong)((d << 1) ^ (d >> 63)));
                    prev = v;
                }
                sections[4] = w.Position;
                // Section starts (lines, entry lines, levels, times) and the end of the times, as fixed-size numbers.
                Span<byte> table = stackalloc byte[SectionTable];
                for (var i = 0; i < 5; i++) System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(table[(i * 8)..], sections[i]);
                w.Bytes(table);
                w.Bytes(Magic); // trailer: a truncated file never passes
                w.Flush();
            }
            File.Move(tmp, file, true);
        }
        catch
        {
            try { File.Delete(tmp); } catch (IOException) { }
            throw;
        }
    }

    private sealed class VarWriter(Stream s)
    {
        private readonly byte[] _buf = new byte[1 << 20];
        private int _n;
        private long _flushed;

        public long Position => _flushed + _n;

        public void U(ulong v)
        {
            if (_n > _buf.Length - 10) Flush();
            while (v >= 0x80)
            {
                _buf[_n++] = (byte)(v | 0x80);
                v >>= 7;
            }
            _buf[_n++] = (byte)v;
        }

        public void Bytes(ReadOnlySpan<byte> b)
        {
            Flush();
            s.Write(b);
            _flushed += b.Length;
        }

        public void Blob(byte[] b)
        {
            U((ulong)b.Length);
            Bytes(b);
        }

        public void Str(string v) => Blob(Encoding.UTF8.GetBytes(v));

        public void Flush()
        {
            s.Write(_buf, 0, _n);
            _flushed += _n;
            _n = 0;
        }
    }

    // ------------------------------------------------------------------- read

    /// <summary>Reads and checks a cache file; null when it is missing, from another version or corrupt.</summary>
    public static IndexCacheEntry? Read(string file)
    {
        try
        {
            return ReadCore(file);
        }
        catch (AggregateException e) when (e.InnerExceptions.All(x => x is InvalidDataException or IndexOutOfRangeException or ArgumentException or OverflowException))
        {
            return null; // from the parallel section decoding
        }
    }

    private static IndexCacheEntry? ReadCore(string file)
    {
        byte[] data;
        try
        {
            if (!File.Exists(file)) return null;
            data = File.ReadAllBytes(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        try
        {
            return Decode(data);
        }
        catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentException or OverflowException or OutOfMemoryException)
        {
            return null;
        }
    }

    private static IndexCacheEntry Decode(byte[] d)
    {
        var p = 0;
        ulong U()
        {
            ulong v = 0;
            for (var shift = 0; ; shift += 7)
            {
                if (shift > 63) throw new InvalidDataException("bad varint");
                var b = d[p++];
                v |= (ulong)(b & 0x7f) << shift;
                if (b < 0x80) return v;
            }
        }
        int I(long max)
        {
            var v = U();
            if (v > (ulong)max) throw new InvalidDataException("value out of range");
            return (int)v;
        }
        byte[] Blob()
        {
            var n = I(1 << 20);
            var b = d.AsSpan(p, n).ToArray();
            p += n;
            return b;
        }
        string Str() => Encoding.UTF8.GetString(Blob());

        if (!d.AsSpan(0, Magic.Length).SequenceEqual(Magic)) throw new InvalidDataException("not an index cache file");
        p = Magic.Length;
        if (U() != Version) throw new InvalidDataException("other version");
        var key = new IndexCacheKey
        {
            Path = Str(),
            Size = (long)U(),
            MtimeTicks = (long)U(),
            HeadLen = I(HashBytes),
            HeadHash = Blob(),
            TailLen = I(HashBytes),
            TailHash = Blob(),
            FormatOption = Str(),
            EncodingOption = Str(),
            CustomKey = Str(),
        };
        var formatId = Str();
        var encodingName = Str();
        var dataStart = I(4);
        var auto = U() == 1;
        var matched = I(int.MaxValue);
        var sampled = I(int.MaxValue);
        var dataEnd = (long)U();
        var lines = I(int.MaxValue);
        var entries = I(lines);
        if (dataEnd > key.Size || dataEnd < dataStart || lines > 0 && entries == 0) throw new InvalidDataException("bad header");
        // Every line takes at least one byte, so impossible counts fail before anything is allocated.
        if (lines > dataEnd - dataStart + 1 || d.Length - p < lines + 2L * entries) throw new InvalidDataException("bad counts");

        // The bulk of the file: four sections decoded in parallel, each with a tight loop over a span.
        var tableAt = d.Length - Magic.Length - SectionTable;
        if (tableAt < p || !d.AsSpan(d.Length - Magic.Length).SequenceEqual(Magic)) throw new InvalidDataException("bad trailer");
        Span<long> sec = stackalloc long[5];
        for (var i = 0; i < 5; i++) sec[i] = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(tableAt + i * 8));
        if (sec[0] != p || sec[4] != tableAt || sec[3] - sec[2] != entries) throw new InvalidDataException("bad section table");
        for (var i = 1; i < 5; i++)
            if (sec[i] < sec[i - 1]) throw new InvalidDataException("bad section table");
        var lineStarts = new long[lines];
        var entryLines = new int[entries];
        var levels = d.AsSpan((int)sec[2], entries).ToArray();
        var times = new long[entries];
        var s0 = (int)sec[0];
        var s1 = (int)sec[1];
        var s2 = (int)sec[2];
        var s3 = (int)sec[3];
        Parallel.Invoke(
            () =>
            {
                var span = d.AsSpan(0, s1);
                var q = s0;
                long prev = 0;
                for (var i = 0; i < lineStarts.Length; i++)
                {
                    var v = prev + (long)Var(span, ref q);
                    if (i > 0 ? v <= prev : v != dataStart) throw new InvalidDataException("line starts out of order");
                    lineStarts[i] = prev = v;
                }
                if (q != s1) throw new InvalidDataException("bad line section");
                if (lineStarts.Length > 0 && lineStarts[^1] >= Math.Max(dataEnd, dataStart + 1)) throw new InvalidDataException("line start past the data");
            },
            () =>
            {
                var span = d.AsSpan(0, s2);
                var q = s1;
                var prev = 0L;
                for (var i = 0; i < entryLines.Length; i++)
                {
                    var v = prev + (long)Var(span, ref q);
                    if (i > 0 ? v <= prev : v != 0) throw new InvalidDataException("entry lines out of order");
                    if (v >= lines) throw new InvalidDataException("entry line past the end");
                    entryLines[i] = (int)(prev = v);
                }
                if (q != span.Length) throw new InvalidDataException("bad entry section");
                if (levels.AsSpan().IndexOfAnyInRange((byte)LevelNames.Count, byte.MaxValue) >= 0) throw new InvalidDataException("bad level");
            },
            () =>
            {
                var span = d.AsSpan(0, tableAt);
                var q = s3;
                long prev = 0;
                for (var i = 0; i < times.Length; i++)
                {
                    var z = Var(span, ref q);
                    times[i] = prev = unchecked(prev + ((long)(z >> 1) ^ -(long)(z & 1)));
                }
                if (q != tableAt) throw new InvalidDataException("bad time section");
            });
        return new IndexCacheEntry
        {
            Key = key,
            FormatId = formatId,
            EncodingName = encodingName,
            DataStart = dataStart,
            DetectAuto = auto,
            DetectMatched = matched,
            DetectSampled = sampled,
            DataEnd = dataEnd,
            LineStarts = lineStarts,
            EntryLines = entryLines,
            Levels = levels,
            Times = times,
        };
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static ulong Var(ReadOnlySpan<byte> d, ref int p)
    {
        var b = d[p++];
        if (b < 0x80) return b;
        var v = b & 0x7fUL;
        for (var shift = 7; ; shift += 7)
        {
            if (shift > 63) throw new InvalidDataException("bad varint");
            b = d[p++];
            v |= (ulong)(b & 0x7f) << shift;
            if (b < 0x80) return v;
        }
    }

    // --------------------------------------------------------------- upkeep

    /// <summary>Marks a cache file as recently used, so eviction keeps it.</summary>
    public static void Touch(string file)
    {
        try { File.SetLastWriteTimeUtc(file, DateTime.UtcNow); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Deletes the least recently used cache files until the total is at most <paramref name="maxBytes"/>.</summary>
    public static void Evict(string dir, long maxBytes)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            var files = new DirectoryInfo(dir).GetFiles("*.idx").OrderBy(f => f.LastWriteTimeUtc).ToList();
            var total = files.Sum(f => f.Length);
            foreach (var f in files)
            {
                if (total <= maxBytes) break;
                try
                {
                    var n = f.Length;
                    f.Delete();
                    total -= n;
                }
                catch (IOException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Total size of the cache files.</summary>
    public static long Size(string dir)
    {
        try
        {
            return Directory.Exists(dir) ? new DirectoryInfo(dir).GetFiles("*.idx").Sum(f => f.Length) : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>Deletes every cache file; returns the bytes freed.</summary>
    public static long Clear(string dir)
    {
        long freed = 0;
        try
        {
            if (!Directory.Exists(dir)) return 0;
            foreach (var f in new DirectoryInfo(dir).GetFiles())
            {
                if (f.Extension is not (".idx" or ".tmp")) continue;
                try
                {
                    var n = f.Length;
                    f.Delete();
                    freed += n;
                }
                catch (IOException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return freed;
    }
}
