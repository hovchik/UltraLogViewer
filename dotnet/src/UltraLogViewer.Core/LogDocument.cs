using System.Text;
using System.Text.Json;

namespace UltraLogViewer.Core;

/// <summary>
/// What a tab shows: one file, or several files merged into one timeline.
/// Base rows are every entry of every source (merged by timestamp when there are several sources);
/// view rows are the base rows that pass the current filter (plus context rows), in base order.
/// Filtering and search run per source in file order (sequential reads) and produce bitmaps,
/// which are then walked in base order; that keeps merged views fast too.
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
            _view = null;
            _verdict = [];
            return true;
        }
        (Func<string, bool> M, string? Column, string? Template) Compile(FilterRule r) =>
            r.PatternMatch ? (_ => false, null, r.Text) : (Query.RuleMatcher(r), r.Column, null);
        var includes = spec.Rules.Where(r => r.Enabled && r.Text.Length > 0 && r.Mode == RuleMode.Include).Select(Compile).ToArray();
        var excludes = spec.Rules.Where(r => r.Enabled && r.Text.Length > 0 && r.Mode == RuleMode.Exclude).Select(Compile).ToArray();
        var cleared = spec.ClearedBefore;
        var query = string.IsNullOrWhiteSpace(spec.Query) ? null : Query.CompileQuery(spec.Query, spec.QueryCaseSensitive);
        var needsText = includes.Length > 0 || excludes.Length > 0 || query is not null;
        var bookmarks = spec.BookmarksOnly ? spec.Bookmarks : null;
        var from = spec.TimeFrom ?? long.MinValue;
        var to = spec.TimeTo ?? long.MaxValue;
        var hasTime = spec.TimeFrom is not null || spec.TimeTo is not null;
        var total = _sources.Sum(s => (long)s.EntryCount);
        long done = 0;

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
            var fmt = src.Format;
            var cancelled = false;
            src.ForEachEntryBatch(start, count, (first, texts) =>
            {
                if (gen != _filterGen || ct.IsCancellationRequested)
                {
                    cancelled = true;
                    return false;
                }
                for (var i = 0; i < texts.Length; i++)
                {
                    var e = first + i;
                    var ok = Cheap(e);
                    if (ok)
                    {
                        var text = texts[i];
                        Dictionary<string, string>? parsed = null;
                        string Field(string k)
                        {
                            // The level is already indexed: answer without parsing the entry.
                            if (k == "level") return LevelNames.Match[(int)src.LevelOf(e)];
                            parsed ??= fmt.Parse(text);
                            if (parsed.TryGetValue(k, out var val)) return val;
                            var alias = k == "timestamp" ? "time" : k;
                            if (parsed.TryGetValue(alias, out val)) return val;
                            return parsed.FirstOrDefault(p => p.Key.Equals(k, StringComparison.OrdinalIgnoreCase)).Value ?? "";
                        }
                        string? template = null;
                        bool Test((Func<string, bool> M, string? Column, string? Template) r)
                        {
                            if (r.Template is not null)
                            {
                                template ??= Patterns.Template(Patterns.MessageOf(text, parsed ??= fmt.Parse(text)));
                                return template == r.Template;
                            }
                            return r.Column is null ? r.M(text) : r.M(Field(r.Column));
                        }
                        if (includes.Length > 0 && !includes.Any(Test)) ok = false;
                        else if (excludes.Any(Test)) ok = false;
                        else if (query is not null && !query(text, Field)) ok = false;
                    }
                    v.Add(ok ? (byte)1 : (byte)0);
                }
                done += texts.Length;
                progress?.Report(total > 0 ? (double)done / total : 1);
                return true;
            });
            if (cancelled) return false;
        }
        BuildView(spec.Context);
        if (spec.Grouped && !GroupView(spec.GroupRepeats, gen, ct)) return false;
        return true;
    }, CancellationToken.None);

    /// <summary>Folds consecutive view rows with the same repeat key into their first row.</summary>
    private bool GroupView(string mode, int gen, CancellationToken ct)
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
                if (gen != _filterGen || ct.IsCancellationRequested)
                {
                    cancelled = true;
                    return false;
                }
                for (var i = 0; i < texts.Length; i++)
                    h.Add(Patterns.Hash(Patterns.RepeatKey(src.LevelOf(first + i), fmt.Parse(texts[i]), texts[i], mode)));
                return true;
            });
            if (cancelled) return false;
        }
        var view = _view!;
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
        _view = new View(rows.ToArray(), ctx?.ToArray(), repeat.ToArray());
        return true;
    }

    private void BuildView(int context)
    {
        var m = _merge;
        var n = m?.Ent.Length ?? (_sources.Count > 0 ? Math.Min(_sources[0].EntryCount, _verdict.Count > 0 ? _verdict[0].Count : 0) : 0);
        var verdict = _verdict;
        bool Hit(int b)
        {
            var (s, e) = Ref(b, m);
            if (s >= verdict.Count) return false;
            var v = verdict[s];
            return e < v.Count && v[e] == 1;
        }
        var rows = new List<int>();
        List<byte>? ctx = context > 0 ? [] : null;
        if (context <= 0)
        {
            for (var b = 0; b < n; b++)
                if (Hit(b)) rows.Add(b);
        }
        else
        {
            var lastAdded = -1;
            for (var b = 0; b < n; b++)
            {
                if (!Hit(b)) continue;
                for (var c = Math.Max(lastAdded + 1, b - context); c < b; c++)
                {
                    rows.Add(c);
                    ctx!.Add(1);
                }
                rows.Add(b);
                ctx!.Add(0);
                lastAdded = b;
                for (var c = b + 1; c <= Math.Min(n - 1, b + context); c++)
                {
                    if (Hit(c)) break;
                    rows.Add(c);
                    ctx.Add(1);
                    lastAdded = c;
                }
            }
        }
        _view = new View(rows.ToArray(), ctx?.ToArray());
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
        const int Block = 1000;
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
        var sampled = n > maxRows;
        if (!sampled)
        {
            for (var r = 0; r < n; r += 2000)
            {
                ct.ThrowIfCancellationRequested();
                Count(GetRows(r, 2000));
            }
        }
        else
        {
            var blocks = Math.Max(1, maxRows / Block);
            var stride = (double)(n - Block) / Math.Max(1, blocks - 1);
            for (var b = 0; b < blocks; b++)
            {
                ct.ThrowIfCancellationRequested();
                Count(GetRows((int)(b * stride), Block));
            }
        }
        var fields = order.Select(k =>
        {
            var c = counts[k];
            var best = c.Values.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(top).Select(kv => (kv.Key, kv.Value)).ToList();
            return new FieldStat(k, c.Present, c.Values.Count, c.Capped, best);
        }).ToList();
        return new FieldStats(scanned, n, sampled, fields);
    }, ct);

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
        const int Block = 1000;
        const int Cap = 20_000;
        const string Other = "(other)";
        var n = ViewCount;
        var sampled = n > maxRows;
        var expected = Math.Max(1, sampled ? Math.Max(1, maxRows / Block) * Block : n);
        var map = new Dictionary<string, (int Count, int[] Levels, string Sample, int[] Spark)>(StringComparer.Ordinal);
        var scanned = 0;
        void Count(List<RowData> rows)
        {
            foreach (var row in rows)
            {
                var msg = Patterns.MessageOf(row.Text, row.Fields);
                var t = Patterns.Template(msg);
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
        if (!sampled)
        {
            for (var r = 0; r < n; r += 2000)
            {
                ct.ThrowIfCancellationRequested();
                Count(GetRows(r, 2000));
            }
        }
        else
        {
            var blocks = Math.Max(1, maxRows / Block);
            var stride = (double)(n - Block) / Math.Max(1, blocks - 1);
            for (var b = 0; b < blocks; b++)
            {
                ct.ThrowIfCancellationRequested();
                Count(GetRows((int)(b * stride), Block));
            }
        }
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
                src.ForEachEntryBatch(0, h.Length, (first, texts) =>
                {
                    for (var i = 0; i < texts.Length; i++)
                    {
                        var e = first + i;
                        if (inView is not null && (e >= inView.Count || inView[e] != 1)) continue;
                        if (match(texts[i])) h[e] = true;
                    }
                    done += texts.Length;
                    progress?.Report(total > 0 ? (double)done / total : 1);
                    return true;
                }, ct);
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

    public int[] LevelCounts()
    {
        var counts = new int[LevelNames.Count];
        foreach (var s in _sources)
            foreach (var l in s.Levels) counts[l]++;
        return counts;
    }

    public (long Min, long Max)? TimeRange()
    {
        long lo = long.MaxValue, hi = long.MinValue;
        foreach (var s in _sources)
            foreach (var t in s.Times)
            {
                if (t == Timestamps.None) continue;
                if (t < lo) lo = t;
                if (t > hi) hi = t;
            }
        return lo <= hi ? (lo, hi) : null;
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
        var range = TimeRange();
        var cols = _sources.Count > 0 ? _sources[0].Format.Columns.ToList() : [];
        if (Merged && cols.Count > 0) cols.Insert(0, new ColumnDef("__source", "Source", 110));
        return new DocInfo(BaseCount, ViewCount, LevelCounts(), Indexing, Progress, range?.Min, range?.Max, Merged,
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
        for (var r = 0; r < n; r += 2000)
        {
            foreach (var row in GetRows(r, 2000))
            {
                var srcName = System.IO.Path.GetFileName(_sources[row.Source].Path);
                switch (format)
                {
                    case "csv":
                        w.WriteLine(string.Join(',', (Merged ? new[] { srcName } : []).Concat([row.Line.ToString()]).Concat(cols.Select(c => row.Field(c.Key))).Select(Q)));
                        break;
                    case "jsonl":
                        var obj = new Dictionary<string, object>();
                        if (Merged) obj["source"] = srcName;
                        obj["line"] = row.Line;
                        foreach (var (k, val) in row.Fields) obj[k] = val;
                        w.WriteLine(JsonSerializer.Serialize(obj));
                        break;
                    default:
                        w.Write(row.Text);
                        w.Write('\n');
                        break;
                }
            }
            progress?.Report(Math.Min(1, (r + 2000.0) / Math.Max(1, n)));
        }
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
