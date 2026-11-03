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

/// <summary>Column choices of a duplicated tab or split pane: its own copy, not saved (see <see cref="DocumentTab.OwnColumns"/>).</summary>
public sealed class TabColumns
{
    public List<string> Extra { get; init; } = [];
    public List<string> Order { get; init; } = [];
    public List<string> Hidden { get; init; } = [];
}

/// <summary>
/// State of one open tab (or split pane): the engine document plus everything the UI remembers about it. Duplicated
/// tabs and split panes have documents over the same indexed files (<see cref="LogDocument.Duplicate"/>).
/// </summary>
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
    /// <summary>The time window this tab was opened with (null: the whole file).</summary>
    public OpenWindow? Window { get; set; }
    /// <summary>Only part of a file is shown (a time window): line numbers are relative and bookmarks are not saved.</summary>
    public bool IsPartial => Info.Sources.Any(s => s.Partial);
    public double? BusyProgress { get; set; }
    public string BusyLabel { get; set; } = "";
    /// <summary>What the last filter or search scanned and how long it took, e.g. "Filtered 8,695,680 entries in 0.82 s".</summary>
    public string ScanCost { get; set; } = "";
    /// <summary>The document's <see cref="LogDocument.ViewEpoch"/> the grid shows (rows of a running filter appear as they are found).</summary>
    public int ViewEpoch { get; set; }
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

    /// <summary>The right-hand pane of a split view (a document over the same files), or null.</summary>
    public DocumentTab? Split { get; set; }
    /// <summary>For a split pane: the tab it belongs to.</summary>
    public DocumentTab? SplitOf { get; init; }
    /// <summary>Selecting an entry in one pane of a split scrolls the other to it (or to the nearest entry by time).</summary>
    public bool SyncSplit { get; set; } = true;
    /// <summary>Number shown after the title of a duplicated tab ("app.log (2)"); 0 for the first one.</summary>
    public int Copy { get; init; }
    /// <summary>Column choices of this tab alone (duplicates and split panes); null: the per-format ones in the settings.</summary>
    public TabColumns? OwnColumns { get; set; }

    public string Title => (Paths.Length == 1 ? Path.GetFileName(Paths[0]) : $"{Path.GetFileName(Paths[0])} + {Paths.Length - 1}") + (Copy > 0 ? $" ({Copy + 1})" : "");
    public int SelectionStart => Math.Min(Anchor, Focus);
    public int SelectionEnd => Math.Max(Anchor, Focus);

    public bool HasCustomFilter =>
        !Filter.Levels.All(l => l) || Filter.Rules.Count > 0 || !string.IsNullOrWhiteSpace(Filter.Query) || Filter.TimeFrom is not null || Filter.TimeTo is not null || Filter.BookmarksOnly
        || Filter.Grouped || Filter.ClearedBefore is not null;

    /// <summary>
    /// A new tab over the same, already indexed files: no reindexing. It starts unfiltered (the caller applies a filter
    /// if it gives one) and has its own view, search, bookmarks and columns.
    /// </summary>
    public DocumentTab Duplicate(int copy, DocumentTab? splitOf, TabColumns columns)
    {
        var t = new DocumentTab(Paths, Doc.Duplicate()) { Copy = copy, SplitOf = splitOf, OwnColumns = columns };
        t.Encoding = Encoding;
        t.FormatId = FormatId;
        t.Window = Window;
        t.Ready = true;
        foreach (var (k, b) in Bookmarks) t.Bookmarks[k] = new Bookmark { Key = b.Key, Line = b.Line, Source = b.Source, Preview = b.Preview, Note = b.Note };
        foreach (var k in ExpandedRows) t.ExpandedRows.Add(k);
        foreach (var k in Unfolded) t.Unfolded.Add(k);
        return t;
    }

    /// <summary>Closes the tab and its split pane; the files stay open while another tab still uses them.</summary>
    public void Dispose()
    {
        Split?.Dispose();
        Split = null;
        Doc.Dispose();
    }
}
