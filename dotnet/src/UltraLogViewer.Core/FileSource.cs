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

    private void IndexFrom(long size, IProgress<double>? progress, CancellationToken ct)
    {
        var fmt = Format;
        var buf = new byte[_opts.ChunkSize];
        var pos = DataEnd;
        var startPos = pos;
        var starts = new List<long>(1 << 16);
        var entryLines = new List<int>(1 << 16);
        var entryLevels = new List<byte>(1 << 16);
        var entryTimes = new List<long>(1 << 16);
        while (pos < size && !Closed)
        {
            ct.ThrowIfCancellationRequested();
            var want = (int)Math.Min(buf.Length, size - pos);
            var n = RandomAccess.Read(_handle!, buf.AsSpan(0, want), pos);
            if (n <= 0) break;
            var eof = pos + n >= size;
            var end = buf.AsSpan(0, n).LastIndexOf((byte)'\n') + 1; // bytes up to and including the last '\n'
            if (end == 0 && !eof && buf.Length < MaxLineBytes)
            {
                // A line longer than the buffer: grow it and read again.
                buf = new byte[Math.Min(MaxLineBytes, buf.Length * 2)];
                continue;
            }
            if (end == 0) end = n; // the unterminated tail, or a pathological line split at MaxLineBytes
            else if (eof && end < n) end = n; // include the unterminated last line

            var text = _decoder.GetString(buf, 0, end);
            starts.Clear();
            entryLines.Clear();
            entryLevels.Clear();
            entryTimes.Clear();
            var lineIdx = LineCount;
            var hasEntries = EntryCount > 0;
            var off = 0;
            var ci = 0;
            while (ci <= text.Length)
            {
                var nl = text.IndexOf('\n', ci);
                if (nl < 0 && ci == text.Length) break; // text ended with '\n'
                var lineEnd = nl < 0 ? text.Length : nl;
                var line = text.AsSpan(ci, lineEnd > ci && text[lineEnd - 1] == '\r' ? lineEnd - 1 - ci : lineEnd - ci);
                starts.Add(pos + off);
                var isStart = fmt.ScanSpan(line, out var q) ?? fmt.Scan(line.ToString(), out q);
                if (!isStart && !hasEntries && entryLines.Count == 0)
                {
                    q = fmt.Quick(line.ToString());
                    isStart = true;
                }
                if (isStart)
                {
                    entryLines.Add(lineIdx);
                    entryLevels.Add((byte)q.Level);
                    if (q.Time != Timestamps.None) _lastTime = q.Time;
                    entryTimes.Add(q.Time != Timestamps.None ? q.Time : _lastTime);
                }
                lineIdx++;
                var bnl = buf.AsSpan(off, end - off).IndexOf((byte)'\n');
                off = bnl < 0 ? end : off + bnl + 1;
                if (nl < 0) break;
                ci = nl + 1;
            }
            lock (_sync)
            {
                foreach (var s in starts) _lineStart.Add(s);
                for (var i = 0; i < entryLines.Count; i++)
                {
                    _entryLine.Add(entryLines[i]);
                    _entryLevel.Add(entryLevels[i]);
                    _entryTime.Add(entryTimes[i]);
                }
                pos += end;
                DataEnd = pos;
            }
            if (size > startPos) progress?.Report((double)(pos - startPos) / (size - startPos));
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
