namespace UltraLogViewer.Core;

public sealed partial class LogDocument
{
    private (string Key, GroupedHistogram? Result)? _groupCache;

    /// <summary>
    /// The timeline of the view stacked by a field (<see cref="HistogramGroups.SourceField"/> = the file of a merged view)
    /// instead of by level, over the same buckets as <see cref="Histogram"/>. Other fields need the entry texts: the view
    /// rows are read and parsed on all cores (<see cref="InOrder"/>) and counted here in order. The result is kept per
    /// view, so redrawing or resizing to the same width does not scan again. Null when the view has no timestamps.
    /// </summary>
    public Task<GroupedHistogram?> HistogramByAsync(string field, int buckets, CancellationToken ct = default) => Task.Run(() =>
    {
        var key = $"{_viewEpoch}:{_dataEpoch}:{ViewCount}:{IgnoreFilter}:{BaseCount}:{field}:{buckets}";
        if (_groupCache is { } c && c.Key == key) return c.Result;
        var epoch = _viewEpoch;
        var data = _dataEpoch;
        if (Histogram(buckets) is not { } shape) return null;
        var groups = new HistogramGroups(shape.Start, shape.BucketMs, shape.Counts.Length);
        var n = ViewCount;
        if (field == HistogramGroups.SourceField)
        {
            // answered from the index: no entry has to be read
            var v = CurView;
            var m = _merge;
            var names = _sources.Select(s => s.Info().DisplayName).ToArray();
            for (var r = 0; r < n; r++)
            {
                var (s, e) = Ref(BaseOf(r, v), m);
                groups.Add(_sources[s].TimeOf(e), names[s]);
            }
        }
        else
        {
            var blocks = new List<(int Start, int Count)>();
            for (var r = 0; r < n; r += 2000) blocks.Add((r, 2000));
            ForEachRowBlock(blocks, rows => rows.Select(r => (Time: r.Time ?? Timestamps.None, Value: HistogramGroups.Normalize(FieldValue(r.Fields, field)))).ToArray(), part =>
            {
                foreach (var (t, val) in part) groups.Add(t, val);
            }, ct);
        }
        var result = groups.Build(field);
        if (epoch == _viewEpoch && data == _dataEpoch) _groupCache = (key, result);
        return result;
    }, ct);

    /// <summary>A parsed field by name, matched like the quick filter does: exactly, "timestamp" as "time", then ignoring case.</summary>
    internal static string FieldValue(IReadOnlyDictionary<string, string> fields, string key)
    {
        if (fields.TryGetValue(key, out var v)) return v;
        if (key.Equals("timestamp", StringComparison.OrdinalIgnoreCase) && fields.TryGetValue("time", out v)) return v;
        foreach (var (k, x) in fields)
            if (k.Equals(key, StringComparison.OrdinalIgnoreCase)) return x;
        return "";
    }
}
