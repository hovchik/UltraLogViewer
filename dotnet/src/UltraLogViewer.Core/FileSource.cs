using System.Buffers;
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
    /// <summary>Folder for saved indexes; null turns the index cache off.</summary>
    public string? CacheDir { get; init; }
    /// <summary>Total size the index cache may use; the least recently used indexes are deleted beyond it.</summary>
    public long CacheMaxBytes { get; init; } = 2L * 1024 * 1024 * 1024;
    /// <summary>Smaller files index faster than a cache file loads: they are not cached.</summary>
    public long CacheMinBytes { get; init; } = 1024 * 1024;
    /// <summary>Open only the entries in this time window.</summary>
    public OpenWindow? Window { get; init; }
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
    /// <summary>Where indexing starts: the data start, or the first entry of an opened time window.</summary>
    private long _rangeStart;
    /// <summary>End of an opened time window that ends before the end of the file (null: index to the end and tail).</summary>
    private long? _rangeEnd;
    /// <summary>Bytes of the temp file written so far while a compressed file is decompressed in the background.</summary>
    private long _written;
    /// <summary>Chunks being scanned across all files, so indexing several files at once does not multiply the buffers.</summary>
    private static int _globalInFlight;

    public FileSource(int id, string path, SourceOptions? opts = null)
    {
        Id = id;
        Path = path;
        _readPath = path;
        _opts = opts ?? new SourceOptions();
    }

    /// <summary>Position in the document's source list (renumbered when a file of a merge fails to open).</summary>
    public int Id { get; internal set; }
    public string Path { get; }
    public LogFormat Format => Detection.Format;
    public FormatDetection Detection { get; private set; } = new(new PlainFormat(), true, 0, 0);
    public string EncodingName { get; private set; } = "utf-8";
    public bool Compressed { get; private set; }
    /// <summary>"gzip", "bzip2", "zip" or "" for a plain file.</summary>
    public string Compression { get; private set; } = "";
    public bool Live => _tempFile is null && _rangeEnd is null;
    public bool Indexing { get; private set; }
    /// <summary>Indexing progress of this file, 0..1.</summary>
    public double Progress { get; private set; }
    /// <summary>Only a time window of the file was indexed.</summary>
    public bool Partial { get; private set; }
    /// <summary>The requested window (epoch ms), when one was opened.</summary>
    public long? WindowFrom { get; private set; }
    public long? WindowTo { get; private set; }
    /// <summary>"hit" (the saved index was used as is), "grown" (saved index plus the new bytes) or "".</summary>
    public string CacheState { get; private set; } = "";
    /// <summary>The background save of the index cache started after opening, if any (tests wait for it).</summary>
    public Task? CacheSave { get; private set; }
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

    public SourceInfo Info() => new(Id, Path, Archives.DisplayName(Path), DataEnd, LineCount, EntryCount, Format.Name, EncodingName, Compressed, Live,
        Format.Id, FormatDetector.Label(Format), Detection.Auto, Detection.Matched, Detection.Sampled,
        Compression, Indexing, Indexing ? Progress : 1, Partial, WindowFrom, WindowTo, CacheState);

    /// <summary>
    /// Prepares the file (decompress / transcode if needed), detects the format and indexes it: from the index
    /// cache when it is still valid, only the requested time window when one is set, and a compressed file while
    /// it is still being decompressed, so its first rows show up right away.
    /// </summary>
    public async Task OpenAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Indexing = true;
        try
        {
            await OpenCoreAsync(new SyncProgress(f =>
            {
                Progress = f;
                progress?.Report(f);
            }), ct);
        }
        finally
        {
            Progress = 1;
            Indexing = false;
        }
    }

    private async Task OpenCoreAsync(IProgress<double> progress, CancellationToken ct)
    {
        Task? decompress = null;
        var stream = Archives.OpenDecompressed(Path, out var kind, out var total, out var position);
        if (stream is not null)
        {
            Compressed = true;
            Compression = kind;
            var temp = _tempFile = TempPath(Archives.DisplayName(Path), ".log");
            _readPath = temp;
            File.WriteAllBytes(temp, []);
            decompress = Task.Run(() => DecompressTo(stream, temp, total, position, progress, ct), ct);
            // Encoding and format detection look at the first 256 KB.
            while (!decompress.IsCompleted && Volatile.Read(ref _written) <= 256 * 1024) await Task.WhenAny(decompress, Task.Delay(15, ct));
            if (decompress.IsFaulted || decompress.IsCanceled) await decompress;
        }
        var detected = DetectEncoding(_readPath);
        // A forced encoding still skips its own byte-order mark.
        var (enc, bom) = _opts.Encoding is "auto" or "" ? detected : (_opts.Encoding, detected.Encoding == _opts.Encoding ? detected.Bom : 0);
        EncodingName = enc;
        if (enc is "utf-16le" or "utf-16be")
        {
            if (decompress is not null) await decompress; // transcoding needs the whole file
            decompress = null;
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
        var window = _opts.Window is { IsEmpty: false } w ? w : null;
        if (decompress is not null && window is not null)
        {
            await decompress; // a window is found by seeking around, which needs the whole file
            decompress = null;
        }
        _handle = File.OpenHandle(_readPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var size = RandomAccess.GetLength(_handle);
        Detection = FormatDetector.DetectWithInfo(SampleLines(256 * 1024), File.GetLastWriteTime(_readPath).Year, _opts.CustomFormats, _opts.Format);
        _rangeStart = _dataStart;
        DataEnd = _dataStart;
        await _gate.WaitAsync(ct);
        try
        {
            if (decompress is not null) await StreamIndexAsync(decompress, ct);
            else
            {
                var end = size;
                if (window is not null)
                {
                    (_rangeStart, end) = await Task.Run(() => ResolveWindow(size, window), ct);
                    DataEnd = _rangeStart;
                    _rangeEnd = window.Until is null ? null : end;
                    Partial = _rangeStart > _dataStart || end < size;
                }
                else if (CacheEligible) await Task.Run(() => TryLoadCache(size), ct);
                if (CacheState != "hit") await Task.Run(() => IndexFrom(end, progress, ct), ct);
            }
            if (Live) _head = ReadHead(Path, 256);
        }
        finally
        {
            _gate.Release();
        }
        if (CacheEligible && CacheState != "hit" && DataEnd - _dataStart >= _opts.CacheMinBytes) CacheSave = Task.Run(SaveCacheAsync);
    }

    /// <summary>Copies the decompressed data into the temp file the index reads from.</summary>
    private void DecompressTo(Stream input, string temp, long total, Func<long> position, IProgress<double> progress, CancellationToken ct)
    {
        using (input)
        using (var output = new FileStream(temp, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1))
        {
            var buf = new byte[1 << 20];
            int n;
            while ((n = input.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false)) > 0 && !Closed)
            {
                ct.ThrowIfCancellationRequested();
                output.Write(buf, 0, n);
                Volatile.Write(ref _written, _written + n);
                if (total > 0) progress.Report(Math.Min(0.99, (double)position() / total));
            }
        }
    }

    /// <summary>Indexes the temp file as it grows, until the decompression has finished.</summary>
    private async Task StreamIndexAsync(Task decompress, CancellationToken ct)
    {
        while (true)
        {
            var done = decompress.IsCompleted;
            var written = Volatile.Read(ref _written);
            if (written > DataEnd) await Task.Run(() => IndexFrom(written, null, ct, final: done), ct);
            if (done)
            {
                await decompress; // surfaces a decompression error
                return;
            }
            await Task.WhenAny(decompress, Task.Delay(30, ct));
        }
    }

    // ------------------------------------------------------------ time window

    /// <summary>Byte range [start, end) of the entries inside the window, found by binary search on timestamps.</summary>
    private (long Start, long End) ResolveWindow(long size, OpenWindow w)
    {
        var since = w.Since;
        if (since is null && w.Last is long last && NewestTime(size) is long newest) since = newest - last;
        WindowFrom = since;
        WindowTo = w.Until;
        var start = since is long s ? FirstEntryWhere(_dataStart, size, t => t >= s) : _dataStart;
        var end = w.Until is long u ? FirstEntryWhere(start, size, t => t > u) : size;
        return (start, Math.Max(start, end));
    }

    /// <summary>
    /// Start of the first entry with a timestamp for which <paramref name="pred"/> holds (it must be monotonic over the
    /// file), or <paramref name="size"/>. Probes narrow the range down to 1 MB, which is then scanned line by line.
    /// Results are always entry starts, so multi-line entries are never cut.
    /// </summary>
    private long FirstEntryWhere(long lo, long size, Func<long, bool> pred)
    {
        var hi = size;
        while (hi - lo > 1 << 20)
        {
            var mid = lo + (hi - lo) / 2;
            (long Pos, long Time)? e = null;
            ScanEntries(mid, hi, (pos, t) =>
            {
                e = (pos, t);
                return false;
            });
            if (e is not { } found || pred(found.Time)) hi = mid;
            else lo = found.Pos + 1;
        }
        var result = size;
        ScanEntries(lo, size, (pos, t) =>
        {
            if (!pred(t)) return true;
            result = pos;
            return false;
        });
        return result;
    }

    /// <summary>Time of the last entry with a timestamp, looking back up to 16 MB from the end.</summary>
    private long? NewestTime(long size)
    {
        for (long span = 1 << 20; ; span *= 4)
        {
            long? newest = null;
            ScanEntries(Math.Max(_dataStart, size - span), size, (_, t) =>
            {
                newest = t;
                return true;
            });
            if (newest is not null || size - span <= _dataStart || span >= 16 << 20) return newest;
        }
    }

    /// <summary>
    /// Calls <paramref name="visit"/> with the start and time of each timestamped entry that starts in [from, limit),
    /// beginning at the first full line at or after <paramref name="from"/>; stops when it returns false.
    /// </summary>
    private void ScanEntries(long from, long limit, Func<long, long, bool> visit)
    {
        var fmt = Format;
        var pos = from;
        if (pos > _dataStart)
        {
            // Not at a line start: skip the rest of the line.
            Span<byte> prev = stackalloc byte[1];
            if (RandomAccess.Read(_handle!, prev, pos - 1) == 1 && prev[0] != '\n')
            {
                var nl = FindByte(pos, (byte)'\n');
                if (nl < 0) return;
                pos = nl + 1;
            }
        }
        var size = RandomAccess.GetLength(_handle!);
        var block = 64 * 1024;
        var buf = new byte[block];
        while (pos < limit && pos < size)
        {
            if (buf.Length < block) buf = new byte[block];
            var n = ReadFully(buf.AsSpan(0, (int)Math.Min(block, size - pos)), pos);
            if (n <= 0) return;
            var eof = pos + n >= size;
            var span = buf.AsSpan(0, n);
            var off = 0;
            while (off < n)
            {
                var nl = span[off..].IndexOf((byte)'\n');
                if (nl < 0 && !eof) break;
                var lineEnd = nl < 0 ? n : off + nl;
                var lineStart = pos + off;
                if (lineStart >= limit) return;
                var line = span[off..(lineEnd > off && span[lineEnd - 1] == '\r' ? lineEnd - 1 : lineEnd)];
                if (fmt.Scan(_decoder.GetString(line), out var q) && q.Time != Timestamps.None && !visit(lineStart, q.Time)) return;
                off = nl < 0 ? n : lineEnd + 1;
            }
            if (off == 0)
            {
                if (block >= MaxLineBytes) return; // a pathological line: give up rather than read it whole
                block *= 2; // a line longer than the block
                continue;
            }
            pos += off;
        }
    }

    private long FindByte(long from, byte b)
    {
        var buf = new byte[64 * 1024];
        for (var pos = from; ;)
        {
            var n = RandomAccess.Read(_handle!, buf, pos);
            if (n <= 0) return -1;
            var i = buf.AsSpan(0, n).IndexOf(b);
            if (i >= 0) return pos + i;
            pos += n;
        }
    }

    // ------------------------------------------------------------ index cache

    private bool CacheEligible => _opts.CacheDir is not null && _tempFile is null && _opts.Window is not { IsEmpty: false } && Archives.SplitMember(Path) is null;

    private IndexCacheKey KeyFor(long size)
    {
        var n = (int)Math.Min(IndexCache.HashBytes, size);
        return new IndexCacheKey
        {
            Path = Path,
            Size = size,
            MtimeTicks = File.GetLastWriteTimeUtc(Path).Ticks,
            HeadLen = n,
            HeadHash = IndexCache.HashRange(_handle!, 0, n),
            TailLen = n,
            TailHash = IndexCache.HashRange(_handle!, size - n, n),
            FormatOption = _opts.Format ?? "",
            EncodingOption = _opts.Encoding,
            CustomKey = IndexCache.CustomFormatsKey(_opts.CustomFormats),
        };
    }

    /// <summary>Loads a saved index that is still valid for the file, as is or as the start of a grown file.</summary>
    private void TryLoadCache(long size)
    {
        var file = IndexCache.FileFor(_opts.CacheDir!, Path);
        var e = IndexCache.Read(file);
        if (e is null) return;
        var k = e.Key;
        if (k.Path != Path || k.FormatOption != (_opts.Format ?? "") || k.EncodingOption != _opts.Encoding || k.CustomKey != IndexCache.CustomFormatsKey(_opts.CustomFormats)) return;
        if (e.FormatId != Format.Id || e.EncodingName != EncodingName || e.DataStart != _dataStart || k.Size > size || e.DataEnd != k.Size) return;
        var headLen = (int)Math.Min(IndexCache.HashBytes, k.Size);
        if (k.HeadLen != headLen || !IndexCache.HashRange(_handle!, 0, headLen).AsSpan().SequenceEqual(k.HeadHash)) return;
        if (k.Size == size && k.MtimeTicks != File.GetLastWriteTimeUtc(Path).Ticks) return;
        if (k.TailLen > k.Size || !IndexCache.HashRange(_handle!, k.Size - k.TailLen, k.TailLen).AsSpan().SequenceEqual(k.TailHash)) return;
        lock (_sync)
        {
            _lineStart.Adopt(e.LineStarts);
            _entryLevel.Adopt(e.Levels);
            _entryTime.Adopt(e.Times);
            _entryLine.Adopt(e.EntryLines); // EntryCount comes from _entryLine, so it is published last
            DataEnd = e.DataEnd;
            _lastTime = e.Times.Length > 0 ? e.Times[^1] : Timestamps.None;
        }
        IndexCache.Touch(file);
        if (k.Size == size)
        {
            CacheState = "hit";
            return;
        }
        CacheState = "grown";
        RollbackLastEntry();
    }

    private async Task SaveCacheAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (Closed || _handle is null) return;
            var dir = _opts.CacheDir!;
            var size = DataEnd;
            var header = new IndexCacheEntry
            {
                Key = KeyFor(size),
                FormatId = Format.Id,
                EncodingName = EncodingName,
                DataStart = (int)_dataStart,
                DetectAuto = Detection.Auto,
                DetectMatched = Detection.Matched,
                DetectSampled = Detection.Sampled,
                DataEnd = size,
            };
            IndexCache.Write(IndexCache.FileFor(dir, Path), header, _lineStart.Span, _entryLine.Span, _entryLevel.Span, _entryTime.Span);
            IndexCache.Evict(dir, _opts.CacheMaxBytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // the cache is an optimisation: a failed save only means a full index next time
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
            await Task.Run(() => IndexFrom(_rangeEnd ?? size, null, default));
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
                    // A new file: an opened time window does not apply to it.
                    _rangeStart = _dataStart;
                    Partial = false;
                    ResetIndex();
                }
                await Task.Run(() => IndexFrom(RandomAccess.GetLength(_handle), null, default));
                _head = ReadHead(Path, 256);
                return new(true, true, 0);
            }
            if (size == DataEnd) return new(false, false, EntryCount);
            var first = RollbackLastEntry();
            await Task.Run(() => IndexFrom(size, null, default));
            if (_head.Length < 256) _head = ReadHead(Path, 256);
            return new(true, false, first);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Takes the last entry out of the index before more data is indexed: it may have been an unterminated line or may
    /// gain continuation lines. Returns the first entry that may change.
    /// </summary>
    private int RollbackLastEntry()
    {
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
        return first;
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
            DataEnd = _rangeStart;
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
    /// <param name="final">False while the file is still being written (decompressed): an unterminated last line is left for later.</param>
    private void IndexFrom(long size, IProgress<double>? progress, CancellationToken ct, bool final = true)
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
                if (end == 0 && eof && !final)
                {
                    ArrayPool<byte>.Shared.Return(buf); // the line is still being written
                    break;
                }
                if (end == 0 && !eof && chunkSize < MaxLineBytes)
                {
                    // A line longer than the chunk: read again with a bigger one.
                    ArrayPool<byte>.Shared.Return(buf);
                    chunkSize = Math.Min(MaxLineBytes, chunkSize * 2);
                    continue;
                }
                if (end == 0) end = n; // the unterminated tail, or a pathological line split at MaxLineBytes
                else if (eof && final && end < n) end = n; // include the unterminated last line
                var chunkPos = pos;
                var chunkLen = end;
                Interlocked.Increment(ref _globalInFlight);
                inFlight.Enqueue((Task.Run(() => ScanChunk(fmt, decoder, utf8, buf, chunkLen, chunkPos), ct), buf, pos + end));
                pos += end;
                // Files indexed at the same time share the in-flight budget; one with nothing in flight always proceeds.
                while (inFlight.Count >= maxInFlight || inFlight.Count > 0 && (inFlight.Peek().Task.IsCompleted || Volatile.Read(ref _globalInFlight) >= maxInFlight))
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
                Interlocked.Decrement(ref _globalInFlight);
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
            Interlocked.Decrement(ref _globalInFlight);
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
        var buf = new byte[end - start];
        var got = 0;
        while (got < buf.Length)
        {
            var n = RandomAccess.Read(_handle!, buf.AsSpan(got), start + got);
            if (n <= 0) break;
            got += n;
        }
        var lines = _decoder.GetString(buf, 0, got).Split('\n');
        var output = new string[counts.Length];
        var li = 0;
        for (var i = 0; i < counts.Length; i++)
        {
            var c = counts[i];
            if (c == 1) output[i] = li < lines.Length ? lines[li].TrimEnd('\r') : "";
            else output[i] = string.Join('\n', lines.Skip(li).Take(c).Select(l => l.TrimEnd('\r')));
            li += c;
        }
        return output;
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
