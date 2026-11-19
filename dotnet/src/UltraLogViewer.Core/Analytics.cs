using System.Text.RegularExpressions;

namespace UltraLogViewer.Core;

// Background analytics (as src/core/analytics.ts in the Electron app): one pass over every entry of a document (the
// whole log, not the filtered view) that works out levels over time, error bursts, silences, message templates (most
// common, error-heavy, new, stopped, slow), exceptions, field value distributions (with number statistics), durations,
// IPs, URLs and HTTP methods.
//
// It must never slow down opening files or the UI, so it runs on its own below-normal-priority thread, waits while any
// file is indexed or a filter, search or other scan the user waits for runs (see Activity), pauses between batches
// whenever one starts, reads entry texts on at most half of the cores, publishes snapshots a few times a second, and on
// a tail append only reads the new entries (the last entry of a file, which the tail re-reads, is taken back out).

/// <summary>Counts the scans the user waits for (filtering, searching, indexing…): background work holds back meanwhile.</summary>
public static class Activity
{
    private static int _foreground;

    public static bool Busy => Volatile.Read(ref _foreground) > 0;

    /// <summary>Marks a foreground scan until the returned object is disposed.</summary>
    public static IDisposable Foreground()
    {
        Interlocked.Increment(ref _foreground);
        return new Release();
    }

    private sealed class Release : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) Interlocked.Decrement(ref _foreground);
        }
    }

    /// <summary>Runs a task as a foreground scan.</summary>
    public static async Task<T> Run<T>(Func<Task<T>> fn)
    {
        using var _ = Foreground();
        return await fn();
    }
}

public enum AnalyticsState { Waiting, Running, Paused, Done, Cancelled, Error }

public sealed record AnalyticsNumeric(int Count, double Min, double Max, double Avg, double P50, double P90, double P99, double HistStart, double HistWidth, int[] Hist);

public sealed record AnalyticsField(string Key, int Present, int Distinct, bool DistinctCapped, IReadOnlyList<(string Value, int Count)> Top, AnalyticsNumeric? Numeric);

public sealed record AnalyticsPattern(string Template, string Sample, int Count, int[] Levels, long? First, long? Last, int[] Spark, int DurCount, double DurAvg, double DurMax)
{
    public int Errors => Levels[(int)Level.Error] + Levels[(int)Level.Fatal];
}

public sealed record AnalyticsBurst(long From, long To, int Errors, int Total, double Factor);

public sealed record AnalyticsGap(long From, long To, long Ms);

public sealed record AnalyticsSource(string Name, int Entries, int[] Levels, long? TimeMin, long? TimeMax);

public sealed record AnalyticsException(string Name, int Count, long? First, long? Last, int[] Levels);

public sealed record AnalyticsTimeline(long Start, long BucketMs, int[][] Counts);

public sealed record AnalyticsSummary(int Entries, int Sources, long Bytes, int Timed, long? TimeMin, long? TimeMax, double RatePerMin, double PeakPerMin,
    long? PeakAt, int Multiline, double AvgLength, int MaxLength, int Patterns, int Exceptions);

public sealed record AnalyticsResult(
    AnalyticsState State,
    string? Error,
    double Progress,
    int Scanned,
    int Total,
    long ElapsedMs,
    int Run,
    AnalyticsSummary Summary,
    int[] Levels,
    AnalyticsTimeline? Timeline,
    (long Start, long End, int Buckets, bool ByTime) Spark,
    int[][] Hours,
    int[] Weekdays,
    IReadOnlyList<AnalyticsBurst> Bursts,
    IReadOnlyList<AnalyticsGap> Gaps,
    IReadOnlyList<AnalyticsSource> Sources,
    IReadOnlyList<AnalyticsPattern> Patterns,
    IReadOnlyList<AnalyticsPattern> ErrorPatterns,
    IReadOnlyList<AnalyticsPattern> NewPatterns,
    IReadOnlyList<AnalyticsPattern> StoppedPatterns,
    IReadOnlyList<AnalyticsPattern> SlowPatterns,
    IReadOnlyList<AnalyticsException> Exceptions,
    IReadOnlyList<AnalyticsField> Fields,
    AnalyticsNumeric? Durations,
    IReadOnlyList<(string Value, int Count)> Ips,
    IReadOnlyList<(string Value, int Count)> Urls,
    IReadOnlyList<(string Value, int Count)> Methods);

/// <summary>What the workers work out for one entry.</summary>
public sealed record AnalyticsRow(string Template, string Sample, Dictionary<string, string>? Fields, string Exception, double Duration, int Length, int Lines,
    string Ip, string Url, string Method);

public static class AnalyticsRows
{
    private static readonly HashSet<string> SkipFields = new(StringComparer.OrdinalIgnoreCase) { "message", "msg", "time", "timestamp", "level", "@t", "@m", "@mt" };
    private static readonly Regex Exc = new(@"\b(?:[A-Za-z_$][\w$]*\.)*[A-Z][\w$]*(?:Exception|Error|Fault)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex UnitAfter = new(@"^ (ns|us|µs|ms|sec|s|min|h)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Dur = new(@"^([0-9]+(?:\.[0-9]+)?)([a-zµ]+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Dictionary<string, double> UnitMs = new() { ["ns"] = 1e-6, ["us"] = 1e-3, ["µs"] = 1e-3, ["ms"] = 1, ["s"] = 1000, ["sec"] = 1000, ["m"] = 60_000, ["min"] = 60_000, ["h"] = 3_600_000 };

    /// <summary>"120ms" → 120, "1.5s" → 1500; NaN when not a duration.</summary>
    public static double DurationMs(string token)
    {
        var m = Dur.Match(token);
        if (!m.Success || !UnitMs.TryGetValue(m.Groups[2].Value, out var unit)) return double.NaN;
        return double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) * unit;
    }

    public static AnalyticsRow Of(string text, LogFormat fmt)
    {
        var parsed = fmt.Parse(text);
        var msg = Core.Patterns.MessageOf(text, parsed);
        var line = Core.Patterns.TemplateInput(msg);
        var tokens = Tokenizer.Tokenize(line, 1000);
        var tpl = Core.Patterns.TemplateFromTokens(line, tokens);
        var nl = msg.IndexOf('\n');
        var sample = nl >= 0 ? msg[..nl] : msg;
        if (sample.Length > 300) sample = sample[..300];
        double dur = double.NaN;
        string ip = "", url = "", method = "";
        foreach (var tk in tokens)
        {
            var v = line.Substring(tk.Start, tk.Length);
            if (tk.Kind == TokenKind.Duration && double.IsNaN(dur)) dur = DurationMs(v);
            else if (tk.Kind == TokenKind.Number && double.IsNaN(dur))
            {
                var after = line.Substring(tk.Start + tk.Length, Math.Min(9, line.Length - tk.Start - tk.Length));
                var u = UnitAfter.Match(after);
                if (u.Success && double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x)) dur = x * UnitMs[u.Groups[1].Value];
            }
            else if (tk.Kind == TokenKind.Ip && ip.Length == 0) ip = Regex.Replace(v, @":[0-9]+$", "");
            else if (tk.Kind == TokenKind.Url && url.Length == 0) url = Regex.Replace(v, @"[?#].*$", "");
            else if (tk.Kind == TokenKind.Method && method.Length == 0) method = v;
        }
        Dictionary<string, string>? fields = null;
        foreach (var (k, raw) in parsed)
        {
            if (SkipFields.Contains(k)) continue;
            var v = raw.Trim();
            if (v.Length == 0 || v.Length > 200 || v.Contains('\n')) continue;
            (fields ??= new())[k] = v;
        }
        var em = Exc.Match(text.Length > 8000 ? text[..8000] : text);
        var lines = 1;
        foreach (var c in text) if (c == '\n') lines++;
        return new AnalyticsRow(tpl, sample, fields, em.Success ? em.Value : "", dur, msg.Length, lines, ip, url, method);
    }
}

/// <summary>A value → count table that drops its rarest values when full.</summary>
internal sealed class Counter
{
    private const int Cap = 5000;
    public readonly Dictionary<string, int> Values = new(StringComparer.Ordinal);
    public bool Capped;

    public void Add(string v, int sign)
    {
        if (Values.TryGetValue(v, out var x))
        {
            if (x + sign <= 0) Values.Remove(v);
            else Values[v] = x + sign;
            return;
        }
        if (sign < 0) return;
        if (Values.Count >= Cap)
        {
            for (var limit = 1; Values.Count > Cap / 2; limit++)
                foreach (var (k, n) in Values.ToList())
                    if (n <= limit) Values.Remove(k);
            Capped = true;
        }
        Values[v] = 1;
    }

    public List<(string, int)> Top(int n) =>
        Values.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(n).Select(kv => (kv.Key, kv.Value)).ToList();
}

/// <summary>Number statistics with a uniform sample for percentiles (deterministic).</summary>
internal sealed class Numbers
{
    private const int Reservoir = 2048;
    public int Count;
    public double Sum, Min = double.PositiveInfinity, Max = double.NegativeInfinity;
    private readonly List<double> _sample = [];
    private long _seen;
    private uint _seed = 0x9e3779b9;

    public void Add(double x)
    {
        Count++;
        Sum += x;
        if (x < Min) Min = x;
        if (x > Max) Max = x;
        _seen++;
        if (_sample.Count < Reservoir) _sample.Add(x);
        else
        {
            _seed = unchecked(_seed * 1664525 + 1013904223);
            var j = _seed % _seen;
            if (j < Reservoir) _sample[(int)j] = x;
        }
    }

    public void Remove(double x)
    {
        Count--;
        Sum -= x;
    }

    public AnalyticsNumeric? Result()
    {
        if (Count <= 0 || _sample.Count == 0) return null;
        var s = _sample.ToArray();
        Array.Sort(s);
        double Q(double p) => s[Math.Min(s.Length - 1, (int)Math.Floor(p * s.Length))];
        const int buckets = 24;
        var width = Max > Min ? (Max - Min) / buckets : 1;
        var counts = new int[buckets];
        foreach (var x in s) counts[Math.Min(buckets - 1, (int)Math.Floor((x - Min) / width))]++;
        var f = (double)Count / s.Length;
        for (var i = 0; i < buckets; i++) counts[i] = (int)Math.Round(counts[i] * f);
        return new AnalyticsNumeric(Count, Min, Max, Sum / Count, Q(0.5), Q(0.9), Q(0.99), Min, width, counts);
    }
}

/// <summary>Counts of the entry-text facts; rows can be added and (for a tail re-read) taken out again.</summary>
internal sealed class TextStats((long Start, long End, bool ByTime) spark)
{
    public const int SparkBuckets = 48;
    private const int PatternsCap = 20_000;
    private const int FieldsCap = 64;
    public const string Other = "(other)";
    private static readonly Regex Num = new(@"^-?[0-9]+(?:\.[0-9]+)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal sealed class PatternAcc(string template, string sample)
    {
        public readonly string Template = template;
        public readonly string Sample = sample;
        public int Count;
        public readonly int[] Levels = new int[LevelNames.Count];
        public long First = long.MaxValue, Last = long.MinValue;
        public readonly int[] Spark = new int[SparkBuckets];
        public int DurCount;
        public double DurSum, DurMax;
    }

    internal sealed class ExcAcc(string name)
    {
        public readonly string Name = name;
        public int Count;
        public long First = long.MaxValue, Last = long.MinValue;
        public readonly int[] Levels = new int[LevelNames.Count];
    }

    internal sealed class FieldAcc(string key)
    {
        public readonly string Key = key;
        public int Present, Numeric;
        public readonly Counter Values = new();
        public readonly Numbers Nums = new();
    }

    public readonly (long Start, long End, bool ByTime) Spark = spark;
    public int Scanned, Multiline, MaxLen;
    public long LenSum;
    public readonly Dictionary<string, PatternAcc> Patterns = new(StringComparer.Ordinal);
    public PatternAcc? OtherPattern;
    public readonly Dictionary<string, ExcAcc> Exceptions = new(StringComparer.Ordinal);
    public readonly Dictionary<string, FieldAcc> Fields = new(StringComparer.OrdinalIgnoreCase);
    public readonly Numbers Durations = new();
    public readonly Counter Ips = new(), Urls = new(), Methods = new();

    private int Bucket(long time, double pos)
    {
        var (start, end, byTime) = Spark;
        double x;
        if (byTime)
        {
            if (time == Timestamps.None) return -1;
            x = time;
        }
        else x = pos;
        var span = (double)(end - start);
        if (!byTime) span = 1;
        if (span <= 0) return 0;
        return Math.Clamp((int)Math.Floor((x - (byTime ? start : 0)) / span * SparkBuckets), 0, SparkBuckets - 1);
    }

    public void Add(AnalyticsRow r, Level level, long time, double pos, int sign)
    {
        Scanned += sign;
        if (r.Lines > 1) Multiline += sign;
        LenSum += sign * r.Length;
        if (sign > 0 && r.Length > MaxLen) MaxLen = r.Length;
        var timed = time != Timestamps.None;
        var b = Bucket(time, pos);
        var l = (int)level;

        if (!Patterns.TryGetValue(r.Template, out var p))
        {
            if (sign < 0) p = OtherPattern;
            else if (Patterns.Count >= PatternsCap) p = OtherPattern ??= new PatternAcc(Other, r.Sample);
            else Patterns[r.Template] = p = new PatternAcc(r.Template, r.Sample);
        }
        if (p is not null)
        {
            p.Count += sign;
            p.Levels[l] += sign;
            if (b >= 0) p.Spark[b] += sign;
            if (timed && sign > 0)
            {
                if (time < p.First) p.First = time;
                if (time > p.Last) p.Last = time;
            }
            if (!double.IsNaN(r.Duration))
            {
                p.DurCount += sign;
                p.DurSum += sign * r.Duration;
                if (sign > 0 && r.Duration > p.DurMax) p.DurMax = r.Duration;
            }
        }
        if (!double.IsNaN(r.Duration))
        {
            if (sign > 0) Durations.Add(r.Duration);
            else Durations.Remove(r.Duration);
        }

        if (r.Exception.Length > 0)
        {
            if (!Exceptions.TryGetValue(r.Exception, out var e) && sign > 0 && Exceptions.Count < 5000) Exceptions[r.Exception] = e = new ExcAcc(r.Exception);
            if (e is not null)
            {
                e.Count += sign;
                e.Levels[l] += sign;
                if (timed && sign > 0)
                {
                    if (time < e.First) e.First = time;
                    if (time > e.Last) e.Last = time;
                }
            }
        }

        if (r.Fields is { } f)
        {
            foreach (var (k, v) in f)
            {
                if (!Fields.TryGetValue(k, out var acc))
                {
                    if (sign < 0 || Fields.Count >= FieldsCap) continue;
                    Fields[k] = acc = new FieldAcc(k);
                }
                acc.Present += sign;
                acc.Values.Add(v, sign);
                if (Num.IsMatch(v) && double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x))
                {
                    acc.Numeric += sign;
                    if (sign > 0) acc.Nums.Add(x);
                    else acc.Nums.Remove(x);
                }
            }
        }
        if (r.Ip.Length > 0) Ips.Add(r.Ip, sign);
        if (r.Url.Length > 0) Urls.Add(r.Url, sign);
        if (r.Method.Length > 0) Methods.Add(r.Method, sign);
    }
}

/// <summary>Facts the index answers without reading an entry: levels, times.</summary>
internal sealed class IndexStats
{
    private static readonly long[] Nice = [1, 2, 5, 10, 20, 50, 100, 250, 500, 1000, 2000, 5000, 10_000, 15_000, 30_000, 60_000, 120_000, 300_000, 600_000, 900_000,
        1_800_000, 3_600_000, 7_200_000, 10_800_000, 21_600_000, 43_200_000, 86_400_000, 172_800_000, 604_800_000, 2_592_000_000, 31_536_000_000];
    private const int TimelineBuckets = 120;
    private const int MaxGaps = 8;
    public int Entries, Timed;
    public readonly int[] Levels = new int[LevelNames.Count];
    public readonly int[][] Hours = Enumerable.Range(0, 24).Select(_ => new int[3]).ToArray();
    public readonly int[] Weekdays = new int[7];
    public readonly AnalyticsTimeline? Timeline;
    public readonly List<AnalyticsGap> Gaps = [];
    private long _prev = Timestamps.None;

    public IndexStats((long Min, long Max)? range)
    {
        if (range is not { } r) return;
        var span = Math.Max(1, r.Max - r.Min);
        var bucketMs = Nice.FirstOrDefault(x => span / x <= TimelineBuckets);
        if (bucketMs == 0) bucketMs = (long)Math.Ceiling(span / (double)TimelineBuckets);
        var start = (long)Math.Floor(r.Min / (double)bucketMs) * bucketMs;
        var count = (int)((r.Max - start) / bucketMs) + 1;
        Timeline = new AnalyticsTimeline(start, bucketMs, Enumerable.Range(0, count).Select(_ => new int[LevelNames.Count]).ToArray());
    }

    public void Add(Level level, long time)
    {
        Entries++;
        Levels[(int)level]++;
        if (time == Timestamps.None) return;
        Timed++;
        if (Timeline is { } tl)
        {
            var b = (time - tl.Start) / tl.BucketMs;
            if (b >= 0 && b < tl.Counts.Length) tl.Counts[b][(int)level]++;
        }
        var d = DateTimeOffset.FromUnixTimeMilliseconds(time).ToLocalTime();
        var hr = Hours[d.Hour];
        hr[0]++;
        if (level == Level.Warn) hr[1]++;
        else if (level >= Level.Error) hr[2]++;
        Weekdays[(int)d.DayOfWeek]++;
        if (_prev != Timestamps.None)
        {
            var gap = time - _prev;
            if (gap > 0 && (Gaps.Count < MaxGaps || gap > Gaps[^1].Ms))
            {
                Gaps.Add(new AnalyticsGap(_prev, time, gap));
                Gaps.Sort((a, b) => b.Ms.CompareTo(a.Ms));
                if (Gaps.Count > MaxGaps) Gaps.RemoveAt(Gaps.Count - 1);
            }
        }
        if (_prev == Timestamps.None || time >= _prev) _prev = time;
    }
}

public static class AnalyticsMath
{
    /// <summary>Buckets whose errors stand out from the typical bucket (median + 4 MADs, at least 3 errors), merged when adjacent.</summary>
    public static List<AnalyticsBurst> FindBursts(AnalyticsTimeline? tl)
    {
        if (tl is null) return [];
        var errs = tl.Counts.Select(c => c[(int)Level.Error] + c[(int)Level.Fatal]).ToArray();
        var active = errs.Where((_, i) => tl.Counts[i].Any(x => x > 0)).ToArray();
        if (active.Length < 4) return [];
        var sorted = active.OrderBy(x => x).ToArray();
        var median = sorted[sorted.Length / 2];
        var dev = active.Select(x => Math.Abs(x - median)).OrderBy(x => x).ToArray();
        var mad = dev[dev.Length / 2] * 1.4826;
        var mean = active.Average();
        var threshold = Math.Max(3, Math.Max(median + 4 * Math.Max(mad, 1), mean * 2));
        var output = new List<(long From, long To, int Errors, int Total)>();
        var open = false;
        for (var i = 0; i < errs.Length; i++)
        {
            if (errs[i] < threshold)
            {
                open = false;
                continue;
            }
            var total = tl.Counts[i].Sum();
            var from = tl.Start + i * tl.BucketMs;
            if (open)
            {
                var c = output[^1];
                output[^1] = (c.From, from + tl.BucketMs, c.Errors + errs[i], c.Total + total);
            }
            else
            {
                output.Add((from, from + tl.BucketMs, errs[i], total));
                open = true;
            }
        }
        var typical = Math.Max(1, median);
        return output.Select(b => new AnalyticsBurst(b.From, b.To, b.Errors, b.Total, b.Errors / Math.Max(1.0, Math.Round((b.To - b.From) / (double)tl.BucketMs)) / typical))
            .OrderByDescending(b => b.Errors).Take(10).ToList();
    }
}

/// <summary>One document's analysis: runs in the background, can be paused, resumed and restarted, follows tail appends.</summary>
public sealed class AnalyticsJob : IDisposable
{
    private readonly LogDocument _doc;
    private readonly object _gate = new();
    private readonly Func<bool> _busy;
    private AnalyticsState _state = AnalyticsState.Waiting;
    private string? _error;
    private int _run, _gen;
    private TextStats _text = new((0, 1, false));
    private IndexStats? _index;
    private double _indexDone;
    private int[] _cursor = [];
    private (AnalyticsRow Row, Level Level, long Time, double Pos)?[] _last = [];
    private readonly Queue<int[]?> _changes = new();
    private bool _reset = true, _dirty;
    private long _elapsed;
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private long _emittedAt = -1_000_000;
    /// <summary>The job's thread runs (gate held to change it).</summary>
    private bool _looping;
    private volatile AnalyticsResult? _latest;

    /// <summary>Entry batches read at once (default: half the cores).</summary>
    public int Concurrency { get; init; } = Math.Max(1, Environment.ProcessorCount / 2);
    public int Batch { get; init; } = 4000;
    public int EmitMs { get; init; } = 400;
    public int Slice { get; init; } = 200_000;

    /// <summary>A new snapshot (on the job's thread): while running a few times a second, and when the state changes.</summary>
    public event Action<AnalyticsResult>? Updated;

    public AnalyticsJob(LogDocument doc, Func<bool>? busy = null)
    {
        _doc = doc;
        _busy = busy ?? (() => Activity.Busy);
        doc.IndexChanged += OnIndexChanged;
    }

    public AnalyticsState State => _state;

    /// <summary>The latest snapshot.</summary>
    public AnalyticsResult Snapshot()
    {
        lock (_gate) return _latest ?? Result();
    }

    /// <summary>Starts (or continues) the analysis.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_state == AnalyticsState.Cancelled) return;
            if (_state is AnalyticsState.Paused or AnalyticsState.Error or AnalyticsState.Done)
            {
                if (_state == AnalyticsState.Done && !_dirty && !_reset) return;
                _state = AnalyticsState.Waiting;
            }
            Kick();
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_state is AnalyticsState.Done or AnalyticsState.Cancelled) return;
            _state = AnalyticsState.Paused;
            _gen++;
            Publish(true);
        }
    }

    /// <summary>Throws away what was counted and starts over.</summary>
    public void Restart()
    {
        lock (_gate)
        {
            if (_state == AnalyticsState.Cancelled) return;
            _reset = true;
            _gen++;
            _state = AnalyticsState.Waiting;
            Kick();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _state = AnalyticsState.Cancelled;
            _gen++;
        }
        _doc.IndexChanged -= OnIndexChanged;
    }

    private void OnIndexChanged(int[]? first)
    {
        lock (_gate)
        {
            if (_state == AnalyticsState.Cancelled) return;
            _changes.Enqueue(first);
            _gen++; // batches in flight may be of re-indexed entries
            _dirty = true;
            if (_state != AnalyticsState.Paused)
            {
                if (_state is AnalyticsState.Done or AnalyticsState.Error) _state = AnalyticsState.Waiting;
                Kick();
            }
        }
    }

    /// <summary>Takes up index changes (with the gate held): the re-read last entry of a source comes back out; anything else starts over.</summary>
    private void ApplyChanges()
    {
        while (_changes.TryDequeue(out var first))
        {
            if (_reset) continue;
            if (first is null || first.Length != _cursor.Length)
            {
                _reset = true;
                continue;
            }
            for (var s = 0; s < first.Length && !_reset; s++)
            {
                var keep = first[s];
                var c = _cursor[s];
                if (keep >= c) continue;
                if (keep == c - 1 && _last[s] is { } last)
                {
                    _text.Add(last.Row, last.Level, last.Time, last.Pos, -1);
                    _cursor[s] = keep;
                    _last[s] = null;
                }
                else _reset = true;
            }
        }
    }

    /// <summary>Starts the job's thread unless it runs (gate held).</summary>
    private void Kick()
    {
        if (_looping) return;
        _looping = true;
        new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "ULV analytics" }.Start();
    }

    private int TotalEntries() => _doc.Sources.ToArray().Sum(s => s.EntryCount);

    private AnalyticsResult Result()
    {
        var total = TotalEntries();
        var textDone = total > 0 ? Math.Min(1, (double)_text.Scanned / total) : 1;
        var progress = _state == AnalyticsState.Done ? 1 : 0.08 * _indexDone + 0.92 * textDone;
        var running = _state == AnalyticsState.Running ? _clock.ElapsedMilliseconds : 0;
        return Build(progress, total, _elapsed + running);
    }

    private void Publish(bool force = false)
    {
        var now = Environment.TickCount64;
        if (!force && now - _emittedAt < EmitMs) return;
        _emittedAt = now;
        var r = Result();
        _latest = r;
        Updated?.Invoke(r);
    }

    /// <summary>Waits while the document indexes or the user waits for something else. False when superseded.</summary>
    private bool Idle(int gen)
    {
        var waited = false;
        while (true)
        {
            lock (_gate)
            {
                if (gen != _gen) return false;
                if (!_doc.Indexing && !_busy())
                {
                    if (waited || _state != AnalyticsState.Running)
                    {
                        _state = AnalyticsState.Running;
                        _clock.Restart();
                    }
                    return true;
                }
                if (!waited && _state == AnalyticsState.Running)
                {
                    _elapsed += _clock.ElapsedMilliseconds;
                    _state = AnalyticsState.Waiting;
                    Publish(true);
                }
            }
            waited = true;
            Thread.Sleep(120);
        }
    }

    private void Loop()
    {
        while (true)
        {
            int gen;
            lock (_gate)
            {
                if (_state is not (AnalyticsState.Waiting or AnalyticsState.Running))
                {
                    _looping = false;
                    return;
                }
                gen = ++_gen;
                _dirty = false;
            }
            try
            {
                if (!Idle(gen)) continue;
                lock (_gate)
                {
                    ApplyChanges();
                    if (_reset) Begin();
                }
                if (!IndexPass(gen)) continue;
                if (!TextPass(gen)) continue;
                lock (_gate)
                {
                    if (gen != _gen || _dirty || _reset || _changes.Count > 0) continue;
                    _elapsed += _clock.ElapsedMilliseconds;
                    _state = AnalyticsState.Done;
                    _looping = false;
                    Publish(true);
                    return;
                }
            }
            catch (Exception) when (gen != Volatile.Read(ref _gen))
            {
                // superseded while reading (e.g. the file was re-indexed under us)
            }
            catch (Exception e)
            {
                lock (_gate)
                {
                    _state = AnalyticsState.Error;
                    _error = e.Message;
                    _looping = false;
                    Publish(true);
                }
                return;
            }
        }
    }

    /// <summary>Starts over: new sparkline layout from the current time range (gate held).</summary>
    private void Begin()
    {
        _reset = false;
        _changes.Clear();
        _run++;
        _elapsed = 0;
        _clock.Restart();
        var range = _doc.TimeRange();
        _text = new TextStats(range is { } r ? (r.Min, r.Max, true) : (0, 1, false));
        var n = _doc.Sources.Count;
        _cursor = new int[n];
        _last = new (AnalyticsRow, Level, long, double)?[n];
        _index = null;
        _indexDone = 0;
        Publish(true);
    }

    private bool IndexPass(int gen)
    {
        var range = _doc.TimeRange();
        var sp = _text.Spark;
        if (sp.ByTime && range is { } r && r.Max > sp.End + (sp.End - sp.Start) / 4 + 60_000)
        {
            lock (_gate) _reset = true;
            return false;
        }
        var stats = new IndexStats(range);
        var n = _doc.BaseCount;
        var sources = _doc.Sources.ToArray();
        for (var b = 0; b < n;)
        {
            if (!Idle(gen)) return false;
            var end = Math.Min(n, b + Slice);
            for (; b < end; b++)
            {
                var (s, e) = _doc.EntryAt(b);
                if (s >= sources.Length || e >= sources[s].EntryCount) return Superseded(gen);
                var src = sources[s];
                stats.Add(src.LevelOf(e), src.TimeOf(e));
            }
            lock (_gate)
            {
                if (_index is null) _indexDone = (double)b / n;
                Publish();
            }
        }
        lock (_gate)
        {
            if (gen != _gen) return false;
            _index = stats;
            _indexDone = 1;
            Publish();
        }
        return true;
    }

    private bool Superseded(int gen)
    {
        lock (_gate) if (gen == _gen) _dirty = true;
        Thread.Sleep(50); // the merge catches up with the files on the document's next sync
        return false;
    }

    private bool TextPass(int gen)
    {
        var sources = _doc.Sources.ToArray();
        var total = Math.Max(1, sources.Sum(s => s.EntryCount));
        var before = new int[sources.Length];
        for (int i = 0, acc = 0; i < sources.Length; acc += sources[i].EntryCount, i++) before[i] = acc;
        var plan = new List<(int S, int From, int To)>();
        int[] cursor;
        lock (_gate) cursor = [.. _cursor];
        for (var s = 0; s < sources.Length && s < cursor.Length; s++)
            for (var e = cursor[s]; e < sources[s].EntryCount; e += Batch) plan.Add((s, e, Math.Min(sources[s].EntryCount, e + Batch)));
        var inFlight = new Queue<((int S, int From, int To) P, Task<(AnalyticsRow[] Rows, Level[] Levels, long[] Times)> Work)>();
        var next = 0;
        try
        {
            while (next < plan.Count || inFlight.Count > 0)
            {
                if (!Idle(gen)) return false;
                while (next < plan.Count && inFlight.Count < Concurrency)
                {
                    var p = plan[next++];
                    var src = sources[p.S];
                    inFlight.Enqueue((p, Task.Run(() =>
                    {
                        var texts = src.ReadEntries(p.From, p.To);
                        var fmt = src.Format;
                        var rows = new AnalyticsRow[texts.Length];
                        var levels = new Level[texts.Length];
                        var times = new long[texts.Length];
                        for (var i = 0; i < texts.Length; i++)
                        {
                            rows[i] = AnalyticsRows.Of(texts[i], fmt);
                            var e = p.From + i;
                            levels[i] = e < src.EntryCount ? src.LevelOf(e) : Level.None;
                            times[i] = e < src.EntryCount ? src.TimeOf(e) : Timestamps.None;
                        }
                        return (rows, levels, times);
                    })));
                }
                var (job, work) = inFlight.Dequeue();
                var result = work.GetAwaiter().GetResult();
                lock (_gate)
                {
                    if (gen != _gen) return false;
                    if (job.S >= _cursor.Length || job.From != _cursor[job.S]) continue;
                    var count = Math.Min(result.Rows.Length, sources[job.S].EntryCount - job.From);
                    for (var i = 0; i < count; i++)
                    {
                        var pos = (double)(before[job.S] + job.From + i) / total;
                        _text.Add(result.Rows[i], result.Levels[i], result.Times[i], pos, 1);
                        if (i == count - 1) _last[job.S] = (result.Rows[i], result.Levels[i], result.Times[i], pos);
                    }
                    _cursor[job.S] = job.From + Math.Max(0, count);
                    Publish();
                }
            }
        }
        finally
        {
            foreach (var (_, w) in inFlight)
            {
                try { w.Wait(); } catch (AggregateException) { }
            }
        }
        return true;
    }

    // ------------------------------------------------------------- the result

    private static long? Finite(long t) => t is long.MaxValue or long.MinValue or Timestamps.None ? null : t;

    private static AnalyticsPattern Out(TextStats.PatternAcc p) =>
        new(p.Template, p.Sample, p.Count, [.. p.Levels], Finite(p.First), Finite(p.Last), [.. p.Spark], p.DurCount, p.DurCount > 0 ? p.DurSum / p.DurCount : 0, p.DurMax);

    private static List<T> TopBy<T>(IEnumerable<T> items, int n, Func<T, double> score) =>
        items.Select(x => (x, s: score(x))).Where(t => t.s > 0).OrderByDescending(t => t.s).Take(n).Select(t => t.x).ToList();

    private AnalyticsResult Build(double progress, int total, long elapsed)
    {
        var t = _text;
        var idx = _index;
        var srcs = _doc.Sources.ToArray().Select(s =>
        {
            var (levels, min, max) = s.Stats();
            return (Info: new AnalyticsSource(s.Info().DisplayName, s.EntryCount, [.. levels], min <= max ? min : null, min <= max ? max : null), Size: s.DataEnd);
        }).ToList();
        var timeMin = srcs.Where(s => s.Info.TimeMin is not null).Select(s => s.Info.TimeMin!.Value).DefaultIfEmpty(long.MaxValue).Min();
        var timeMax = srcs.Where(s => s.Info.TimeMax is not null).Select(s => s.Info.TimeMax!.Value).DefaultIfEmpty(long.MinValue).Max();
        var entries = srcs.Sum(s => s.Info.Entries);
        var levelsAll = idx?.Levels.ToArray() ?? Enumerable.Range(0, LevelNames.Count).Select(l => srcs.Sum(s => s.Info.Levels[l])).ToArray();
        var timed = timeMax > timeMin;
        var span = timed ? timeMax - timeMin : 0;
        var tl = idx?.Timeline;
        long peak = 0;
        long? peakAt = null;
        if (tl is not null)
            for (var i = 0; i < tl.Counts.Length; i++)
            {
                var n = tl.Counts[i].Sum();
                if (n > peak)
                {
                    peak = n;
                    peakAt = tl.Start + i * tl.BucketMs;
                }
            }
        var all = t.Patterns.Values.ToList();
        if (t.OtherPattern is { } o) all.Add(o);
        var lateFrom = timeMin + span * 0.75;
        var earlyTo = timeMin + span * 0.5;
        var avgInterval = idx is { Timed: > 1 } && timed ? (double)span / idx.Timed : 0;
        var fields = t.Fields.Values.Where(f => f.Present > 0).OrderByDescending(f => f.Present).ThenBy(f => f.Key, StringComparer.Ordinal).Take(40)
            .Select(f => new AnalyticsField(f.Key, f.Present, f.Values.Values.Count, f.Values.Capped, f.Values.Top(12),
                f.Numeric >= Math.Max(5, f.Present * 0.9) && f.Values.Values.Count > 10 ? f.Nums.Result() : null))
            .ToList();
        var summary = new AnalyticsSummary(entries, srcs.Count, srcs.Sum(s => s.Size), idx?.Timed ?? 0, timed || timeMin <= timeMax ? timeMin : null, timed || timeMin <= timeMax ? timeMax : null,
            timed && idx is not null ? idx.Timed / (double)span * 60_000 : 0, tl is not null ? peak / (double)tl.BucketMs * 60_000 : 0, peakAt,
            t.Multiline, t.Scanned > 0 ? (double)t.LenSum / t.Scanned : 0, t.MaxLen, all.Count, t.Exceptions.Count);
        return new AnalyticsResult(
            _state, _error, progress, t.Scanned, total, elapsed, _run, summary, levelsAll, tl,
            (t.Spark.Start, t.Spark.End, TextStats.SparkBuckets, t.Spark.ByTime),
            idx?.Hours.Select(h => h.ToArray()).ToArray() ?? [], idx?.Weekdays.ToArray() ?? [],
            AnalyticsMath.FindBursts(tl),
            (idx?.Gaps ?? []).Where(g => g.Ms >= Math.Max(1000, avgInterval * 20)).ToList(),
            srcs.Select(s => s.Info).ToList(),
            TopBy(all, 100, p => p.Count).Select(Out).ToList(),
            TopBy(all, 30, p => p.Levels[(int)Level.Error] + p.Levels[(int)Level.Fatal]).Select(Out).ToList(),
            timed ? TopBy(all, 15, p => p.First >= lateFrom && p.Template != TextStats.Other && p.Count >= 2 ? p.Count : 0).Select(Out).ToList() : [],
            timed ? TopBy(all, 15, p => p.Last <= earlyTo && p.Template != TextStats.Other && p.Count >= 10 ? p.Count : 0).Select(Out).ToList() : [],
            TopBy(all, 15, p => p.DurCount >= 3 && p.Template != TextStats.Other ? p.DurSum / p.DurCount : 0).Select(Out).ToList(),
            TopBy(t.Exceptions.Values, 30, e => e.Count).Select(e => new AnalyticsException(e.Name, e.Count, Finite(e.First), Finite(e.Last), [.. e.Levels])).ToList(),
            fields,
            t.Durations.Result(),
            t.Ips.Top(15), t.Urls.Top(15), t.Methods.Top(10));
    }
}
