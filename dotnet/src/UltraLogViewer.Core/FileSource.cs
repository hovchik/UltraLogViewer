using System.Buffers;
using System.IO.Compression;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace UltraLogViewer.Core;

public sealed record RefreshResult(bool Changed, bool Reset, int FirstChangedEntry);

public sealed class SourceOptions
{
    /// <summary>"auto", "utf-8", "windows-1252", "utf-16le" or "utf-16be".</summary>
    public string Encoding { get; init; } = "auto";
    public IReadOnlyList<CustomFormat> CustomFormats { get; set; } = [];
    public string? Format { get; set; }
    public int ChunkSize { get; init; } = 4 * 1024 * 1024;
}

/// <summary>
/// A single log file: byte-offset line index, entry index (multi-line grouping with level and
/// timestamp per entry), on-demand reads and incremental tail refresh. Only the index lives in
/// memory, so files much larger than RAM open fine.
/// </summary>
public sealed class FileSource : IDisposable
{
    private const int MaxLineBytes = 64 * 1024 * 1024;

    static FileSource() => System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private readonly Grow<long> _lineStart = new(1 << 14);
    private readonly Grow<int> _entryLine = new(1 << 14);
    private readonly Grow<byte> _entryLevel = new(1 << 14);
    private readonly Grow<long> _entryTime = new(1 << 14);
    private readonly SourceOptions _opts;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private string _readPath;
    private SafeFileHandle? _handle;
    private string? _tempFile;
    private long _dataStart;
    private long _lastTime = Timestamps.None;
    private Encoding _decoder = new UTF8Encoding(false, false);
    /// <summary>First bytes of the file, used to detect that it was replaced (rotated) under the same name.</summary>
    private byte[] _head = [];

    public FileSource(int id, string path, SourceOptions? opts = null)
    {
        Id = id;
        Path = path;
        _readPath = path;
        _opts = opts ?? new SourceOptions();
    }

    public int Id { get; }
    public string Path { get; }
    public LogFormat Format => Detection.Format;
    public FormatDetection Detection { get; private set; } = new(new PlainFormat(), true, 0, 0);
    public string EncodingName { get; private set; } = "utf-8";
    public bool Compressed { get; private set; }
    public bool Live => _tempFile is null;
    public bool Closed { get; private set; }
    /// <summary>Byte offset just after the last indexed line.</summary>
    public long DataEnd { get; private set; }
    public int LineCount => _lineStart.Count;
    public int EntryCount => _entryLine.Count;
    public Level LevelOf(int entry) => (Level)_entryLevel[entry];
    public long TimeOf(int entry) => _entryTime[entry];
    public ReadOnlySpan<byte> Levels => _entryLevel.Span;
    public ReadOnlySpan<long> Times => _entryTime.Span;

    // Level counts and time range of entries [0, _statsCount), extended lazily so Info() stays cheap while indexing.
    private readonly int[] _levelCounts = new int[LevelNames.Count];
    private long _minTime = long.MaxValue, _maxTime = long.MinValue;
    private int _statsCount;

    /// <summary>Entries per level and the time range (Min &gt; Max without timestamps), updated incrementally.</summary>
    public (int[] LevelCounts, long Min, long Max) Stats()
    {
        lock (_sync)
        {
            var count = EntryCount;
            if (_statsCount > count) ResetStats();
            var levels = _entryLevel.Span;
            var times = _entryTime.Span;
            for (var e = _statsCount; e < count; e++)
            {
                _levelCounts[levels[e]]++;
                var t = times[e];
                if (t == Timestamps.None) continue;
                if (t < _minTime) _minTime = t;
                if (t > _maxTime) _maxTime = t;
            }
            _statsCount = count;
            return ((int[])_levelCounts.Clone(), _minTime, _maxTime);
        }
    }

    private void ResetStats()
    {
        Array.Clear(_levelCounts);
        _minTime = long.MaxValue;
        _maxTime = long.MinValue;
        _statsCount = 0;
    }

    /// <summary>Takes entries [first, ...) out of the stats before they are removed from the index (caller holds _sync).</summary>
    private void UnfoldStats(int first)
    {
        if (first >= _statsCount) return;
        for (var e = first; e < _statsCount; e++)
        {
            var t = _entryTime[e];
            if (t != Timestamps.None && (t <= _minTime || t >= _maxTime))
            {
                ResetStats(); // the range may shrink: recompute it on the next call
                return;
            }
            _levelCounts[_entryLevel[e]]--;
        }
        _statsCount = first;
    }

    public SourceInfo Info() => new(Id, Path, System.IO.Path.GetFileName(Path), DataEnd, LineCount, EntryCount, Format.Name, EncodingName, Compressed, Live,
        Format.Id, FormatDetector.Label(Format), Detection.Auto, Detection.Matched, Detection.Sampled);

    /// <summary>Prepares the file (decompress / transcode if needed), detects the format and indexes it.</summary>
    public async Task OpenAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var head = ReadHead(Path, 4);
        if (head.Length >= 2 && head[0] == 0x1f && head[1] == 0x8b)
        {
            Compressed = true;
            _tempFile = await GunzipToTempAsync(Path, progress, ct);
            _readPath = _tempFile;
        }
        var detected = DetectEncoding(_readPath);
        // A forced encoding still skips its own byte-order mark.
        var (enc, bom) = _opts.Encoding is "auto" or "" ? detected : (_opts.Encoding, detected.Encoding == _opts.Encoding ? detected.Bom : 0);
        EncodingName = enc;
        if (enc is "utf-16le" or "utf-16be")
        {
            var tmp = await TranscodeToUtf8Async(_readPath, enc, bom, ct);
            if (_tempFile is not null) File.Delete(_tempFile);
            _tempFile = tmp;
            _readPath = tmp;
            _dataStart = 0;
            _decoder = new UTF8Encoding(false, false);
        }
        else
        {
            _dataStart = bom;
            _decoder = enc == "windows-1252" ? System.Text.Encoding.GetEncoding(1252) : new UTF8Encoding(false, false);
        }
        _handle = File.OpenHandle(_readPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var size = RandomAccess.GetLength(_handle);
        Detection = FormatDetector.DetectWithInfo(SampleLines(256 * 1024), File.GetLastWriteTime(_readPath).Year, _opts.CustomFormats, _opts.Format);
        DataEnd = _dataStart;
        await _gate.WaitAsync(ct);
        try
        {
            await Task.Run(() => IndexFrom(size, progress, ct), ct);
            _head = ReadHead(Path, 256);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Re-indexes with a different format id (null = detect again).</summary>
    public async Task SetFormatAsync(string? formatId, IReadOnlyList<CustomFormat>? custom = null)
    {
        _opts.Format = formatId;
        if (custom is not null) _opts.CustomFormats = custom;
        await _gate.WaitAsync();
        try
        {
            var size = RandomAccess.GetLength(_handle!);
            Detection = FormatDetector.DetectWithInfo(SampleLines(256 * 1024), File.GetLastWriteTime(_readPath).Year, _opts.CustomFormats, formatId);
            ResetIndex();
            await Task.Run(() => IndexFrom(size, null, default));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Picks up appended data, truncation or replacement (log rotation).</summary>
    public async Task<RefreshResult> RefreshAsync()
    {
        if (!Live || Closed || _handle is null) return new(false, false, EntryCount);
        await _gate.WaitAsync();
        try
        {
            long size;
            bool replaced;
            try
            {
                size = new FileInfo(Path).Length;
                var head = ReadHead(Path, _head.Length);
                replaced = !head.AsSpan().SequenceEqual(_head);
            }
            catch (IOException)
            {
                return new(false, false, EntryCount);
            }
            if (replaced || size < DataEnd)
            {
                // Rotated or truncated: start over on the new file.
                lock (_sync)
                {
                    _handle.Dispose();
                    _handle = File.OpenHandle(_readPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    ResetIndex();
                }
                await Task.Run(() => IndexFrom(RandomAccess.GetLength(_handle), null, default));
                _head = ReadHead(Path, 256);
                return new(true, true, 0);
            }
            if (size == DataEnd) return new(false, false, EntryCount);
            // Roll back the last entry: it may have been an unterminated line or may gain continuation lines.
            var first = Math.Max(0, EntryCount - 1);
            lock (_sync)
            {
                if (EntryCount > 0)
                {
                    var line = _entryLine[first];
                    UnfoldStats(first);
                    DataEnd = _lineStart[line];
                    _lineStart.Truncate(line);
                    _entryLine.Truncate(first);
                    _entryLevel.Truncate(first);
                    _entryTime.Truncate(first);
                    _lastTime = first > 0 ? _entryTime[first - 1] : Timestamps.None;
                }
            }
            await Task.Run(() => IndexFrom(size, null, default));
            if (_head.Length < 256) _head = ReadHead(Path, 256);
            return new(true, false, first);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ResetIndex()
    {
        lock (_sync)
        {
            ResetStats();
            _lineStart.Truncate(0);
            _entryLine.Truncate(0);
            _entryLevel.Truncate(0);
            _entryTime.Truncate(0);
            DataEnd = _dataStart;
            _lastTime = Timestamps.None;
        }
    }

    private List<string> SampleLines(int bytes)
    {
        var len = (int)Math.Min(bytes, Math.Max(0, RandomAccess.GetLength(_handle!) - _dataStart));
        var buf = new byte[len];
        var n = RandomAccess.Read(_handle!, buf, _dataStart);
        var lines = _decoder.GetString(buf, 0, n).Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        if (n == bytes && lines.Count > 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    /// <summary>
    /// Line starts and entry starts found in one chunk; line numbers are relative to the chunk. The arrays come from
    /// the shared pool (they are large enough to land on the large object heap) and go back after the commit.
    /// </summary>
    private sealed class ChunkScan
    {
        public readonly long[] Starts;
        public readonly int[] EntryLines;
        public readonly byte[] EntryLevels;
        public readonly long[] EntryTimes;
        public int Lines;
        public int Entries;
        /// <summary>Level and time of line 0 if it had to be forced to start an entry (only used for the first entry of the file).</summary>
        public QuickInfo FirstLine;

        public ChunkScan(int maxLines)
        {
            Starts = ArrayPool<long>.Shared.Rent(maxLines);
            EntryLines = ArrayPool<int>.Shared.Rent(maxLines);
            EntryLevels = ArrayPool<byte>.Shared.Rent(maxLines);
            EntryTimes = ArrayPool<long>.Shared.Rent(maxLines);
        }

        public void AddEntry(int line, QuickInfo q)
        {
            EntryLines[Entries] = line;
            EntryLevels[Entries] = (byte)q.Level;
            EntryTimes[Entries++] = q.Time;
        }

        public void Return()
        {
            ArrayPool<long>.Shared.Return(Starts);
            ArrayPool<int>.Shared.Return(EntryLines);
            ArrayPool<byte>.Shared.Return(EntryLevels);
            ArrayPool<long>.Shared.Return(EntryTimes);
        }
    }

    /// <summary>
    /// Indexes [DataEnd, size). Chunks are read in order on this thread, scanned in parallel on the thread pool
    /// (the per-line format scan is the expensive part) and committed in file order, so rows still appear
    /// progressively from the top while the rest of the file is being indexed.
    /// </summary>
    private void IndexFrom(long size, IProgress<double>? progress, CancellationToken ct)
    {
        var fmt = Format;
        var utf8 = _decoder is UTF8Encoding;
        var decoder = _decoder;
        var chunkSize = _opts.ChunkSize;
        var pos = DataEnd;
        var startPos = pos;
        var maxInFlight = Math.Max(2, Environment.ProcessorCount * 2);
        var inFlight = new Queue<(Task<ChunkScan> Task, byte[] Buf, long End)>();
        try
        {
            while (pos < size && !Closed)
            {
                ct.ThrowIfCancellationRequested();
                var want = (int)Math.Min(chunkSize, size - pos);
                var buf = ArrayPool<byte>.Shared.Rent(want);
                var n = ReadFully(buf.AsSpan(0, want), pos);
                if (n <= 0)
                {
                    ArrayPool<byte>.Shared.Return(buf);
                    break;
                }
                var eof = pos + n >= size;
                var end = buf.AsSpan(0, n).LastIndexOf((byte)'\n') + 1; // bytes up to and including the last '\n'
                if (end == 0 && !eof && chunkSize < MaxLineBytes)
                {
                    // A line longer than the chunk: read again with a bigger one.
                    ArrayPool<byte>.Shared.Return(buf);
                    chunkSize = Math.Min(MaxLineBytes, chunkSize * 2);
                    continue;
                }
                if (end == 0) end = n; // the unterminated tail, or a pathological line split at MaxLineBytes
                else if (eof && end < n) end = n; // include the unterminated last line
                var chunkPos = pos;
                var chunkLen = end;
                inFlight.Enqueue((Task.Run(() => ScanChunk(fmt, decoder, utf8, buf, chunkLen, chunkPos), ct), buf, pos + end));
                pos += end;
                while (inFlight.Count >= maxInFlight || inFlight.Count > 0 && inFlight.Peek().Task.IsCompleted)
                    Commit(inFlight.Dequeue(), startPos, size, progress);
            }
            while (inFlight.Count > 0) Commit(inFlight.Dequeue(), startPos, size, progress);
        }
        finally
        {
            // On cancellation or error: let the remaining scans finish before their buffers go back to the pool.
            foreach (var (task, buf, _) in inFlight)
            {
                try { task.Wait(CancellationToken.None); } catch (AggregateException) { }
                ArrayPool<byte>.Shared.Return(buf);
            }
        }
    }

    private int ReadFully(Span<byte> dest, long offset)
    {
        var got = 0;
        while (got < dest.Length)
        {
            var n = RandomAccess.Read(_handle!, dest[got..], offset + got);
            if (n <= 0) break;
            got += n;
        }
        return got;
    }

    private void Commit((Task<ChunkScan> Task, byte[] Buf, long End) item, long startPos, long size, IProgress<double>? progress)
    {
        ChunkScan c;
        try
        {
            c = item.Task.GetAwaiter().GetResult();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(item.Buf);
        }
        try
        {
            lock (_sync)
            {
                if (Closed) return;
                var lineBase = LineCount;
                // The file's first line starts an entry even when it does not look like one.
                if (EntryCount == 0 && c.Lines > 0 && (c.Entries == 0 || c.EntryLines[0] != 0))
                    AddEntry(lineBase, c.FirstLine);
                if (_lineStart.Count == 0 && c.Lines > 0 && item.End < size)
                {
                    // Size the index once from the first chunk instead of doubling it a dozen times.
                    var perLine = (double)(item.End - startPos) / c.Lines;
                    var lines = (int)Math.Min(int.MaxValue / 2, (size - startPos) / perLine * 1.05);
                    _lineStart.EnsureCapacity(lines);
                    var entries = (int)Math.Min(lines, (long)lines * Math.Max(1, c.Entries) / c.Lines);
                    _entryLine.EnsureCapacity(entries);
                    _entryLevel.EnsureCapacity(entries);
                    _entryTime.EnsureCapacity(entries);
                }
                var times = c.EntryTimes.AsSpan(0, c.Entries);
                for (var e = 0; e < times.Length; e++)
                {
                    if (times[e] != Timestamps.None) _lastTime = times[e];
                    else times[e] = _lastTime;
                }
                var entryLines = c.EntryLines.AsSpan(0, c.Entries);
                for (var e = 0; e < entryLines.Length; e++) entryLines[e] += lineBase;
                _lineStart.AddRange(c.Starts.AsSpan(0, c.Lines));
                // EntryCount comes from _entryLine, so it is published last.
                _entryLevel.AddRange(c.EntryLevels.AsSpan(0, c.Entries));
                _entryTime.AddRange(times);
                _entryLine.AddRange(entryLines);
                DataEnd = item.End;
            }
        }
        finally
        {
            c.Return();
        }
        if (size > startPos) progress?.Report((double)(item.End - startPos) / (size - startPos));
    }

    /// <summary>Appends an entry; one without a timestamp inherits the previous entry's time.</summary>
    private void AddEntry(int line, QuickInfo q)
    {
        if (q.Time != Timestamps.None) _lastTime = q.Time;
        _entryLine.Add(line);
        _entryLevel.Add((byte)q.Level);
        _entryTime.Add(q.Time != Timestamps.None ? q.Time : _lastTime);
    }

    /// <summary>Finds line starts and entry starts in buf[0, len), which ends at a line boundary (or the end of the data).</summary>
    private static ChunkScan ScanChunk(LogFormat fmt, Encoding decoder, bool utf8, byte[] buf, int len, long pos)
    {
        var bytes = buf.AsSpan(0, len);
        var c = new ChunkScan(bytes.Count((byte)'\n') + 1);
        var byteScan = utf8 && fmt.HasUtf8Scan;
        char[]? chars = null;
        var text = ReadOnlySpan<char>.Empty;
        if (!byteScan)
        {
            chars = ArrayPool<char>.Shared.Rent(Math.Max(1, decoder.GetMaxCharCount(len)));
            text = chars.AsSpan(0, decoder.GetChars(bytes, chars));
        }
        try
        {
            var off = 0;
            var ci = 0;
            while (off < len)
            {
                var bnl = bytes[off..].IndexOf((byte)'\n');
                var lineEnd = bnl < 0 ? len : off + bnl;
                var lineBytes = bytes[off..(lineEnd > off && bytes[lineEnd - 1] == '\r' ? lineEnd - 1 : lineEnd)];
                bool isStart;
                QuickInfo q;
                ReadOnlySpan<char> line;
                if (byteScan)
                {
                    line = default;
                    isStart = fmt.ScanUtf8(lineBytes, out q) ?? fmt.Scan(decoder.GetString(lineBytes), out q);
                }
                else
                {
                    var rest = text[ci..];
                    var nl = rest.IndexOf('\n');
                    var cl = nl < 0 ? rest.Length : nl;
                    line = rest[..(cl > 0 && rest[cl - 1] == '\r' ? cl - 1 : cl)];
                    ci += nl < 0 ? rest.Length : nl + 1;
                    isStart = fmt.ScanSpan(line, out q) ?? fmt.Scan(line.ToString(), out q);
                }
                if (!isStart && c.Lines == 0)
                    c.FirstLine = fmt.Quick(byteScan ? decoder.GetString(lineBytes) : line.ToString());
                if (isStart) c.AddEntry(c.Lines, q);
                c.Starts[c.Lines++] = pos + off;
                off = bnl < 0 ? len : lineEnd + 1;
            }
            return c;
        }
        finally
        {
            if (chars is not null) ArrayPool<char>.Shared.Return(chars);
        }
    }

    /// <summary>Line range [first, last] (0-based, inclusive) of an entry.</summary>
    public (int First, int Last) EntryLines(int entry)
    {
        lock (_sync)
        {
            var count = EntryCount;
            if (entry >= count) return (0, -1);
            var first = _entryLine[entry];
            var last = entry + 1 < count ? _entryLine[entry + 1] - 1 : LineCount - 1;
            return (first, last);
        }
    }

    /// <summary>Entry containing a 0-based line.</summary>
    public int EntryForLine(int line)
    {
        lock (_sync)
        {
            var e = Search.LowerBound(0, EntryCount, i => _entryLine[i] > line) - 1;
            return Math.Max(0, e);
        }
    }

    /// <summary>Texts of entries [from, to). Continuation lines are joined with "\n".</summary>
    public string[] ReadEntries(int from, int to)
    {
        long start, end;
        int[] counts;
        lock (_sync)
        {
            var entryCount = EntryCount;
            to = Math.Min(to, entryCount);
            if (from >= to || _handle is null) return [];
            var firstLine = _entryLine[from];
            var endLine = to < entryCount ? _entryLine[to] : LineCount;
            start = firstLine < LineCount ? _lineStart[firstLine] : DataEnd;
            end = endLine < LineCount ? _lineStart[endLine] : DataEnd;
            counts = new int[to - from];
            for (var e = from; e < to; e++)
                counts[e - from] = (e + 1 < entryCount ? _entryLine[e + 1] : LineCount) - _entryLine[e];
        }
        // Decoded straight from the (pooled) bytes, line by line: no big intermediate string or line array, which kept
        // the garbage collector busier than the actual matching when every core reads at once.
        var len = (int)(end - start);
        var buf = ArrayPool<byte>.Shared.Rent(Math.Max(1, len));
        try
        {
            var bytes = buf.AsSpan(0, ReadFully(buf.AsSpan(0, len), start));
            var output = new string[counts.Length];
            var pos = 0;
            var more = true; // like string.Split: one more (possibly empty) line follows the last '\n'
            for (var i = 0; i < counts.Length; i++)
            {
                if (!more)
                {
                    output[i] = ""; // the file shrank under us
                    continue;
                }
                var from0 = pos;
                var taken = 0;
                for (; taken < counts[i] && more; taken++)
                {
                    var nl = bytes[pos..].IndexOf((byte)'\n');
                    if (nl < 0)
                    {
                        pos = bytes.Length;
                        more = false;
                    }
                    else pos += nl + 1;
                }
                var range = bytes[from0..(more ? pos - 1 : pos)];
                // Each line loses its trailing '\r's (a byte that only ever means '\r' in UTF-8 and Windows-1252).
                if (taken == 1) output[i] = _decoder.GetString(range.TrimEnd((byte)'\r'));
                else if (range.IndexOf((byte)'\r') < 0) output[i] = _decoder.GetString(range);
                else output[i] = JoinLines(range);
            }
            return output;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    /// <summary>Decodes a multi-line entry that has '\r's in it: every line is trimmed of them, then joined with '\n'.</summary>
    private string JoinLines(ReadOnlySpan<byte> range)
    {
        var sb = new StringBuilder(range.Length);
        while (true)
        {
            var nl = range.IndexOf((byte)'\n');
            sb.Append(_decoder.GetString((nl < 0 ? range : range[..nl]).TrimEnd((byte)'\r')));
            if (nl < 0) return sb.ToString();
            sb.Append('\n');
            range = range[(nl + 1)..];
        }
    }

    /// <summary>Calls <paramref name="fn"/> with batches of entry texts in [from, to); return false to stop.</summary>
    public void ForEachEntryBatch(int from, int to, Func<int, string[], bool> fn, CancellationToken ct = default, int batch = 4096)
    {
        for (var e = from; e < to && !Closed; e += batch)
        {
            ct.ThrowIfCancellationRequested();
            var texts = ReadEntries(e, Math.Min(to, e + batch));
            if (!fn(e, texts)) return;
        }
    }

    /// <summary>
    /// Like <see cref="ForEachEntryBatch"/>, but on all cores: batches are read and handed to <paramref name="work"/> on
    /// the thread pool, several at a time, and their results go to <paramref name="commit"/> on the calling thread in
    /// entry order, so whatever commit builds comes out exactly as a sequential pass would build it. Commit gets the
    /// first entry, the entry count and the result; it returns false to stop. <paramref name="work"/> must be thread safe.
    /// </summary>
    public void ForEachEntryBatchParallel<T>(int from, int to, Func<int, string[], T> work, Func<int, int, T, bool> commit, CancellationToken ct = default, int batch = 4096,
        bool parallel = true)
    {
        if (from >= to) return;
        InOrder.Run((int)(((long)to - from + batch - 1) / batch), i =>
        {
            var first = from + i * batch;
            var texts = Closed ? [] : ReadEntries(first, Math.Min(to, first + batch));
            return (First: first, Count: texts.Length, Result: work(first, texts));
        }, (_, r) => !Closed && commit(r.First, r.Count, r.Result), ct, parallel);
    }

    public void Dispose()
    {
        Closed = true;
        lock (_sync)
        {
            _handle?.Dispose();
            _handle = null;
        }
        if (_tempFile is not null)
        {
            try { File.Delete(_tempFile); } catch (IOException) { }
        }
    }

    // ------------------------------------------------------------------ helpers

    private static byte[] ReadHead(string file, int n)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var b = new byte[n];
        var got = fs.Read(b, 0, n);
        return b[..got];
    }

    public static (string Encoding, int Bom) DetectEncoding(string file)
    {
        const int SampleSize = 64 * 1024;
        var b = ReadHead(file, SampleSize);
        if (b.Length >= 3 && b[0] == 0xef && b[1] == 0xbb && b[2] == 0xbf) return ("utf-8", 3);
        if (b.Length >= 2 && b[0] == 0xff && b[1] == 0xfe) return ("utf-16le", 2);
        if (b.Length >= 2 && b[0] == 0xfe && b[1] == 0xff) return ("utf-16be", 2);
        if (b.Length >= 4)
        {
            int zEven = 0, zOdd = 0;
            var n = Math.Min(b.Length, 4096) & ~1;
            for (var i = 0; i < n; i += 2)
            {
                if (b[i] == 0) zEven++;
                if (b[i + 1] == 0) zOdd++;
            }
            if (zOdd > n / 8 && zEven < n / 64) return ("utf-16le", 0);
            if (zEven > n / 8 && zOdd < n / 64) return ("utf-16be", 0);
        }
        // When the sample stops mid-file, drop a multi-byte sequence the cut left incomplete.
        // A complete file is validated whole, so a trailing Windows-1252 byte is not ignored.
        var end = b.Length;
        if (b.Length == SampleSize)
        {
            var i = end - 1;
            var cont = 0;
            while (i >= 0 && cont < 3 && (b[i] & 0xc0) == 0x80)
            {
                i--;
                cont++;
            }
            if (i >= 0 && b[i] >= 0xc0)
            {
                var need = b[i] >= 0xf0 ? 4 : b[i] >= 0xe0 ? 3 : 2;
                if (cont + 1 < need) end = i;
            }
        }
        try
        {
            new UTF8Encoding(false, true).GetString(b, 0, end);
            return ("utf-8", 0);
        }
        catch (DecoderFallbackException)
        {
            return ("windows-1252", 0);
        }
    }

    private static string TempPath(string src, string ext) =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ultralogviewer-{Environment.ProcessId}-{Guid.NewGuid():N}-{System.IO.Path.GetFileName(src)}{ext}");

    private static async Task<string> GunzipToTempAsync(string file, IProgress<double>? progress, CancellationToken ct)
    {
        var output = TempPath(file, ".log");
        await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        await using var gz = new GZipStream(input, CompressionMode.Decompress);
        await using var outStream = File.Create(output);
        var buf = new byte[1 << 20];
        int n;
        while ((n = await gz.ReadAsync(buf, ct)) > 0)
        {
            await outStream.WriteAsync(buf.AsMemory(0, n), ct);
            progress?.Report(Math.Min(0.99, (double)input.Position / Math.Max(1, input.Length)) * 0.5);
        }
        return output;
    }

    private static async Task<string> TranscodeToUtf8Async(string file, string enc, int bom, CancellationToken ct)
    {
        var output = TempPath(file, ".utf8.log");
        var source = enc == "utf-16le" ? new UnicodeEncoding(false, false) : new UnicodeEncoding(true, false);
        await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        input.Seek(bom, SeekOrigin.Begin);
        using var reader = new StreamReader(input, source, detectEncodingFromByteOrderMarks: false);
        await using var writer = new StreamWriter(output, false, new UTF8Encoding(false));
        var chars = new char[1 << 16];
        int n;
        while ((n = await reader.ReadAsync(chars.AsMemory(), ct)) > 0) await writer.WriteAsync(chars.AsMemory(0, n), ct);
        return output;
    }
}
