using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using UltraLogViewer.Core;
using UltraLogViewer.Desktop.Controls;

namespace UltraLogViewer.Desktop;

/// <summary>
/// Exploration tabs and split view. "Duplicate tab" opens another tab over the same indexed files, and "Split right"
/// puts a second grid beside the first, each with its own filter. Both are documents over one <see cref="SourceSet"/>:
/// nothing is indexed again and the files are tailed once. The focused pane is the active tab, so the toolbar,
/// sidebar, timeline and details pane follow whichever grid has focus.
/// </summary>
public sealed partial class MainWindow
{
    private readonly LogGrid _gridMain;
    private readonly LogGrid _gridSplit;
    private readonly Grid _panes = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto,0") };
    private readonly GridSplitter _paneSplitter = new() { ResizeDirection = GridResizeDirection.Columns, Width = 5, IsVisible = false, Background = new SolidColorBrush(Color.Parse("#808080"), 0.25) };
    private readonly PaneHeader _headMain, _headSplit;
    private readonly Control _splitPane;
    /// <summary>Set while one pane is moved to follow the other, so that move is not synced back.</summary>
    private bool _syncingPanes;

    /// <summary>
    /// The compact header over each pane of a split: its own quick filter and entry count; the right one also has the
    /// sync toggle and the close button.
    /// </summary>
    private sealed class PaneHeader
    {
        public readonly Border Root;
        public readonly TextBox Quick = new() { Watermark = "Quick filter", FontFamily = Fonts.Mono, FontSize = 12, MinHeight = 22, Padding = new Thickness(6, 1), VerticalContentAlignment = VerticalAlignment.Center };
        public readonly TextBlock Count = new() { FontSize = 11.5, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) };
        public readonly ToggleButton? Sync;
        public readonly Button? Close;
        public readonly DispatcherTimer Debounce = new() { Interval = TimeSpan.FromMilliseconds(350) };

        public PaneHeader(bool right)
        {
            var row = new DockPanel { LastChildFill = true };
            if (right)
            {
                Close = new Button { Content = "×", Classes = { "mini" }, Padding = new Thickness(6, 0), MinHeight = 20, FontSize = 13 };
                ToolTip.SetTip(Close, "Close split (Ctrl+\\)");
                Sync = new ToggleButton { Content = "⇅ Sync", Classes = { "mini" }, Padding = new Thickness(6, 0), MinHeight = 20, FontSize = 11.5, IsChecked = true };
                ToolTip.SetTip(Sync, "Selecting an entry in one pane scrolls the other to it, or to the nearest entry by time");
                DockPanel.SetDock(Close, Dock.Right);
                DockPanel.SetDock(Sync, Dock.Right);
                row.Children.Add(Close);
                row.Children.Add(Sync);
            }
            DockPanel.SetDock(Count, Dock.Right);
            row.Children.Add(Count);
            row.Children.Add(Quick);
            Root = new Border { Padding = new Thickness(4, 3), BorderThickness = new Thickness(0, 0, 0, 2), Child = row };
        }
    }

    /// <summary>
    /// What a grid sees of the window. The window works on the active tab, so whatever the user does in a grid (select,
    /// press a key, open a menu) first makes its pane the active one; rendering asks about the grid's own tab.
    /// </summary>
    private sealed class PaneHost(MainWindow w) : ILogGridHost
    {
        public LogGrid Grid = null!;
        private bool Focus()
        {
            if (w._syncingPanes) return false;
            w.FocusPane(Grid);
            return true;
        }
        public AppSettings Settings => w.Settings;
        public IReadOnlyList<HighlightRule> Highlights => w.Highlights;
        public IReadOnlyList<ColumnDef> Columns(DocumentTab tab) => w.Columns(tab);
        public double ColumnWidth(ColumnDef col) => w.ColumnWidth(col);
        public void SetColumnWidth(string key, double width) => w.SetColumnWidth(key, width);
        public bool IsColumnHidden(string key) => w.IsColumnHidden(Grid.Tab, key);
        public void ShowColumnMenu(Control anchor, ColumnDef? col)
        {
            if (Focus()) w.ShowColumnMenu(anchor, col);
        }
        public void ShowRowMenu(RowData row, ColumnDef? col)
        {
            if (Focus()) w.ShowRowMenu(row, col);
        }
        public void SelectionChanged()
        {
            if (Focus()) w.SelectionChanged();
        }
        // Scrolling (or a layout pass) in the other pane does not take the focus.
        public void TopRowChanged()
        {
            if (Grid == w._grid) w.TopRowChanged();
        }
        public bool HandleKey(KeyEventArgs e) => Focus() && w.HandleKey(e);
        public void ToggleBookmark(RowData row)
        {
            if (Focus()) w.ToggleBookmark(row);
        }
        public void CopyRows(int from, int to)
        {
            if (Focus()) w.CopyRows(from, to);
        }
        public void SearchFor(string text, int fromRow)
        {
            if (Focus()) w.SearchFor(text, fromRow);
        }
        public void OpenSource(SourceRef r)
        {
            if (Focus()) w.OpenSource(r);
        }
    }

    /// <summary>Both panes side by side: the main one (with the patterns view and pill over it) and the split.</summary>
    private Control BuildPanes(Control main)
    {
        var left = new DockPanel { Children = { _headMain.Root, main } };
        DockPanel.SetDock(_headMain.Root, Dock.Top);
        Grid.SetColumn(_paneSplitter, 1);
        Grid.SetColumn(_splitPane, 2);
        _panes.Children.Add(left);
        _panes.Children.Add(_paneSplitter);
        _panes.Children.Add(_splitPane);
        foreach (var (head, grid) in new[] { (_headMain, _gridMain), (_headSplit, _gridSplit) })
        {
            grid.GotFocus += (_, _) => FocusPane(grid);
            head.Quick.GotFocus += (_, _) => FocusPane(grid);
            head.Debounce.Tick += (_, _) =>
            {
                head.Debounce.Stop();
                CommitPaneQuick(head, grid);
            };
            head.Quick.TextChanged += (_, _) =>
            {
                if (!head.Quick.IsFocused) return;
                head.Debounce.Stop();
                head.Debounce.Start();
            };
            head.Quick.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    head.Debounce.Stop();
                    CommitPaneQuick(head, grid);
                }
                else if (e.Key != Key.Escape) return;
                e.Handled = true;
                grid.Focus();
            };
        }
        _headSplit.Close!.Click += (_, _) => CloseSplit(OwnerOf(_active));
        _headSplit.Sync!.IsCheckedChanged += (_, _) =>
        {
            if (OwnerOf(_active) is { } owner) owner.SyncSplit = _headSplit.Sync.IsChecked == true;
        };
        _headMain.Root.IsVisible = false;
        return _panes;
    }

    // ============================================================== helpers

    /// <summary>Every open tab, split panes included.</summary>
    private IEnumerable<DocumentTab> AllTabs() => _tabs.SelectMany(t => t.Split is { } s ? new[] { t, s } : [t]);

    /// <summary>A tab, or a split pane that is still open.</summary>
    private bool IsOpen(DocumentTab tab) => _tabs.Contains(tab) || tab.SplitOf is { } o && o.Split == tab && _tabs.Contains(o);

    /// <summary>The tab in the tab strip: the tab itself, or the tab a split pane belongs to.</summary>
    private static DocumentTab? OwnerOf(DocumentTab? tab) => tab?.SplitOf ?? tab;

    /// <summary>The grid showing a tab, or null when it is not on screen.</summary>
    private LogGrid? GridOf(DocumentTab tab) => _gridMain.Tab == tab ? _gridMain : _gridSplit.Tab == tab && _splitPane.IsVisible ? _gridSplit : null;

    private bool SplitShown => _splitPane.IsVisible;

    /// <summary>The grids on screen: display settings and highlights apply to both panes of a split.</summary>
    private IEnumerable<LogGrid> VisibleGrids => SplitShown ? [_gridMain, _gridSplit] : [_gridMain];

    /// <summary>Puts a tab (and its split pane) in the grids; <paramref name="tab"/> is the focused pane.</summary>
    private void ShowPanes(DocumentTab? tab)
    {
        var owner = OwnerOf(tab);
        if (_gridMain.Tab != owner) _gridMain.SetTab(owner);
        var split = owner?.Split;
        if (_gridSplit.Tab != split) _gridSplit.SetTab(split);
        var shown = split is not null;
        if (shown != _splitPane.IsVisible)
        {
            _splitPane.IsVisible = _paneSplitter.IsVisible = _headMain.Root.IsVisible = shown;
            _panes.ColumnDefinitions[2].Width = shown ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            _panes.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        }
        _grid = shown && tab == split ? _gridSplit : _gridMain;
        // The toolbar's quick filter belongs to the active tab; in a split each pane has its own.
        _quick.IsVisible = !shown;
        if (shown) _headSplit.Sync!.IsChecked = owner!.SyncSplit;
        UpdatePaneHeaders();
    }

    /// <summary>Makes the pane of <paramref name="grid"/> the active one (a click or key in it, or focus).</summary>
    private void FocusPane(LogGrid grid)
    {
        if (grid == _grid || grid.Tab is not { } tab || !SplitShown || _syncingPanes) return;
        Activate(tab, focusGrid: false);
    }

    private void UpdatePaneHeaders()
    {
        if (!SplitShown) return;
        var p = Palette.Current;
        foreach (var (head, grid) in new[] { (_headMain, _gridMain), (_headSplit, _gridSplit) })
        {
            if (grid.Tab is not { } t) continue;
            head.Root.BorderBrush = grid == _grid ? p.Accent : Brushes.Transparent;
            head.Root.Background = p.Bg2;
            var i = t.Info;
            head.Count.Text = t.BusyProgress is double f ? $"{t.BusyLabel} {f:P0}"
                : t.Doc.IgnoreFilter || i.ViewCount == i.Total && !t.HasCustomFilter ? $"{i.Total:N0}"
                : $"{i.ViewCount:N0} of {i.Total:N0}";
            if (!head.Quick.IsFocused && head.Quick.Text != t.Filter.Query) head.Quick.Text = t.Filter.Query;
        }
    }

    private void CommitPaneQuick(PaneHeader head, LogGrid grid)
    {
        if (grid.Tab is not { } tab) return;
        var q = head.Quick.Text ?? "";
        if (q == tab.Filter.Query) return;
        if (Query.ValidateQuery(q, tab.Filter.QueryCaseSensitive) is { } err)
        {
            Toast($"Quick filter: {err}", true);
            return;
        }
        tab.Filter.Query = q;
        if (tab == _active) SetQuickFilterText(q);
        Run(() => ApplyFilterAsync(tab));
    }

    // ============================================================ commands

    /// <summary>Opens another tab over the same files with the same filter, search and columns: instant, no reindexing.</summary>
    internal async Task<DocumentTab?> DuplicateTab(DocumentTab? tab)
    {
        if (tab is not { Ready: true } || tab.Doc.Indexing) return null;
        var owner = OwnerOf(tab)!;
        var copy = AllTabs().Where(t => t.Paths.SequenceEqual(tab.Paths)).Max(t => t.Copy) + 1;
        var dup = tab.Duplicate(copy, null, CopyColumns(tab));
        dup.Filter = tab.Filter.Clone();
        dup.SearchPattern = tab.SearchPattern;
        dup.Focus = tab.Focus;
        dup.Anchor = tab.Anchor;
        dup.TopRow = tab.TopRow;
        dup.Info = dup.Doc.Info();
        _tabs.Insert(_tabs.IndexOf(owner) + 1, dup);
        Activate(dup);
        if (!dup.Filter.IsTrivial) await ApplyFilterAsync(dup); // runs the search too
        else
        {
            UpdateBookmarkRows(dup);
            if (dup.SearchPattern.Text.Length > 0 && dup == _active) await RunSearch(false, showCost: false);
        }
        ScheduleFieldStats(dup);
        return dup;
    }

    private void ToggleSplit()
    {
        if (OwnerOf(_active) is not { } owner) return;
        if (owner.Split is not null) CloseSplit(owner);
        else Run(() => SplitRight(owner));
    }

    /// <summary>Opens a second, unfiltered pane over the same files to the right of the tab, scrolled to the selected entry.</summary>
    internal async Task<DocumentTab?> SplitRight(DocumentTab? tab)
    {
        var owner = OwnerOf(tab);
        if (owner is not { Ready: true } || owner.Doc.Indexing) return null;
        if (owner.Split is { } existing) return existing;
        var pane = owner.Duplicate(0, owner, CopyColumns(owner));
        pane.Info = pane.Doc.Info();
        owner.Split = pane;
        Activate(pane);
        UpdateBookmarkRows(pane);
        ScheduleFieldStats(pane);
        await SyncPanesAsync(owner, pane);
        return pane;
    }

    internal void CloseSplit(DocumentTab? owner)
    {
        if (owner?.Split is not { } pane) return;
        owner.Split = null;
        var wasActive = _active == pane;
        pane.Dispose();
        if (wasActive || _active == owner) Activate(owner);
        else RebuildTabStrip();
    }

    /// <summary>After a selection in one pane: the other pane shows the same entry if it passes its filter, else the nearest by time.</summary>
    private void SyncOtherPane()
    {
        if (!SplitShown || _syncingPanes || _active is not { } tab || OwnerOf(tab) is not { SyncSplit: true, Split: { } split } owner) return;
        var other = tab == owner ? split : owner;
        Run(() => SyncPanesAsync(tab, other));
    }

    private async Task SyncPanesAsync(DocumentTab from, DocumentTab to)
    {
        if (from.Info.ViewCount == 0 || to.Info.ViewCount == 0 || GridOf(to) is not { } grid) return;
        var row = await RowAsync(from, from.Focus);
        if (row is null || GridOf(to) != grid || grid.Tab != to) return;
        var target = to.Doc.RowForKey(row.Key) ?? (row.Time is long t ? to.Doc.RowForTime(t) : null);
        if (target is not int r || r == to.Focus && r >= to.TopRow && r < to.TopRow + grid.VisibleRows) return;
        _syncingPanes = true;
        try
        {
            var onScreen = r >= to.TopRow && r < to.TopRow + grid.VisibleRows;
            grid.Select(r, center: !onScreen);
        }
        finally
        {
            _syncingPanes = false;
        }
        UpdatePaneHeaders();
    }

    /// <summary>A pane that is on screen but not active: redraw it after its rows changed.</summary>
    private void RefreshInactivePane(DocumentTab tab)
    {
        if (tab == _active || GridOf(tab) is not { } grid) return;
        grid.Invalidate();
        if (tab.Follow) grid.ScrollTo(int.MaxValue);
        UpdatePaneHeaders();
    }

    internal LogGrid SplitGridForTest => _gridSplit;
    internal LogGrid MainGridForTest => _gridMain;
    internal bool SplitShownForTest => SplitShown;
    internal string PaneCountForTest(bool right) => (right ? _headSplit : _headMain).Count.Text ?? "";
    internal void SetPaneQuickForTest(bool right, string q)
    {
        var head = right ? _headSplit : _headMain;
        head.Quick.Focus();
        head.Quick.Text = q;
        CommitPaneQuick(head, right ? _gridSplit : _gridMain);
    }
    internal void FocusPaneForTest(bool right) => FocusPane(right ? _gridSplit : _gridMain);
    internal void SetSyncForTest(bool on) => _headSplit.Sync!.IsChecked = on;
}
