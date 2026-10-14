using UltraLogViewer.Core;

namespace UltraLogViewer.Desktop;

public sealed class Bookmark
{
    public required string Key { get; init; }
    public int Line { get; init; }
    public int Source { get; init; }
    public string Preview { get; init; } = "";
    public string Note { get; set; } = "";
    /// <summary>Position in the current view (null when filtered out).</summary>
    public int? Row { get; set; }
}

/// <summary>State of one open tab: the engine document plus everything the UI remembers about it.</summary>
public sealed class DocumentTab : IDisposable
{
    public DocumentTab(string[] paths, LogDocument doc)
    {
        Paths = paths;
        Doc = doc;
        Info = doc.Info();
    }

    public string[] Paths { get; }
    public LogDocument Doc { get; set; }
    public DocInfo Info { get; set; }
    public FilterSpec Filter { get; set; } = new();
    public TextPattern SearchPattern { get; set; } = new("");
    public SearchResult? SearchResult { get; set; }
    public int SearchCurrent { get; set; }
    public Dictionary<string, Bookmark> Bookmarks { get; } = [];
    public int Anchor { get; set; }
    public int Focus { get; set; }
    public int TopRow { get; set; }
    public bool Follow { get; set; }
    public string FormatId { get; set; } = "";
    public string Encoding { get; set; } = "auto";
    public double? BusyProgress { get; set; }
    public string BusyLabel { get; set; } = "";
    public bool Ready { get; set; }
    /// <summary>New data arrived since the overview, timeline and search hits were computed.</summary>
    public bool StaleViews { get; set; }
    /// <summary>Top values per field for the Fields panel and quick-filter suggestions.</summary>
    public FieldStats? FieldStats { get; set; }
    public DateTime StatsTime { get; set; }
    /// <summary>Fields expanded in the Fields panel.</summary>
    public HashSet<string> ExpandedFields { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Keys of multi-line entries expanded inline in the grid.</summary>
    public HashSet<string> ExpandedRows { get; } = [];
    /// <summary>Entries whose framework frames are shown in full.</summary>
    public HashSet<string> Unfolded { get; } = [];
    /// <summary>Rows appended to the view since the user stopped following (the "↓ N new" pill).</summary>
    public int NewRows { get; set; }
    /// <summary>First view row of the latest appended batch, and when it arrived (new-row flash).</summary>
    public int FlashFrom { get; set; } = int.MaxValue;
    public DateTime FlashStart { get; set; }
    public PatternStats? Patterns { get; set; }
    public DateTime PatternsTime { get; set; }

    public string Title => Paths.Length == 1 ? Path.GetFileName(Paths[0]) : $"{Path.GetFileName(Paths[0])} + {Paths.Length - 1}";
    public int SelectionStart => Math.Min(Anchor, Focus);
    public int SelectionEnd => Math.Max(Anchor, Focus);

    public bool HasCustomFilter =>
        !Filter.Levels.All(l => l) || Filter.Rules.Count > 0 || !string.IsNullOrWhiteSpace(Filter.Query) || Filter.TimeFrom is not null || Filter.TimeTo is not null || Filter.BookmarksOnly
        || Filter.Grouped || Filter.ClearedBefore is not null;

    public void Dispose() => Doc.Dispose();
}
