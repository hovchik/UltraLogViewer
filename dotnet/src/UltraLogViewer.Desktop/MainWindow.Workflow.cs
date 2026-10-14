using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UltraLogViewer.Core;
using UltraLogViewer.Desktop.Controls;

namespace UltraLogViewer.Desktop;

/// <summary>
/// Noise reduction and workflow: the patterns view, group repeats, clear, the live-tail pill, stack-trace folding in
/// the details pane, Ctrl+click search, the command palette and saved workspaces.
/// </summary>
public sealed partial class MainWindow
{
    private readonly PatternsView _patterns = new();
    private readonly TextBlock _patternsHint = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
    private readonly Button _scanAll = new() { Content = "Scan all", Classes = { "mini" }, IsVisible = false };
    private Control _patternsPane = null!;
    private readonly Button _newPill = new()
    {
        Classes = { "accent" },
        IsVisible = false,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Bottom,
        Margin = new Thickness(0, 0, 0, 16),
        Padding = new Thickness(14, 4),
        CornerRadius = new CornerRadius(12),
    };
    private readonly Button _statusCleared = new() { Classes = { "mini" }, Padding = new Thickness(6, 0), MinHeight = 18, FontSize = 12, IsVisible = false };
    private readonly Button _unfoldBtn = new() { Classes = { "mini" }, Margin = new Thickness(6, 2), IsVisible = false };
    private readonly StackPanel _recentWsList = new() { Spacing = 2 };
    private Menu _mainMenu = null!;
    private bool _patternsShown;
    private CancellationTokenSource? _patternsCts;
    private DispatcherTimer? _patternsTimer;
    private string? _workspaceName;

    // ============================================================ layout

    /// <summary>The grid with the patterns view and the "↓ N new" pill layered over it.</summary>
    private Control BuildGridArea()
    {
        var back = new Button { Content = "Back to entries (Esc)", Classes = { "mini" } };
        back.Click += (_, _) => ShowPatterns(false);
        var refresh = new Button { Content = "↻", Classes = { "mini" } };
        ToolTip.SetTip(refresh, "Find patterns again");
        refresh.Click += (_, _) => { if (_active is { } t) SchedulePatterns(t, 0); };
        _scanAll.Click += (_, _) => { if (_active is { } t) SchedulePatterns(t, 0, all: true); };
        var head = new DockPanel { Margin = new Thickness(8, 4), LastChildFill = true };
        var headRight = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _scanAll, refresh, back } };
        DockPanel.SetDock(headRight, Dock.Right);
        head.Children.Add(headRight);
        head.Children.Add(_patternsHint);
        var cols = new Grid { ColumnDefinitions = new ColumnDefinitions($"{PatternsView.ColX[1]},{PatternsView.ColX[2] - PatternsView.ColX[1]},{PatternsView.ColX[3] - PatternsView.ColX[2]},{PatternsView.ColX[4] - PatternsView.ColX[3]},*"), Margin = new Thickness(0, 2) };
        string[] titles = ["Count", "%", "Levels", "Trend", "Pattern"];
        for (var i = 0; i < titles.Length; i++)
        {
            var tb = new TextBlock { Text = titles[i], FontSize = 11.5, FontWeight = FontWeight.SemiBold, Opacity = 0.7, HorizontalAlignment = i < 2 ? HorizontalAlignment.Right : HorizontalAlignment.Left, Margin = new Thickness(i < 2 ? 0 : 0, 0, i < 2 ? 10 : 0, 0) };
            Grid.SetColumn(tb, i);
            cols.Children.Add(tb);
        }
        var scroll = new ScrollViewer { Content = _patterns, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var pane = new DockPanel { Children = { head, cols, scroll } };
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(cols, Dock.Top);
        _patternsPane = new Border { Child = pane, IsVisible = false, Background = Brushes.Transparent };
        _patterns.Pick += (p, only) => AddPatternRule(p, only);
        _patterns.MenuRequested += ShowPatternMenu;
        _patterns.CloseRequested += () => ShowPatterns(false);
        _patterns.SelectionChanged += _ => UpdateDetail();
        _newPill.Click += (_, _) => SetFollow(true);
        return new Panel { Children = { _grid, _patternsPane, _newPill } };
    }

    // ========================================================= patterns

    private void TogglePatterns() => ShowPatterns(!_patternsShown);

    private void ShowPatterns(bool on)
    {
        if (_active is not { } tab) on = false;
        _patternsShown = on;
        _patternsPane.IsVisible = on;
        _grid.IsVisible = !on;
        RefreshChecks();
        if (on)
        {
            _patterns.Focus();
            if (_active is { } t) SchedulePatterns(t, 0);
        }
        else
        {
            _patternsCts?.Cancel();
            _grid.Focus();
        }
        UpdateDetail();
    }

    private void SchedulePatterns(DocumentTab tab, int delayMs = 300, bool all = false)
    {
        if (!_patternsShown || tab != _active) return;
        _patternsTimer?.Stop();
        _patternsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(1, delayMs)) };
        _patternsTimer.Tick += (_, _) =>
        {
            _patternsTimer?.Stop();
            Run(() => RefreshPatternsAsync(tab, all));
        };
        _patternsTimer.Start();
    }

    private async Task RefreshPatternsAsync(DocumentTab tab, bool all = false)
    {
        _patternsCts?.Cancel();
        var cts = _patternsCts = new CancellationTokenSource();
        _patternsHint.Text = "Finding patterns…";
        tab.PatternsTime = DateTime.UtcNow;
        PatternStats stats;
        try
        {
            stats = await tab.Doc.PatternsAsync(all ? Math.Max(200_000, tab.Info.ViewCount) : 200_000, 300, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (cts.IsCancellationRequested || tab != _active) return;
        tab.Patterns = stats;
        _patterns.Data = stats;
        _patternsHint.Text = $"{stats.Total:N0} {(stats.Total == 1 ? "pattern" : "patterns")} in {stats.Scanned:N0} of {stats.ViewCount:N0} entries{(stats.Sampled ? " (sample)" : "")}";
        _scanAll.IsVisible = stats.Sampled;
    }

    private void AddPatternRule(PatternInfo p, bool only)
    {
        if (_active is not { } tab || p.Template == "(other)") return;
        tab.Filter.Rules.Add(new FilterRule { Text = p.Template, PatternMatch = true, Mode = only ? RuleMode.Include : RuleMode.Exclude });
        if (only) ShowPatterns(false);
        ApplyFilter();
    }

    private void ShowPatternMenu(PatternInfo p)
    {
        var menu = new ContextMenu { Placement = PlacementMode.Pointer };
        menu.Items.Add(Item("Only this pattern", () => AddPatternRule(p, true)));
        menu.Items.Add(Item("Exclude this pattern", () => AddPatternRule(p, false)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Copy pattern", () => Run(() => CopyText(p.Template))));
        menu.Items.Add(Item("Copy sample", () => Run(() => CopyText(p.Sample))));
        menu.Open(_patterns);
    }

    private async Task CopyText(string text)
    {
        if (Clipboard is { } cb) await cb.SetTextAsync(text);
        Toast("Copied");
    }

    /// <summary>The details pane while the patterns view is up: the selected pattern and an example.</summary>
    private bool UpdatePatternDetail()
    {
        if (!_patternsShown) return false;
        var p = _patterns.Current;
        _detailFields.Children.Clear();
        _unfoldBtn.IsVisible = false;
        _detailBar.Text = p is null ? "" : $"Pattern   ·   {p.Count:N0} entries";
        _detail.Text = p is null ? "" : $"{p.Template}\n\nExample:\n{p.Sample}";
        return true;
    }

    // ===================================================== group repeats

    private MenuItem GroupMenu()
    {
        var m = new MenuItem { Header = "_Group repeats" };
        foreach (var (mode, title) in new[] { ("off", "Off"), ("exact", "Identical entries"), ("pattern", "Same pattern") })
            m.Items.Add(Check(title, () => (_active?.Filter.GroupRepeats ?? "off") == mode, () => SetGroupRepeats(mode)));
        return m;
    }

    private void SetGroupRepeats(string mode)
    {
        if (_active is not { } tab) return;
        tab.Filter.GroupRepeats = mode;
        RefreshChecks();
        ApplyFilter();
    }

    // ============================================================ clear

    private void ClearSoFar()
    {
        if (_active is not { Ready: true } tab) return;
        tab.Filter.ClearedBefore = tab.Info.Sources.Select(s => s.Entries).ToArray();
        ApplyFilter();
    }

    private void Unclear()
    {
        if (_active is not { } tab || tab.Filter.ClearedBefore is null) return;
        tab.Filter.ClearedBefore = null;
        ApplyFilter();
    }

    private void UpdateClearedStatus(DocumentTab? tab)
    {
        var n = tab?.Filter.ClearedBefore?.Sum() ?? 0;
        _statusCleared.IsVisible = tab?.Filter.ClearedBefore is not null;
        _statusCleared.Content = $"{n:N0} cleared · Show";
    }

    // ========================================================= live tail

    /// <summary>Rows arrived in the view: count them for the pill while not following, and flash them.</summary>
    private void OnRowsAppended(DocumentTab tab, int before, int after)
    {
        if (after <= before) return;
        if (!tab.Follow) tab.NewRows += after - before;
        if (tab == _active) _grid.Flash(before);
        UpdatePill();
    }

    private void UpdatePill()
    {
        var tab = _active;
        if (tab is not null && (tab.Follow || tab.TopRow + _grid.VisibleRows >= tab.Info.ViewCount)) tab.NewRows = 0;
        var n = tab?.NewRows ?? 0;
        _newPill.IsVisible = n > 0 && !_patternsShown;
        _newPill.Content = $"↓ {n:N0} new";
    }

    // ========================================================== folding

    /// <summary>The details pane's entry text with framework frames folded (unless shown in full for this entry).</summary>
    private string FoldDetail(DocumentTab tab, RowData row, string text)
    {
        _unfoldBtn.IsVisible = false;
        if (!_settings.FoldFrames || tab.Unfolded.Contains(row.Key)) return text;
        var folded = StackFolding.Fold(text.Replace("\r\n", "\n").Split('\n'), _settings.FrameworkPackages);
        var hidden = folded.Sum(f => f.Folded);
        if (hidden == 0) return text;
        _unfoldBtn.Content = $"Show {hidden:N0} hidden lines";
        _unfoldBtn.IsVisible = true;
        return string.Join('\n', folded.Select(f => f.Line));
    }

    private void UnfoldFocused()
    {
        if (_active is not { } tab) return;
        Run(async () =>
        {
            if (await RowAsync(tab, tab.Focus) is not { } row) return;
            tab.Unfolded.Add(row.Key);
            _grid.LayoutChanged();
            UpdateDetail();
        });
    }

    // ================================================== ctrl+click search

    public void SearchFor(string text, int fromRow)
    {
        if (_active is not { } tab) return;
        _search.Text = text;
        tab.Focus = tab.Anchor = fromRow;
        Run(() => RunSearch(true));
    }

    // ==================================================== command palette

    public sealed record PaletteAction(string Label, string Gesture, bool Checked, MenuItem Item);

    /// <summary>Every enabled leaf item of the main menu, labelled with its menu path.</summary>
    internal List<PaletteAction> PaletteActions()
    {
        RebuildRecentMenu();
        var list = new List<PaletteAction>();
        void Walk(IEnumerable<object?> items, string path)
        {
            foreach (var o in items)
            {
                if (o is not MenuItem mi || mi.Header is not string h || !mi.IsEnabled) continue;
                var label = (path.Length > 0 ? path + " › " : "") + h.Replace("__", "\u0001").Replace("_", "").Replace('\u0001', '_');
                if (mi.Items.Count > 0) Walk(mi.Items, label);
                else list.Add(new PaletteAction(label, mi.InputGesture?.ToString() ?? "", mi.ToggleType != MenuItemToggleType.None && mi.IsChecked, mi));
            }
        }
        Walk(_mainMenu.Items, "");
        return list;
    }

    /// <summary>
    /// Palette ranking: every word must occur in the label; labels starting with the query come first, then those
    /// where every word starts a word, then the rest. -1 = no match.
    /// </summary>
    public static int PaletteRank(string label, string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return 2;
        if (!words.All(w => label.Contains(w, StringComparison.OrdinalIgnoreCase))) return -1;
        var last = label.Contains('›') ? label[(label.LastIndexOf('›') + 1)..].Trim() : label;
        if (last.StartsWith(query.Trim(), StringComparison.OrdinalIgnoreCase) || label.StartsWith(query.Trim(), StringComparison.OrdinalIgnoreCase)) return 0;
        var starts = label.Split(new[] { ' ', '›', '/', '(', '-' }, StringSplitOptions.RemoveEmptyEntries);
        return words.All(w => starts.Any(s => s.StartsWith(w, StringComparison.OrdinalIgnoreCase))) ? 1 : 2;
    }

    public static List<T> RankPalette<T>(IReadOnlyList<T> items, Func<T, string> label, string query) =>
        items.Select((a, i) => (a, i, r: PaletteRank(label(a), query))).Where(x => x.r >= 0).OrderBy(x => x.r).ThenBy(x => x.i).Select(x => x.a).ToList();

    internal Window? PaletteWindow { get; private set; }

    private void OpenPalette()
    {
        if (PaletteWindow is not null) return;
        var actions = PaletteActions();
        var box = new TextBox { Watermark = "Type a command", FontSize = 13 };
        var list = new ListBox { MaxHeight = 12 * 28, FontSize = 12.5 };
        list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<PaletteAction>((a, _) => new DockPanel
        {
            Children =
            {
                new TextBlock { Text = a?.Gesture ?? "", Opacity = 0.55, FontSize = 11.5, Margin = new Thickness(16, 0, 0, 0), [DockPanel.DockProperty] = Dock.Right, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = (a?.Checked == true ? "✓ " : "") + (a?.Label ?? ""), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center },
            },
        });
        void Filter()
        {
            var ranked = RankPalette(actions, a => a.Label, box.Text ?? "");
            list.ItemsSource = ranked;
            list.SelectedIndex = ranked.Count > 0 ? 0 : -1;
        }
        Filter();
        var w = new Window
        {
            Title = "Command palette",
            Width = 560,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            SystemDecorations = SystemDecorations.BorderOnly,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Border { Padding = new Thickness(10), Child = new StackPanel { Spacing = 6, Children = { box, list } } },
        };
        void RunSelected()
        {
            if (list.SelectedItem is not PaletteAction a) return;
            w.Close();
            Dispatcher.UIThread.Post(() => a.Item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)));
        }
        box.TextChanged += (_, _) => Filter();
        box.AddHandler(KeyDownEvent, (_, e) =>
        {
            var n = list.ItemCount;
            switch (e.Key)
            {
                case Key.Down when n > 0: list.SelectedIndex = Math.Min(n - 1, list.SelectedIndex + 1); list.ScrollIntoView(list.SelectedIndex); break;
                case Key.Up when n > 0: list.SelectedIndex = Math.Max(0, list.SelectedIndex - 1); list.ScrollIntoView(list.SelectedIndex); break;
                case Key.Enter: RunSelected(); break;
                case Key.Escape: w.Close(); break;
                default: return;
            }
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        list.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if ((e.Source as Control)?.FindAncestorOfType<ListBoxItem>(true) is not null) RunSelected();
        }, RoutingStrategies.Bubble);
        w.Opened += (_, _) => box.Focus();
        w.Closed += (_, _) => PaletteWindow = null;
        w.Deactivated += (_, _) => { if (PaletteWindow == w) w.Close(); };
        PaletteWindow = w;
        _ = w.ShowDialog(this);
    }

    // ========================================================= workspaces

    private async Task PickSaveWorkspace()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save workspace",
            DefaultExtension = Workspace.Extension,
            SuggestedFileName = (_workspaceName ?? "workspace") + "." + Workspace.Extension,
            FileTypeChoices = [new FilePickerFileType("UltraLogViewer workspace") { Patterns = ["*." + Workspace.Extension] }],
        });
        if (file?.TryGetLocalPath() is { } path) await SaveWorkspaceAsync(path);
    }

    private async Task PickOpenWorkspace()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open workspace",
            FileTypeFilter = [new FilePickerFileType("UltraLogViewer workspace") { Patterns = ["*." + Workspace.Extension] }, FilePickerFileTypes.All],
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) await OpenWorkspaceAsync(path);
    }

    internal WorkspaceFile BuildWorkspace(string file)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(file)) ?? "";
        var w = new WorkspaceFile { Active = Math.Max(0, _active is null ? 0 : _tabs.IndexOf(_active)), Highlights = _settings.Highlights.ToList() };
        foreach (var t in _tabs)
        {
            var filter = t.Filter.Clone();
            filter.Bookmarks = [];
            filter.ClearedBefore = null;
            var fk = FormatKey(t);
            w.Tabs.Add(new WorkspaceTab
            {
                Paths = t.Paths,
                RelPaths = t.Paths.Select(p => Workspace.Relative(p, dir)).ToArray(),
                Format = t.FormatId,
                FormatKey = fk,
                Encoding = t.Encoding,
                Filter = filter,
                Search = new WorkspaceSearch { Text = t.SearchPattern.Text, Regex = t.SearchPattern.Regex, CaseSensitive = t.SearchPattern.CaseSensitive, WholeWord = t.SearchPattern.WholeWord },
                Bookmarks = t.Bookmarks.Values.Where(b => b.Source < t.Info.Sources.Count)
                    .Select(b => new WorkspaceBookmark { Path = t.Info.Sources[b.Source].Path, Line = b.Line, Note = b.Note }).ToList(),
                ExtraColumns = [.. _settings.ExtraColumns.GetValueOrDefault(fk) ?? []],
                ColumnOrder = [.. _settings.ColumnOrder.GetValueOrDefault(fk) ?? []],
                HiddenColumns = [.. _settings.HiddenColumns],
            });
        }
        return w;
    }

    public async Task SaveWorkspaceAsync(string file)
    {
        file = Path.GetFullPath(file);
        await File.WriteAllTextAsync(file, Workspace.Serialize(BuildWorkspace(file)));
        _settings.AddRecentWorkspace(file);
        _settings.Save();
        _workspaceName = Path.GetFileName(file);
        UpdateTitle();
        Toast($"Saved {_workspaceName}");
    }

    public async Task OpenWorkspaceAsync(string file)
    {
        file = Path.GetFullPath(file);
        WorkspaceFile ws;
        try
        {
            ws = Workspace.Deserialize(await File.ReadAllTextAsync(file));
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            Toast($"Could not open the workspace: {e.Message}", true);
            return;
        }
        var dir = Path.GetDirectoryName(file) ?? "";
        _settings.Highlights = ws.Highlights;
        HighlightsChanged();
        var opened = new List<(int Index, DocumentTab Tab)>();
        var skipped = 0;
        for (var i = 0; i < ws.Tabs.Count; i++)
        {
            var wt = ws.Tabs[i];
            var paths = Workspace.Resolve(wt, dir);
            skipped += wt.Paths.Length - paths.Length;
            if (paths.Length == 0) continue;
            // bookmarks and column settings go where opening a file reads them from
            foreach (var g in wt.Bookmarks.GroupBy(b => b.Path))
            {
                var target = paths.FirstOrDefault(p => Path.GetFileName(p) == Path.GetFileName(g.Key)) ?? g.Key;
                var saved = _settings.Bookmarks.GetValueOrDefault(target) ?? [];
                saved.RemoveAll(s => g.Any(b => b.Line == s.Line));
                saved.AddRange(g.Select(b => new SavedBookmark { Line = b.Line, Note = b.Note }));
                _settings.Bookmarks[target] = saved;
            }
            if (wt.FormatKey.Length > 0)
            {
                if (wt.ExtraColumns.Count > 0) _settings.ExtraColumns[wt.FormatKey] = [.. wt.ExtraColumns];
                if (wt.ColumnOrder.Count > 0) _settings.ColumnOrder[wt.FormatKey] = [.. wt.ColumnOrder];
            }
            foreach (var h in wt.HiddenColumns)
                if (!_settings.HiddenColumns.Contains(h)) _settings.HiddenColumns.Add(h);
            var filter = wt.Filter ?? new FilterSpec();
            filter.Bookmarks = [];
            var tab = await OpenTab(paths, wt.Encoding, null, t =>
            {
                t.FormatId = wt.Format;
                t.Filter = filter;
                t.SearchPattern = new TextPattern(wt.Search.Text, wt.Search.Regex, wt.Search.CaseSensitive, wt.Search.WholeWord);
            });
            if (tab is not null) opened.Add((i, tab));
        }
        if (skipped > 0) Toast($"Skipped {skipped} missing file(s)", true);
        _settings.AddRecentWorkspace(file);
        _settings.Save();
        _workspaceName = Path.GetFileName(file);
        var active = opened.FirstOrDefault(o => o.Index == ws.Active).Tab ?? opened.LastOrDefault().Tab;
        if (active is null)
        {
            UpdateTitle();
            return;
        }
        Activate(active);
        if (active.SearchPattern.Text.Length > 0) await RunSearch(false);
    }

    private void UpdateTitle() =>
        Title = _workspaceName is { } ws ? $"{ws} — UltraLogViewer" : _active is null ? "UltraLogViewer" : $"{_active.Title} — UltraLogViewer";

    private void RebuildRecentWorkspaces()
    {
        _recentWsList.Children.Clear();
        foreach (var p in _settings.RecentWorkspaces.Take(5))
        {
            var b = new Button
            {
                Content = new TextBlock { Text = p, TextTrimming = TextTrimming.PathSegmentEllipsis },
                Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(8, 3),
            };
            b.Click += (_, _) => Run(() => OpenWorkspaceAsync(p));
            _recentWsList.Children.Add(b);
        }
        _recentWsList.IsVisible = _settings.RecentWorkspaces.Count > 0;
        _recentWsTitle.IsVisible = _recentWsList.IsVisible;
    }

    private readonly TextBlock _recentWsTitle = new() { Text = "WORKSPACES", Classes = { "h3" }, Margin = new Thickness(0, 12, 0, 0) };

    internal bool PatternsShownForTest => _patternsShown;
    internal PatternsView PatternsForTest => _patterns;
    internal Button NewPillForTest => _newPill;
    internal void TogglePatternsForTest() => TogglePatterns();
    internal void ClearSoFarForTest() => ClearSoFar();
    internal void OpenPaletteForTest() => OpenPalette();
    internal void SetGroupRepeatsForTest(string mode) => SetGroupRepeats(mode);
    internal void CloseAllTabsForTest()
    {
        foreach (var t in _tabs.ToList()) CloseTab(t);
    }
}
