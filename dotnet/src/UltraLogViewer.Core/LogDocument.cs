using System.Text;
using System.Text.Json;

namespace UltraLogViewer.Core;

/// <summary>
/// What a tab shows: one file, or several files merged into one timeline.
/// Base rows are every entry of every source (merged by timestamp when there are several sources);
/// view rows are the base rows that pass the current filter (plus context rows), in base order.
/// Filtering and search run per source in file order and produce bitmaps, which are then walked in base order; that
/// keeps merged views fast too. Entry texts are read and tested in batches on all cores and merged in entry order
/// (see <see cref="InOrder"/>), so the answers are exactly those of a sequential pass.
/// Snapshots (merge, view) are immutable and swapped atomically, so reads never block.
/// </summary>
public sealed class LogDocument : IDisposable
{
    private const int MaxSearchRows = 2_000_000;

    private sealed record Merge(ushort[] Src, int[] Ent, int[][] BasePos);
    private sealed record View(int[] Rows, byte[]? Context, int[]? Repeat = null);

    private readonly List<FileSource> _sources = [];
    private readonly SemaphoreSlim _queue = new(1, 1);
    private Merge? _merge;
    private View? _view;
    private FilterSpec? _filter;
    /// <summary>Per-source filter verdicts of already evaluated entries: 1 = match.</summary>
    private List<Grow<byte>> _verdict = [];
    private int _filterGen;
    private int _viewEpoch;

    /// <summary>How often a running filter publishes the rows it has found so far.</summary>
    internal int PartialViewMs { get; set; } = 200;
    /// <summary>Entries per batch when scanning entry texts.</summary>
    internal int ScanBatch { get; set; } = 4096;
    /// <summary>False scans on the calling thread only: the sequential reference for the equivalence tests.</summary>
    internal bool ScanInParallel { get; set; } = true;
    /// <summary>Per-source repeat-key hashes for group repeats, computed for <see cref="_repeatMode"/>.</summary>
    private List<Grow<ulong>> _repeatHash = [];
    private string _repeatMode = "";

    public LogDocument(string title) => Title = title;

    public string Title { get; }
    public IReadOnlyList<FileSource> Sources => _sources;
    public bool Indexing { get; private set; }
    public double Progress { get; private set; }
    public bool Merged => _sources.Count > 1;
    public FilterSpec? Filter => _filter;

    /// <summary>
    /// Changes whenever the view is replaced by one with different rows. While a filter runs, the partial views it
    /// publishes keep the epoch: each one only appends rows, so a grid can keep what it has cached.
    /// </summary>
    public int ViewEpoch => _viewEpoch;

    /// <summary>
    /// "Show in context": every entry is shown, the current filter is kept (and stays up to date), and entries it
    /// would hide come back with <see cref="RowData.IsContext"/> set so they can be drawn dimmed.
    /// </summary>
    public bool IgnoreFilter { get; set; }

    /// <summary>The view that rows are read from: null means every base row.</summary>
    private View? CurView => IgnoreFilter ? null : _view;

    public int BaseCount => _merge is { } m ? m.Ent.Length : _sources.Count > 0 ? _sources[0].EntryCount : 0;
    public int ViewCount => CurView is { } v ? v.Rows.Length : BaseCount;

    private (int Src, int Entry) Ref(int baseRow, Merge? m) => m is null ? (0, baseRow) : (m.Src[baseRow], m.Ent[baseRow]);
    private static int BaseOf(int row, View? v) => v is null ? row : v.Rows[row];
    private static int BaseForEntry(int src, int entry, Merge? m) => m is null ? entry : m.BasePos[src][entry];

    public async Task AddSourceAsync(string path, SourceOptions opts, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (_sources.Count > ushort.MaxValue) throw new InvalidOperationException($"A merged view supports at most {ushort.MaxValue + 1} files.");
        var src = new FileSource(_sources.Count, path, opts);
        lock (_sources) _sources.Add(src);
        Indexing = true;
        try
        {
            await src.OpenAsync(new SyncProgress(f =>
            {
                Progress = f;
                progress?.Report(f);
            }), ct);
        }
        catch
        {
            lock (_sources) _sources.Remove(src);
            src.Dispose();
            throw;
        }
        finally
        {
            Indexing = false;
        }
        await Serial(() =>
        {
            RebuildBase();
            return _filter is not null ? ApplyNowAsync(_filter, false, _filterGen, null, ct) : Task.FromResult(true);
        });
    }

    /// <summary>
    /// Opens several files at once (a merged view): they are indexed in parallel, each reporting its own progress
    /// (<see cref="FileSource.Progress"/>). A file that fails is left out; the failures are returned. Throws only when
    /// cancelled.
    /// </summary>
    public async Task<List<(string Path, Exception Error)>> AddSourcesAsync(IReadOnlyList<string> paths, Func<string, SourceOptions> opts,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (_sources.Count + paths.Count > ushort.MaxValue + 1) throw new InvalidOperationException($"A merged view supports at most {ushort.MaxValue + 1} files.");
        var added = paths.Select((p, i) => new FileSource(_sources.Count + i, p, opts(p))).ToList();
        lock (_sources) _sources.AddRange(added);
        Indexing = true;
        var failed = new List<(string, Exception)>();
        try
        {
            void Report()
            {
                Progress = added.Average(s => s.Indexing ? s.Progress : 1);
                progress?.Report(Progress);
            }
            // On the thread pool: each file's encoding and format detection runs before its first await, and would
            // otherwise run one file after the other on the caller's (UI) thread.
            var tasks = added.Select(s => Task.Run(() => s.OpenAsync(new SyncProgress(_ => Report()), ct), ct)).ToArray();
            try
            {
                await Task.WhenAll(tasks);
            }
            catch
            {
                // looked at per file below
            }
            ct.ThrowIfCancellationRequested();
            lock (_sources)
            {
                for (var i = 0; i < added.Count; i++)
                {
                    if (tasks[i].Exception is not { } e) continue;
                    failed.Add((added[i].Path, e.InnerException ?? e));
                    _sources.Remove(added[i]);
                    added[i].Dispose();
                }
                for (var i = 0; i < _sources.Count; i++) _sources[i].Id = i;
            }
        }
        catch
        {
            lock (_sources)
                foreach (var s in added)
                {
                    _sources.Remove(s);
                    s.Dispose();
                }
            throw;
        }
        finally
        {
            Indexing = false;
        }
        await Serial(() =>
        {
            RebuildBase();
            return _filter is not null ? ApplyNowAsync(_filter, false, _filterGen, null, ct) : Task.FromResult(true);
        });
        return failed;
    }

    private async Task<T> Serial<T>(Func<Task<T>> fn)
    {
        await _queue.WaitAsync();
        try
        {
            return await fn();
        }
        finally
        {
            _queue.Release();
        }
    }

    /// <summary>k-way merge of all sources by timestamp (stable: ties keep source order).</summary>
    private void RebuildBase()
    {
        if (_sources.Count < 2)
        {
            _merge = null;
            return;
        }
        var counts = _sources.Select(s => s.EntryCount).ToArray();
        var total = counts.Sum();
        var src = new ushort[total];
        var ent = new int[total];
        var basePos = counts.Select(c => new int[c]).ToArray();
        var cur = new int[_sources.Count];
        for (var n = 0; n < total; n++)
        {
            var best = -1;
            var bestT = long.MaxValue;
            for (var s = 0; s < _sources.Count; s++)
            {
                if (cur[s] >= counts[s]) continue;
                var t = _sources[s].TimeOf(cur[s]); // None (MinValue) sorts first
                if (best < 0 || t < bestT)
                {
                    best = s;
                    bestT = t;
                }
            }
            basePos[best][cur[best]] = n;
            src[n] = (ushort)best;
            ent[n] = cur[best]++;
        }
        _merge = new Merge(src, ent, basePos);
    }

    // ------------------------------------------------------------------ refresh

    /// <summary>Polls all sources for appended or rotated data. Returns true if anything changed.</summary>
    public Task<bool> RefreshAsync() => Serial(async () =>
    {
        if (Indexing) return false;
        var changed = false;
        for (var i = 0; i < _sources.Count; i++)
        {
            var r = await _sources[i].RefreshAsync();
            if (!r.Changed) continue;
            changed = true;
            if (i < _verdict.Count) _verdict[i].Truncate(r.Reset ? 0 : r.FirstChangedEntry);
            if (i < _repeatHash.Count) _repeatHash[i].Truncate(r.Reset ? 0 : r.FirstChangedEntry);
        }
        if (!changed) return false;
        RebuildBase();
        if (_filter is not null) await ApplyNowAsync(_filter, true, _filterGen, null, default);
        return true;
    });

    // ------------------------------------------------------------------- filter

    /// <summary>Applies a filter. Returns false if superseded by a newer call.</summary>
    public Task<bool> ApplyFilterAsync(FilterSpec? spec, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var gen = Interlocked.Increment(ref _filterGen);
        return Serial(() => ApplyNowAsync(spec?.Clone(), false, gen, progress, ct));
    }

    private Task<bool> ApplyNowAsync(FilterSpec? spec, bool incremental, int gen, IProgress<double>? progress, CancellationToken ct) => Task.Run(() =>
    {
        if (gen != _filterGen) return false;
        if (!incremental) _verdict = [];
        _filter = spec;
        if (spec is null || spec.IsTrivial)
        {
            SetView(null);
            _verdict = [];
            return true;
        }
        TextRule Compile(FilterRule r) =>
            r.PatternMatch ? new(_ => false, null, r.Text) : new(Query.RuleMatcher(r), r.Column, null);
        var includes = spec.Rules.Where(r => r.Enabled && r.Text.Length > 0 && r.Mode == RuleMode.Include).Select(Compile).ToArray();
        var excludes = spec.Rules.Where(r => r.Enabled && r.Text.Length > 0 && r.Mode == RuleMode.Exclude).Select(Compile).ToArray();
        var cleared = spec.ClearedBefore;
        // The query's top-level since:/until: join the time range, which the index answers without reading entries.
        var (query, qFrom, qTo) = string.IsNullOrWhiteSpace(spec.Query) ? (null, null, null) : Query.CompileQueryWithTimeRange(spec.Query, spec.QueryCaseSensitive);
        var needsText = includes.Length > 0 || excludes.Length > 0 || query is not null;
        var bookmarks = spec.BookmarksOnly ? spec.Bookmarks : null;
        var from = Math.Max(spec.TimeFrom ?? long.MinValue, qFrom ?? long.MinValue);
        var to = Math.Min(spec.TimeTo ?? long.MaxValue, qTo ?? long.MaxValue);
        var hasTime = spec.TimeFrom is not null || spec.TimeTo is not null || qFrom is not null || qTo is not null;
        var total = _sources.Sum(s => (long)s.EntryCount);
        long done = 0;
        // A full (not tail) text filter publishes partial views as it goes. Each one is a prefix of the next and of the
        // final view (see ViewBuilder), so rows never move once shown. Grouping folds rows at the end, so it waits.
        var progressive = !incremental && !spec.Grouped;
        var published = false;
        var partialClock = System.Diagnostics.Stopwatch.StartNew();
        var builder = new ViewBuilder(this, spec.Context);

        for (var si = 0; si < _sources.Count; si++)
        {
            var src = _sources[si];
            while (_verdict.Count <= si) _verdict.Add(new Grow<byte>(Math.Max(16, src.EntryCount)));
            var v = _verdict[si];
            var start = v.Count;
            var count = src.EntryCount;
            done += start;
            bool Cheap(int e)
            {
                if (cleared is not null && si < cleared.Length && e < cleared[si]) return false;
                if (bookmarks is not null) return bookmarks.Contains(RowKeys.Of(si, e));
                if (!spec.Levels[(int)src.LevelOf(e)]) return false;
                if (hasTime)
                {
                    var t = src.TimeOf(e);
                    if (t == Timestamps.None || t < from || t > to) return false;
                }
                return true;
            }
            if (!needsText || bookmarks is not null)
            {
                for (var e = start; e < count; e++) v.Add(Cheap(e) ? (byte)1 : (byte)0);
                done += count - start;
                continue;
            }
            var cancelled = false;
            src.ForEachEntryBatch(start, count, (first, texts) =>
            {
                // Runs on the thread pool, several batches at once; commit below appends the verdicts in entry order.
                var output = new byte[texts.Length];
                if (gen != _filterGen || ct.IsCancellationRequested) return output; // commit drops it
                var test = new TextTest(src, includes, excludes, query);
                for (var i = 0; i < texts.Length; i++)
                {
                    var e = first + i;
                    output[i] = Cheap(e) && test.Matches(e, texts[i]) ? (byte)1 : (byte)0;
                }
                return output;
            }, (first, n, verdicts) =>
            {
                if (gen != _filterGen || ct.IsCancellationRequested)
                {
                    cancelled = true;
                    return false;
                }
                v.AddRange(verdicts);
                done += n;
                progress?.Report(total > 0 ? (double)done / total : 1);
                // Show the rows found so far while the rest of the file is filtered.
                if (progressive && partialClock.ElapsedMilliseconds >= PartialViewMs)
                {
                    SetView(builder.Extend(partial: true), extends: published);
                    published = true;
                    partialClock.Restart();
                }
                return true;
            }, default, ScanBatch, ScanInParallel);
            if (cancelled) return false;
        }
        var view = builder.Extend();
        if (spec.Grouped)
        {
            if (GroupView(view, spec.GroupRepeats, gen, ct) is not { } grouped) return false;
            view = grouped;
        }
        SetView(view, extends: published);
        return true;
    }, CancellationToken.None);

    /// <summary>A compiled rule: a matcher (on the text, or on one field), or a message template to compare with.</summary>
    private readonly record struct TextRule(Func<string, bool> M, string? Column, string? Template);

    /// <summary>
    /// The text part of a filter: include rules (any), exclude rules (none) and the quick-filter query. One instance per
    /// batch and thread, reused for every entry, so testing an entry allocates nothing but what parsing it needs.
    /// </summary>
    private sealed class TextTest
    {
        private readonly FileSource _src;
        private readonly LogFormat _fmt;
        private readonly TextRule[] _includes, _excludes;
        private readonly Func<string, Func<string, string>, bool>? _query;
        private readonly Func<string, string> _field;
        private readonly Predicate<TextRule> _test;
        private string _text = "";
        private int _entry;
        private Dictionary<string, string>? _parsed;
        private string? _template;

        public TextTest(FileSource src, TextRule[] includes, TextRule[] excludes, Func<string, Func<string, string>, bool>? query)
        {
            _src = src;
            _fmt = src.Format;
            _includes = includes;
            _excludes = excludes;
            _query = query;
            _field = Field;
            _test = Test;
        }

        public bool Matches(int entry, string text)
        {
            _entry = entry;
            _text = text;
            _parsed = null;
            _template = null;
            if (_includes.Length > 0 && !Array.Exists(_includes, _test)) return false;
            if (Array.Exists(_excludes, _test)) return false;
            return _query is null || _query(text, _field);
        }

        private Dictionary<string, string> Parsed => _parsed ??= _fmt.Parse(_text);

        private string Field(string k)
        {
            // The level is already indexed: answer without parsing the entry.
            if (k == "level") return LevelNames.Match[(int)_src.LevelOf(_entry)];
            if (k == Query.TimeField) return _src.TimeOf(_entry) is var t && t != Timestamps.None ? t.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
            var parsed = Parsed;
            if (parsed.TryGetValue(k, out var val)) return val;
            var alias = k == "timestamp" ? "time" : k;
            if (parsed.TryGetValue(alias, out val)) return val;
            return parsed.FirstOrDefault(p => p.Key.Equals(k, StringComparison.OrdinalIgnoreCase)).Value ?? "";
        }

        private bool Test(TextRule r)
        {
            if (r.Template is not null)
            {
                _template ??= Patterns.Template(Patterns.MessageOf(_text, Parsed));
                return _template == r.Template;
            }
            return r.Column is null ? r.M(_text) : r.M(Field(r.Column));
        }
    }

    /// <summary>Folds consecutive view rows with the same repeat key into their first row. Null when superseded.</summary>
    private View? GroupView(View view, string mode, int gen, CancellationToken ct)
    {
        if (mode != _repeatMode)
        {
            _repeatHash = [];
            _repeatMode = mode;
        }
        for (var si = 0; si < _sources.Count; si++)
        {
            var src = _sources[si];
            while (_repeatHash.Count <= si) _repeatHash.Add(new Grow<ulong>(Math.Max(16, src.EntryCount)));
            var h = _repeatHash[si];
            if (h.Count >= src.EntryCount) continue;
            var fmt = src.Format;
            var cancelled = false;
            src.ForEachEntryBatch(h.Count, src.EntryCount, (first, texts) =>
            {
                var output = new ulong[texts.Length];
                if (gen != _filterGen || ct.IsCancellationRequested) return output; // commit drops it
                for (var i = 0; i < texts.Length; i++)
                    output[i] = Patterns.Hash(Patterns.RepeatKey(src.LevelOf(first + i), fmt.Parse(texts[i]), texts[i], mode));
                return output;
            }, (_, _, hashes) =>
            {
                if (gen != _filterGen || ct.IsCancellationRequested)
                {
                    cancelled = true;
                    return false;
                }
                h.AddRange(hashes);
                return true;
            }, default, ScanBatch, ScanInParallel);
            if (cancelled) return null;
        }
        var m = _merge;
        var rows = new List<int>(view.Rows.Length);
        var ctx = view.Context is null ? null : new List<byte>(view.Rows.Length);
        var repeat = new List<int>(view.Rows.Length);
        ulong? prev = null;
        for (var i = 0; i < view.Rows.Length; i++)
        {
            var b = view.Rows[i];
            var isCtx = view.Context is { } c && c[i] == 1;
            var (s, e) = Ref(b, m);
            ulong? key = isCtx || s >= _repeatHash.Count || e >= _repeatHash[s].Count ? null : _repeatHash[s][e];
            if (key is not null && key == prev)
            {
                repeat[^1]++;
                continue;
            }
            rows.Add(b);
            ctx?.Add(isCtx ? (byte)1 : (byte)0);
            repeat.Add(1);
            prev = key;
        }
        return new View(rows.ToArray(), ctx?.ToArray(), repeat.ToArray());
    }

    /// <summary>
    /// Publishes a view. <paramref name="extends"/> says it only appends rows to the current one (a filter still
    /// running); anything else starts a new <see cref="ViewEpoch"/>.
    /// </summary>
    private void SetView(View? view, bool extends = false)
    {
        _view = view;
        if (!extends) Interlocked.Increment(ref _viewEpoch);
    }

    /// <summary>
    /// Builds the view rows (matches plus <c>context</c> rows around them) in one pass over the base rows, and can
    /// resume where it stopped. While a filter runs it is extended over the base rows whose verdicts are known so far,
    /// so each partial view costs only the new rows. A row is added when it matches, when it is within
    /// <c>context</c> rows before a match, or within <c>context</c> rows after the latest match before it. That
    /// depends only on rows above it and the next <c>context</c> rows, and leading context never reaches back before
    /// the last row added, so the view only ever gains rows at its end: every partial view is a prefix of the final one.
    /// </summary>
    private sealed class ViewBuilder(LogDocument doc, int context)
    {
        private readonly List<int> _rows = [];
        private readonly List<byte>? _ctx = context > 0 ? [] : null;
        private int _next;
        private int _lastAdded = -1;
        private long _lastHit = int.MinValue;

        /// <summary>
        /// Extends the view over the remaining base rows and returns it. With <paramref name="partial"/> it stops at the
        /// first row whose verdict is not known yet; otherwise such rows count as not matching.
        /// </summary>
        public View Extend(bool partial = false)
        {
            var m = doc._merge;
            var verdict = doc._verdict;
            var n = m?.Ent.Length ?? (doc._sources.Count > 0 ? Math.Min(doc._sources[0].EntryCount, verdict.Count > 0 ? verdict[0].Count : 0) : 0);
            var b = _next;
            for (; b < n; b++)
            {
                int s = 0, e = b;
                if (m is not null)
                {
                    s = m.Src[b];
                    e = m.Ent[b];
                }
                var v = s < verdict.Count ? verdict[s] : null;
                if (partial && (v is null || e >= v.Count)) break;
                var hit = v is not null && e < v.Count && v[e] == 1;
                if (_ctx is null)
                {
                    if (hit) _rows.Add(b);
                    continue;
                }
                if (hit)
                {
                    for (var c = Math.Max(_lastAdded + 1, b - context); c < b; c++)
                    {
                        _rows.Add(c);
                        _ctx.Add(1);
                    }
                    _rows.Add(b);
                    _ctx.Add(0);
                    _lastAdded = b;
                    _lastHit = b;
                }
                else if (b - _lastHit <= context)
                {
                    _rows.Add(b);
                    _ctx.Add(1);
                    _lastAdded = b;
                }
            }
            _next = b;
            return new View(_rows.ToArray(), _ctx?.ToArray());
        }
    }

    // --------------------------------------------------------------------- rows

    public List<RowData> GetRows(int start, int count)
    {
        var m = _merge;
        var v = CurView;
        // In "show in context" mode, entries the filter rejects are marked as context.
        var hidden = IgnoreFilter && _view is not null ? _verdict : null;
        var viewCount = v?.Rows.Length ?? BaseCount;
        var end = Math.Min(viewCount, start + count);
        var rows = new List<RowData>(Math.Max(0, end - start));
        var r = Math.Max(0, start);
        while (r < end)
        {
            // Group consecutive rows of one source with consecutive entries into one read.
            var (s, e0) = Ref(BaseOf(r, v), m);
            var len = 1;
            while (r + len < end)
            {
                var (s2, e2) = Ref(BaseOf(r + len, v), m);
                if (s2 != s || e2 != e0 + len) break;
                len++;
            }
            var src = _sources[s];
            var texts = src.ReadEntries(e0, e0 + len);
            for (var i = 0; i < len; i++)
            {
                var e = e0 + i;
                var text = i < texts.Length ? texts[i] : "";
                var (l0, l1) = src.EntryLines(e);
                var t = e < src.EntryCount ? src.TimeOf(e) : Timestamps.None;
                rows.Add(new RowData
                {
                    Row = r + i,
                    Key = RowKeys.Of(s, e),
                    Source = s,
                    Entry = e,
                    Line = l0 + 1,
                    LineCount = Math.Max(1, l1 - l0 + 1),
                    Level = e < src.EntryCount ? src.LevelOf(e) : Level.None,
                    Time = t == Timestamps.None ? null : t,
                    Fields = src.Format.Parse(text),
                    Text = text,
                    IsContext = v?.Context is { } c ? c[r + i] == 1 : hidden is not null && !Passes(hidden, s, e),
                    Repeat = v?.Repeat is { } rep ? rep[r + i] : 1,
                });
            }
            r += len;
        }
        return rows;
    }

    private static bool Passes(List<Grow<byte>> verdict, int s, int e) => s < verdict.Count && e < verdict[s].Count && verdict[s][e] == 1;

    // ------------------------------------------------------------- field stats

    /// <summary>Fields never worth counting: every value differs.</summary>
    private static readonly HashSet<string> UncountedFields = new(StringComparer.OrdinalIgnoreCase) { "message", "time" };

    /// <summary>
    /// Top values of every parsed field over the view rows. Views larger than <paramref name="maxRows"/> are sampled
    /// in evenly spaced blocks, so the panel stays responsive on huge files.
    /// </summary>
    public Task<FieldStats> FieldStatsAsync(int maxRows = 100_000, int top = 10, CancellationToken ct = default) => Task.Run(() =>
    {
        const int DistinctCap = 5000;
        var n = ViewCount;
        var counts = new Dictionary<string, (Dictionary<string, int> Values, int Present, bool Capped)>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        var scanned = 0;
        void Count(List<RowData> rows)
        {
            foreach (var row in rows)
            {
                foreach (var (k, raw) in row.Fields)
                {
                    if (UncountedFields.Contains(k)) continue;
                    var v = raw.Trim();
                    if (v.Length == 0) continue;
                    if (!counts.TryGetValue(k, out var c))
                    {
                        c = (new Dictionary<string, int>(), 0, false);
                        order.Add(k);
                    }
                    c.Present++;
                    // Multi-line or very long values are counted as present but not listed: a shortened value
                    // could not be used as an exact filter.
                    if (v.Length <= 200 && !v.Contains('\n'))
                    {
                        if (c.Values.TryGetValue(v, out var x)) c.Values[v] = x + 1;
                        else
                        {
                            if (c.Values.Count >= DistinctCap)
                            {
                                PruneRare(c.Values, DistinctCap / 2);
                                c.Capped = true;
                            }
                            c.Values[v] = 1;
                        }
                    }
                    counts[k] = c;
                }
            }
            scanned += rows.Count;
        }
        var (blocks, sampled) = ScanBlocks(n, maxRows);
        // Reading and parsing the rows is the expensive part: done on all cores, counted here in row order.
        ForEachRowBlock(blocks, rows => rows, Count, ct);
        var fields = order.Select(k =>
        {
            var c = counts[k];
            var best = c.Values.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(top).Select(kv => (kv.Key, kv.Value)).ToList();
            return new FieldStat(k, c.Present, c.Values.Count, c.Capped, best);
        }).ToList();
        return new FieldStats(scanned, n, sampled, fields);
    }, ct);

    private const int StatsBlock = 1000;

    /// <summary>
    /// The view-row blocks that field stats and patterns read: every row (in blocks of 2000), or, for views larger than
    /// <paramref name="maxRows"/>, evenly spaced blocks of <see cref="StatsBlock"/> rows.
    /// </summary>
    private static (List<(int Start, int Count)> Blocks, bool Sampled) ScanBlocks(int n, int maxRows)
    {
        var blocks = new List<(int, int)>();
        if (n <= maxRows)
        {
            for (var r = 0; r < n; r += 2000) blocks.Add((r, 2000));
            return (blocks, false);
        }
        var count = Math.Max(1, maxRows / StatsBlock);
        var stride = (double)(n - StatsBlock) / Math.Max(1, count - 1);
        for (var b = 0; b < count; b++) blocks.Add(((int)(b * stride), StatsBlock));
        return (blocks, true);
    }

    /// <summary>Reads blocks of view rows and runs <paramref name="work"/> on them on all cores; <paramref name="commit"/> gets the results here, in order.</summary>
    private void ForEachRowBlock<T>(List<(int Start, int Count)> blocks, Func<List<RowData>, T> work, Action<T> commit, CancellationToken ct) =>
        InOrder.Run(blocks.Count, i => work(GetRows(blocks[i].Start, blocks[i].Count)), (_, r) =>
        {
            commit(r);
            return true;
        }, ct, ScanInParallel);

    /// <summary>
    /// Makes room in a full value-count table by dropping the rarest values (count ≤ 1, then ≤ 2, …) until at most
    /// <paramref name="keep"/> remain, so values that only become common later in the view can still reach the top list.
    /// </summary>
    private static void PruneRare(Dictionary<string, int> values, int keep)
    {
        for (var limit = 1; values.Count > keep; limit++)
            foreach (var (k, n) in values.ToList())
                if (n <= limit) values.Remove(k);
    }

    // ----------------------------------------------------------------- patterns

    /// <summary>
    /// Groups the view's messages by template (variable parts masked), counting levels and where each occurs.
    /// Views larger than <paramref name="maxRows"/> are sampled like <see cref="FieldStatsAsync"/>.
    /// </summary>
    public Task<PatternStats> PatternsAsync(int maxRows = 200_000, int top = 300, CancellationToken ct = default) => Task.Run(() =>
    {
        const int Cap = 20_000;
        const string Other = "(other)";
        var n = ViewCount;
        var (blocks, sampled) = ScanBlocks(n, maxRows);
        var expected = Math.Max(1, sampled ? Math.Max(1, maxRows / StatsBlock) * StatsBlock : n);
        var map = new Dictionary<string, (int Count, int[] Levels, string Sample, int[] Spark)>(StringComparer.Ordinal);
        var scanned = 0;
        void Count((RowData Row, string Msg, string Template)[] rows)
        {
            foreach (var (row, msg, tpl) in rows)
            {
                var t = tpl;
                if (!map.TryGetValue(t, out var p))
                {
                    if (map.Count >= Cap) t = Other;
                    if (!map.TryGetValue(t, out p))
                    {
                        var nl = msg.IndexOf('\n');
                        var sample = nl >= 0 ? msg[..nl] : msg;
                        p = (0, new int[7], sample.Length > 300 ? sample[..300] : sample, new int[24]);
                    }
                }
                p.Count++;
                p.Levels[(int)row.Level]++;
                p.Spark[Math.Min(23, (int)((long)scanned * 24 / expected))]++;
                map[t] = p;
                scanned++;
            }
        }
        // Messages and templates are worked out on all cores; the counting (and the cap) runs here in row order.
        ForEachRowBlock(blocks, rows => rows.Select(r =>
        {
            var msg = Patterns.MessageOf(r.Text, r.Fields);
            return (r, msg, Patterns.Template(msg));
        }).ToArray(), Count, ct);
        var list = map.OrderByDescending(kv => kv.Value.Count).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(top)
            .Select(kv => new PatternInfo(kv.Key, kv.Value.Count, kv.Value.Levels, kv.Value.Sample, kv.Value.Spark)).ToList();
        return new PatternStats(scanned, n, sampled, map.Count, list);
    }, ct);

    // ------------------------------------------------------------------- search

    /// <summary>Finds all view rows whose entry text matches.</summary>
    public Task<SearchResult> SearchAsync(TextPattern p, IProgress<double>? progress = null, CancellationToken ct = default) => Task.Run(() =>
    {
        if (string.IsNullOrEmpty(p.Text)) return SearchResult.Empty;
        var match = Query.Matcher(p);
        var m = _merge;
        var v = CurView;
        var verdict = _verdict;
        var hits = _sources.Select(s => new bool[s.EntryCount]).ToArray();
        var total = _sources.Sum(s => (long)s.EntryCount);
        long done = 0;
        try
        {
            for (var si = 0; si < _sources.Count; si++)
            {
                var src = _sources[si];
                var inView = v is not null && v.Context is null && si < verdict.Count ? verdict[si] : null;
                var h = hits[si];
                // Batches run in parallel; each one only writes its own entries' slots in h.
                src.ForEachEntryBatch(0, h.Length, (first, texts) =>
                {
                    for (var i = 0; i < texts.Length; i++)
                    {
                        var e = first + i;
                        if (inView is not null && (e >= inView.Count || inView[e] != 1)) continue;
                        if (match(texts[i])) h[e] = true;
                    }
                    return true;
                }, (_, n, _) =>
                {
                    done += n;
                    progress?.Report(total > 0 ? (double)done / total : 1);
                    return true;
                }, ct, ScanBatch, ScanInParallel);
            }
        }
        catch (OperationCanceledException)
        {
            return SearchResult.Empty with { Cancelled = true };
        }
        var rows = new List<int>();
        var n = v?.Rows.Length ?? (m?.Ent.Length ?? (_sources.Count > 0 ? hits[0].Length : 0));
        var count = 0;
        for (var r = 0; r < n; r++)
        {
            var (s, e) = Ref(BaseOf(r, v), m);
            if (e < hits[s].Length && hits[s][e])
            {
                count++;
                if (rows.Count < MaxSearchRows) rows.Add(r);
            }
        }
        return new SearchResult(rows.ToArray(), count, count > rows.Count);
    }, ct);

    // ---------------------------------------------------------------- navigation

    /// <summary>View row for a base position (nearest following row if filtered out).</summary>
    private int RowForBase(int baseRow, View? v)
    {
        if (v is null) return baseRow;
        var rows = v.Rows;
        if (rows.Length == 0) return 0;
        return Math.Min(rows.Length - 1, Search.LowerBound(0, rows.Length, i => rows[i] >= baseRow));
    }

    public int RowForLine(int source, int line1)
    {
        if (source >= _sources.Count || _sources[source].EntryCount == 0) return 0;
        var e = _sources[source].EntryForLine(Math.Max(0, line1 - 1));
        var m = _merge;
        if (m is not null && e >= m.BasePos[source].Length) return Math.Max(0, ViewCount - 1);
        return RowForBase(BaseForEntry(source, e, m), CurView);
    }

    /// <summary>View row of an entry; null if it is filtered out (or, with <paramref name="nearest"/>, the next row that isn't).</summary>
    public int? RowForKey(string key, bool nearest = false)
    {
        if (!RowKeys.TryParse(key, out var s, out var e) || s >= _sources.Count || e >= _sources[s].EntryCount) return null;
        var m = _merge;
        var v = CurView;
        if (m is not null && e >= m.BasePos[s].Length) return null;
        var b = BaseForEntry(s, e, m);
        var row = RowForBase(b, v);
        return v is null || nearest || (v.Rows.Length > row && v.Rows[row] == b) ? row : null;
    }

    public string? KeyForLine(int source, int line1)
    {
        if (source >= _sources.Count) return null;
        var src = _sources[source];
        if (src.EntryCount == 0 || line1 < 1 || line1 > src.LineCount) return null;
        return RowKeys.Of(source, src.EntryForLine(line1 - 1));
    }

    private long RowTime(int r, View? v, Merge? m)
    {
        var (s, e) = Ref(BaseOf(r, v), m);
        return e < _sources[s].EntryCount ? _sources[s].TimeOf(e) : Timestamps.None;
    }

    /// <summary>First view row at or after time t.</summary>
    public int RowForTime(long t)
    {
        var v = CurView;
        var m = _merge;
        var n = v?.Rows.Length ?? BaseCount;
        var r = Search.LowerBound(0, n, i =>
        {
            var x = RowTime(i, v, m);
            return x != Timestamps.None && x >= t;
        });
        return Math.Max(0, Math.Min(r, n - 1));
    }

    /// <summary>Next/previous view row (exclusive of <paramref name="from"/>): errors (level ≥ Error) or warnings (== Warn).</summary>
    public int? FindLevel(int from, int dir, Level level)
    {
        var v = CurView;
        var m = _merge;
        var n = v?.Rows.Length ?? BaseCount;
        for (var r = from + dir; r >= 0 && r < n; r += dir)
        {
            var (s, e) = Ref(BaseOf(r, v), m);
            var l = _sources[s].LevelOf(e);
            if (level == Level.Warn ? l == Level.Warn : l >= level) return r;
        }
        return null;
    }

    // ----------------------------------------------------------------- overview

    public int[] LevelCounts() => Totals().LevelCounts;

    public (long Min, long Max)? TimeRange() => Totals().Range;

    /// <summary>Level counts and time range over all sources (each source keeps them up to date incrementally).</summary>
    private (int[] LevelCounts, (long Min, long Max)? Range) Totals()
    {
        var counts = new int[LevelNames.Count];
        long lo = long.MaxValue, hi = long.MinValue;
        FileSource[] sources;
        lock (_sources) sources = [.. _sources];
        foreach (var s in sources)
        {
            var (c, min, max) = s.Stats();
            for (var i = 0; i < counts.Length; i++) counts[i] += c[i];
            lo = Math.Min(lo, min);
            hi = Math.Max(hi, max);
        }
        return (counts, lo <= hi ? (lo, hi) : null);
    }

    /// <summary>Per-bucket flags over the view rows for the overview ruler: bit 1 = warning, bit 2 = error/fatal.</summary>
    public byte[] Overview(int buckets)
    {
        buckets = Math.Max(1, buckets);
        var v = CurView;
        var m = _merge;
        var n = v?.Rows.Length ?? BaseCount;
        var output = new byte[buckets];
        for (var r = 0; r < n; r++)
        {
            var (s, e) = Ref(BaseOf(r, v), m);
            var l = _sources[s].LevelOf(e);
            if (l < Level.Warn) continue;
            output[(int)((long)r * buckets / n)] |= l == Level.Warn ? (byte)1 : (byte)2;
        }
        return output;
    }

    private static readonly long[] NiceBuckets =
    [
        1, 2, 5, 10, 20, 50, 100, 250, 500, 1_000, 2_000, 5_000, 10_000, 15_000, 30_000, 60_000, 120_000, 300_000, 600_000, 900_000,
        1_800_000, 3_600_000, 7_200_000, 10_800_000, 21_600_000, 43_200_000, 86_400_000, 172_800_000, 604_800_000, 2_592_000_000, 31_536_000_000,
    ];

    public Histogram? Histogram(int buckets)
    {
        var v = CurView;
        var m = _merge;
        var n = v?.Rows.Length ?? BaseCount;
        long lo = long.MaxValue, hi = long.MinValue;
        for (var r = 0; r < n; r++)
        {
            var t = RowTime(r, v, m);
            if (t == Timestamps.None) continue;
            if (t < lo) lo = t;
            if (t > hi) hi = t;
        }
        if (lo > hi) return null;
        var span = Math.Max(1, hi - lo);
        var bucketMs = NiceBuckets.FirstOrDefault(x => (double)span / x <= buckets);
        if (bucketMs == 0) bucketMs = (long)Math.Ceiling((double)span / buckets);
        var start = lo / bucketMs * bucketMs;
        var count = (int)((hi - start) / bucketMs) + 1;
        var counts = new (int Other, int Warn, int Error)[count];
        for (var r = 0; r < n; r++)
        {
            var (s, e) = Ref(BaseOf(r, v), m);
            var t = _sources[s].TimeOf(e);
            if (t == Timestamps.None) continue;
            var b = (int)((t - start) / bucketMs);
            var l = _sources[s].LevelOf(e);
            if (l >= Level.Error) counts[b].Error++;
            else if (l == Level.Warn) counts[b].Warn++;
            else counts[b].Other++;
        }
        return new Histogram(start, bucketMs, counts);
    }

    public DocInfo Info()
    {
        var (levelCounts, range) = Totals();
        var cols = _sources.Count > 0 ? _sources[0].Format.Columns.ToList() : [];
        if (Merged && cols.Count > 0) cols.Insert(0, new ColumnDef("__source", "Source", 110));
        return new DocInfo(BaseCount, ViewCount, levelCounts, Indexing, Progress, range?.Min, range?.Max, Merged,
            _sources.Select(s => s.Info()).ToList(), cols);
    }

    // ------------------------------------------------------------------- export

    /// <summary>Writes the current view to a file as "text", "csv" or "jsonl". Returns the number of entries.</summary>
    public Task<int> ExportAsync(string file, string format, IProgress<double>? progress = null) => Task.Run(() =>
    {
        var n = ViewCount;
        var cols = Info().Columns.Where(c => c.Key != "__source").ToList();
        using var w = new StreamWriter(file, false, new UTF8Encoding(false));
        static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        if (format == "csv")
            w.WriteLine(string.Join(',', (Merged ? new[] { "source" } : []).Concat(["line"]).Concat(cols.Select(c => c.Title)).Select(Q)));
        var blocks = new List<(int Start, int Count)>();
        for (var r = 0; r < n; r += 2000) blocks.Add((r, 2000));
        var written = 0;
        var newLine = w.NewLine;
        // Rows are read and formatted on all cores, and written here in order.
        ForEachRowBlock(blocks, rows =>
        {
            var sb = new StringBuilder();
            foreach (var row in rows)
            {
                var srcName = System.IO.Path.GetFileName(_sources[row.Source].Path);
                switch (format)
                {
                    case "csv":
                        sb.Append(string.Join(',', (Merged ? new[] { srcName } : []).Concat([row.Line.ToString()]).Concat(cols.Select(c => row.Field(c.Key))).Select(Q))).Append(newLine);
                        break;
                    case "jsonl":
                        var obj = new Dictionary<string, object>();
                        if (Merged) obj["source"] = srcName;
                        obj["line"] = row.Line;
                        foreach (var (k, val) in row.Fields) obj[k] = val;
                        sb.Append(JsonSerializer.Serialize(obj)).Append(newLine);
                        break;
                    default:
                        sb.Append(row.Text).Append('\n');
                        break;
                }
            }
            return (Text: sb.ToString(), Rows: rows.Count);
        }, chunk =>
        {
            w.Write(chunk.Text);
            written += chunk.Rows;
            progress?.Report(Math.Min(1, (double)written / Math.Max(1, n)));
        }, default);
        return n;
    });

    public Task ReformatAsync(string? formatId, IReadOnlyList<CustomFormat>? custom) => Serial(async () =>
    {
        foreach (var s in _sources) await s.SetFormatAsync(formatId, custom);
        _verdict = [];
        _repeatHash = [];
        RebuildBase();
        if (_filter is not null) await ApplyNowAsync(_filter, false, _filterGen, null, default);
        return true;
    });

    public void Dispose()
    {
        Interlocked.Increment(ref _filterGen);
        foreach (var s in _sources) s.Dispose();
    }
}

/// <summary>Reports synchronously on the calling thread (Progress&lt;T&gt; would post to a sync context).</summary>
public sealed class SyncProgress(Action<double> report) : IProgress<double>
{
    public void Report(double value) => report(value);
}
