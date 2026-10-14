namespace UltraLogViewer.Core;

/// <summary>Severity levels, ordered so that a higher number is more severe.</summary>
public enum Level : byte
{
    None = 0,
    Trace = 1,
    Debug = 2,
    Info = 3,
    Warn = 4,
    Error = 5,
    Fatal = 6,
}

public static class LevelNames
{
    public const int Count = 7;
    public static readonly string[] Labels = ["", "TRACE", "DEBUG", "INFO", "WARN", "ERROR", "FATAL"];
    public static readonly string[] Display = ["Other", "Trace", "Debug", "Info", "Warning", "Error", "Fatal"];
    /// <summary>Names used when matching <c>level:</c> queries (WARNING also matches "warn").</summary>
    internal static readonly string[] Match = ["NONE", "TRACE", "DEBUG", "INFO", "WARNING", "ERROR", "FATAL"];
}

public record TextPattern(string Text, bool Regex = false, bool CaseSensitive = false, bool WholeWord = false);

public enum RuleMode { Include, Exclude }

public sealed class FilterRule
{
    public string Text { get; set; } = "";
    public bool Regex { get; set; }
    public bool CaseSensitive { get; set; }
    public bool WholeWord { get; set; }
    public bool Enabled { get; set; } = true;
    public RuleMode Mode { get; set; } = RuleMode.Include;
    /// <summary>Restrict matching to one parsed column (e.g. "logger"); null = whole entry text.</summary>
    public string? Column { get; set; }
    /// <summary>The whole (trimmed) value must equal <see cref="Text"/>, e.g. "logger = com.acme.Db" from a click on a cell.</summary>
    public bool Exact { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public TextPattern Pattern => new(Text, Regex, CaseSensitive, WholeWord);
    public FilterRule Clone() => (FilterRule)MemberwiseClone();
}

public sealed class FilterSpec
{
    /// <summary>Indexed by <see cref="Level"/>; false hides that level.</summary>
    public bool[] Levels { get; set; } = [true, true, true, true, true, true, true];
    public List<FilterRule> Rules { get; set; } = [];
    /// <summary>Quick-filter query (see <see cref="Query"/>), AND-ed with everything else.</summary>
    public string Query { get; set; } = "";
    public bool QueryCaseSensitive { get; set; }
    /// <summary>Epoch milliseconds (inclusive).</summary>
    public long? TimeFrom { get; set; }
    public long? TimeTo { get; set; }
    public bool BookmarksOnly { get; set; }
    public HashSet<string> Bookmarks { get; set; } = [];
    /// <summary>Also show N entries before/after each matching entry (grep -C).</summary>
    public int Context { get; set; }

    public bool IsTrivial =>
        Levels.All(l => l) && !Rules.Any(r => r.Enabled && r.Text.Length > 0) && string.IsNullOrWhiteSpace(Query)
        && TimeFrom is null && TimeTo is null && !BookmarksOnly;

    public FilterSpec Clone() => new()
    {
        Levels = (bool[])Levels.Clone(),
        Rules = Rules.Select(r => r.Clone()).ToList(),
        Query = Query,
        QueryCaseSensitive = QueryCaseSensitive,
        TimeFrom = TimeFrom,
        TimeTo = TimeTo,
        BookmarksOnly = BookmarksOnly,
        Bookmarks = [.. Bookmarks],
        Context = Context,
    };
}

public record CustomFormat(string Name, string Pattern);

/// <summary>A column; Width 0 means "take the remaining space".</summary>
public record ColumnDef(string Key, string Title, double Width);

public sealed record SourceInfo(int Id, string Path, string DisplayName, long Size, int Lines, int Entries,
    string Format, string Encoding, bool Compressed, bool Live);

public sealed record DocInfo(int Total, int ViewCount, int[] LevelCounts, bool Indexing, double Progress,
    long? TimeMin, long? TimeMax, bool Merged, IReadOnlyList<SourceInfo> Sources, IReadOnlyList<ColumnDef> Columns);

public sealed class RowData
{
    public int Row { get; init; }
    public string Key { get; init; } = "";
    public int Source { get; init; }
    public int Entry { get; init; }
    /// <summary>1-based first line number in its source file.</summary>
    public int Line { get; init; }
    public int LineCount { get; init; }
    public Level Level { get; init; }
    /// <summary>Unix epoch milliseconds, or null.</summary>
    public long? Time { get; init; }
    public IReadOnlyDictionary<string, string> Fields { get; init; } = new Dictionary<string, string>();
    public string Text { get; init; } = "";
    /// <summary>Only shown as filter context.</summary>
    public bool IsContext { get; init; }

    public string Field(string key) => Fields.TryGetValue(key, out var v) ? v : "";
}

/// <summary>The most common values of one parsed field, counted over the view (or a sample of it).</summary>
public sealed record FieldStat(string Key, int Present, int Distinct, bool DistinctCapped, IReadOnlyList<(string Value, int Count)> Top);

/// <summary><see cref="LogDocument.FieldStatsAsync"/> result: Scanned of ViewCount rows were read (Sampled when fewer).</summary>
public sealed record FieldStats(int Scanned, int ViewCount, bool Sampled, IReadOnlyList<FieldStat> Fields)
{
    public static readonly FieldStats Empty = new(0, 0, false, []);
    public FieldStat? Get(string key) => Fields.FirstOrDefault(f => f.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
}

public sealed record SearchResult(int[] Rows, int Total, bool Truncated, bool Cancelled = false)
{
    public static readonly SearchResult Empty = new([], 0, false);
}

/// <summary>Counts[bucket] = (other, warn, error).</summary>
public sealed record Histogram(long Start, long BucketMs, (int Other, int Warn, int Error)[] Counts);

public static class RowKeys
{
    public static string Of(int source, int entry) => $"{source}:{entry}";

    public static bool TryParse(string key, out int source, out int entry)
    {
        source = entry = 0;
        var i = key.IndexOf(':');
        return i > 0 && int.TryParse(key.AsSpan(0, i), out source) && int.TryParse(key.AsSpan(i + 1), out entry);
    }
}
