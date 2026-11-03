using Avalonia.Controls;
using UltraLogViewer.Core;

namespace UltraLogViewer.Desktop;

/// <summary>
/// The timeline's "Group by" chooser: stack the bars by level (the default), by the file of a merged view, or by any
/// parsed field (logger, thread, host…). Clicking a legend item filters to that value through the quick filter.
/// </summary>
public sealed partial class MainWindow
{
    private CancellationTokenSource? _groupsCts;

    private void WireTimeline()
    {
        _hist.GroupMenuRequested += ShowGroupMenu;
        _hist.LegendClicked += i =>
        {
            if (_hist.Groups is { } g && g.Field != HistogramGroups.SourceField && i < g.Keys.Count) AddQueryClause(HistogramGroups.LegendClause(g, i));
        };
    }

    private static string GroupTitle(string key) => key switch
    {
        "level" => "Level",
        HistogramGroups.SourceField => "Source file",
        _ => key,
    };

    /// <summary>What the timeline can be grouped by in this tab: level, the file (merged views), then the parsed fields.</summary>
    internal List<string> GroupChoices(DocumentTab tab)
    {
        var list = new List<string> { "level" };
        if (tab.Info.Merged) list.Add(HistogramGroups.SourceField);
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "level", "time", "timestamp", "message", HistogramGroups.SourceField };
        foreach (var k in Columns(tab).Select(c => c.Key).Concat(tab.FieldStats?.Fields.Select(f => f.Key) ?? []))
            if (skip.Add(k)) list.Add(k);
        return list;
    }

    private void ShowGroupMenu(Control anchor)
    {
        if (_active is not { } tab) return;
        var menu = new ContextMenu();
        foreach (var key in GroupChoices(tab))
        {
            var item = new MenuItem { Header = GroupTitle(key).Replace("_", "__"), ToggleType = MenuItemToggleType.Radio, IsChecked = key == tab.HistogramGroup };
            item.Click += (_, _) => SetHistogramGroup(key);
            menu.Items.Add(item);
        }
        menu.Open(anchor);
    }

    internal void SetHistogramGroup(string key)
    {
        if (_active is not { } tab || key == tab.HistogramGroup) return;
        tab.HistogramGroup = key;
        _hist.Groups = null;
        Run(RefreshHistogramAsync);
    }

    /// <summary>Recomputes the grouped bars for the active tab (cancelling a grouping still running).</summary>
    private async Task RefreshGroupsAsync(DocumentTab tab, int buckets)
    {
        _groupsCts?.Cancel();
        _hist.GroupLabel = GroupTitle(tab.HistogramGroup);
        var group = tab.HistogramGroup;
        if (group == "level")
        {
            _hist.Groups = null;
            return;
        }
        var cts = _groupsCts = new CancellationTokenSource();
        GroupedHistogram? g;
        try
        {
            g = await tab.Doc.HistogramByAsync(group, buckets, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (tab != _active || group != tab.HistogramGroup || cts.IsCancellationRequested) return;
        _hist.LegendClickable = group != HistogramGroups.SourceField;
        _hist.Groups = g;
    }
}
