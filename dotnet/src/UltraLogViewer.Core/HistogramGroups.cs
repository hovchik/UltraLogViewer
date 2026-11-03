namespace UltraLogViewer.Core;

/// <summary>
/// The timeline stacked by one field instead of by level: the <see cref="HistogramGroups.Top"/> most common values, plus
/// <see cref="HistogramGroups.Other"/> for the rest when there is a rest. <c>Counts[bucket][key]</c>; Totals per key.
/// </summary>
public sealed record GroupedHistogram(long Start, long BucketMs, string Field, IReadOnlyList<string> Keys, int[][] Counts, int[] Totals)
{
    public bool HasOther => Keys.Count > 0 && Keys[^1] == HistogramGroups.Other;
}

/// <summary>Counts entries per time bucket and field value, then keeps the most common values.</summary>
public sealed class HistogramGroups(long start, long bucketMs, int buckets)
{
    public const int Top = 8;
    public const string Other = "(other)";
    /// <summary>The value of entries that do not have the field.</summary>
    public const string None = "(none)";
    /// <summary>Pseudo-field: the file of each entry in a merged view.</summary>
    public const string SourceField = "__source";
    /// <summary>Distinct values tracked one by one; rarer values beyond this count as "other" (thread ids, request ids…).</summary>
    private const int DistinctCap = 50_000;

    private readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);
    private readonly List<int> _totals = [];
    private readonly Dictionary<int, int>[] _counts = Enumerable.Range(0, Math.Max(1, buckets)).Select(_ => new Dictionary<int, int>()).ToArray();
    private int _overflow;
    private readonly int[] _overflowCounts = new int[Math.Max(1, buckets)];

    /// <summary>The value an entry counts under: first line, trimmed, at most 200 characters; empty is <see cref="None"/>.</summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return None;
        var nl = raw.IndexOf('\n');
        var v = (nl >= 0 ? raw[..nl] : raw).Trim();
        if (v.Length == 0) return None;
        return v.Length > 200 ? v[..200] : v;
    }

    public void Add(long time, string value)
    {
        if (time == Timestamps.None) return;
        var b = (int)((time - start) / bucketMs);
        if (b < 0 || b >= _counts.Length) return;
        if (!_ids.TryGetValue(value, out var id))
        {
            if (_ids.Count >= DistinctCap)
            {
                _overflow++;
                _overflowCounts[b]++;
                return;
            }
            id = _ids.Count;
            _ids[value] = id;
            _totals.Add(0);
        }
        _totals[id]++;
        var c = _counts[b];
        c[id] = c.TryGetValue(id, out var n) ? n + 1 : 1;
    }

    public GroupedHistogram Build(string field)
    {
        var byCount = _ids.OrderByDescending(kv => _totals[kv.Value]).ThenBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        var keep = byCount.Take(Top).ToList();
        var other = _overflow > 0 || byCount.Count > Top;
        var keys = keep.Select(kv => kv.Key).ToList();
        if (other) keys.Add(Other);
        var slot = new Dictionary<int, int>();
        for (var i = 0; i < keep.Count; i++) slot[keep[i].Value] = i;
        var counts = new int[_counts.Length][];
        var totals = new int[keys.Count];
        for (var b = 0; b < _counts.Length; b++)
        {
            var row = counts[b] = new int[keys.Count];
            foreach (var (id, n) in _counts[b])
            {
                var k = slot.TryGetValue(id, out var s) ? s : keys.Count - 1;
                row[k] += n;
                totals[k] += n;
            }
            if (other)
            {
                row[^1] += _overflowCounts[b];
                totals[^1] += _overflowCounts[b];
            }
        }
        return new GroupedHistogram(start, bucketMs, field, keys, counts, totals);
    }

    /// <summary>
    /// The quick-filter clause for a legend item: the value itself, entries without the field for <see cref="None"/>,
    /// and for <see cref="Other"/> everything but the values shown.
    /// </summary>
    public static string LegendClause(GroupedHistogram h, int index)
    {
        var key = h.Keys[index];
        if (key == None) return $"-exists({h.Field})";
        if (key != Other) return Query.FieldToken(h.Field, key, "==");
        var shown = h.Keys.Where(k => k != Other && k != None).ToList();
        var clause = shown.Count == 0 ? "" : "-" + Query.InClause(h.Field, shown);
        return h.Keys.Contains(None) ? (clause + $" exists({h.Field})").Trim() : clause;
    }
}
