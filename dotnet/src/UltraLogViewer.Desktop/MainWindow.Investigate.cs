using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using UltraLogViewer.Core;

namespace UltraLogViewer.Desktop;

/// <summary>
/// Turning what is on screen into filters: the row and field value menus, "show in context", the top-values
/// statistics behind the Fields panel, user-added columns, and quick-filter suggestions.
/// </summary>
public sealed partial class MainWindow
{
    private CancellationTokenSource? _statsCts;
    private DispatcherTimer? _statsTimer;
    private DocumentTab? _statsPending;

    // ================================================================ columns

    /// <summary>Key under which column choices are remembered: the format name of the first file.</summary>
    private static string FormatKey(DocumentTab tab) => tab.Info.Sources.Count > 0 ? tab.Info.Sources[0].Format : "";

    public IReadOnlyList<ColumnDef> Columns(DocumentTab tab)
    {
        var cols = tab.Info.Columns.ToList();
        var key = FormatKey(tab);
        if (_settings.ExtraColumns.TryGetValue(key, out var extras))
        {
            var at = cols.FindIndex(c => c.Width == 0);
            if (at < 0) at = cols.Count;
            foreach (var k in extras)
            {
                if (cols.Any(c => c.Key.Equals(k, StringComparison.OrdinalIgnoreCase))) continue;
                cols.Insert(at++, new ColumnDef(k, k, 140));
            }
        }
        if (_settings.ColumnOrder.TryGetValue(key, out var order) && order.Count > 0)
        {
            var ordered = order.Select(k => cols.FirstOrDefault(c => c.Key == k)).OfType<ColumnDef>().ToList();
            cols = ordered.Concat(cols.Where(c => !ordered.Contains(c))).ToList();
        }
        return cols;
    }

    private string ColumnTitle(DocumentTab tab, string key) => Columns(tab).FirstOrDefault(c => c.Key == key)?.Title ?? key;

    public bool IsColumnShown(DocumentTab tab, string key) =>
        Columns(tab).Any(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) && !IsColumnHidden(key);

    public void AddColumn(string key)
    {
        if (_active is not { } tab) return;
        _settings.HiddenColumns.Remove(key);
        if (!tab.Info.Columns.Any(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
        {
            var fk = FormatKey(tab);
            if (!_settings.ExtraColumns.TryGetValue(fk, out var list)) _settings.ExtraColumns[fk] = list = [];
            if (!list.Contains(key)) list.Add(key);
        }
        ColumnsChanged(tab);
    }

    /// <summary>Removes a user-added column, or hides a format column.</summary>
    public void RemoveColumn(string key)
    {
        if (_active is not { } tab) return;
        if (_settings.ExtraColumns.TryGetValue(FormatKey(tab), out var list) && list.Remove(key)) { }
        else if (!_settings.HiddenColumns.Contains(key)) _settings.HiddenColumns.Add(key);
        ColumnsChanged(tab);
    }

    private void MoveColumn(DocumentTab tab, string key, int dir)
    {
        var keys = Columns(tab).Select(c => c.Key).ToList();
        var i = keys.IndexOf(key);
        var j = i + dir;
        if (i < 0 || j < 0 || j >= keys.Count) return;
        (keys[i], keys[j]) = (keys[j], keys[i]);
        _settings.ColumnOrder[FormatKey(tab)] = keys;
        ColumnsChanged(tab);
    }

    private void ColumnsChanged(DocumentTab tab)
    {
        _settings.Save();
        _grid.InvalidateVisual();
        _sidebar.Render(tab);
    }

    public void ShowColumnMenu(Control anchor, ColumnDef? col)
    {
        if (_active is not { } tab) return;
        var menu = new ContextMenu { Placement = PlacementMode.Pointer };
        var cols = Columns(tab);
        if (col?.Key == "time")
        {
            foreach (var c in TimeItems(live: false)) menu.Items.Add(c);
            menu.Items.Add(new Separator());
        }
        if (col is not null)
        {
            var i = cols.ToList().IndexOf(col);
            menu.Items.Add(new MenuItem { Header = $"Move “{col.Title}” left", IsEnabled = i > 0, Command = Cmd(() => MoveColumn(tab, col.Key, -1)) });
            menu.Items.Add(new MenuItem { Header = $"Move “{col.Title}” right", IsEnabled = i >= 0 && i < cols.Count - 1, Command = Cmd(() => MoveColumn(tab, col.Key, 1)) });
            menu.Items.Add(new Separator());
        }
        var extras = _settings.ExtraColumns.GetValueOrDefault(FormatKey(tab)) ?? [];
        foreach (var c in cols)
        {
            var item = new MenuItem { Header = c.Title, ToggleType = MenuItemToggleType.CheckBox, IsChecked = !IsColumnHidden(c.Key) };
            ToolTip.SetTip(item, extras.Contains(c.Key) ? "Added field: uncheck to remove" : null);
            item.Click += (_, _) =>
            {
                if (IsColumnHidden(c.Key)) AddColumn(c.Key);
                else RemoveColumn(c.Key);
            };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Reset column widths", () =>
        {
            _settings.ColumnWidths.Clear();
            _settings.Save();
            _grid.InvalidateVisual();
        }));
        menu.Items.Add(Item("Reset column order", () =>
        {
            _settings.ColumnOrder.Remove(FormatKey(tab));
            ColumnsChanged(tab);
        }));
        menu.Open(anchor);
    }

    private sealed class ActionCommand(Action run) : System.Windows.Input.ICommand
    {
#pragma warning disable CS0067 // never raised: always executable
        public event EventHandler? CanExecuteChanged;
#pragma warning restore CS0067
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => run();
    }

    private static ActionCommand Cmd(Action run) => new(run);

    // ============================================================ value menus

    private static string Shorten(string v, int max = 40) => v.Length > max ? v[..max] + "…" : v;

    private static string CellValue(RowData row, string key)
    {
        var v = row.Field(key);
        var nl = v.IndexOf('\n');
        return (nl >= 0 ? v[..nl] : v).Trim();
    }

    public void ShowRowMenu(RowData row, ColumnDef? col)
    {
        if (_active is not { } tab) return;
        var items = new List<Control>();
        if (col is not null && col.Key != "__source") items.AddRange(ValueItems(tab, row, col.Key));
        if (items.Count > 0) items.Add(new Separator());
        if (tab.Doc.IgnoreFilter)
            items.Add(new MenuItem { Header = "Back to filtered view", InputGesture = new KeyGesture(Key.Escape), Command = Cmd(() => Run(() => SetContextMode(false))) });
        else if (tab.HasCustomFilter || tab.Filter.Context > 0)
            items.Add(new MenuItem { Header = "Show in context", InputGesture = new KeyGesture(Key.C), Command = Cmd(() => Run(() => SetContextMode(true))) });
        var shown = Columns(tab).Where(c => !IsColumnHidden(c.Key)).Select(c => c.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var addable = row.Fields.Keys.Where(k => k != "message" && !shown.Contains(k)).ToList();
        if (addable.Count > 0)
        {
            var add = new MenuItem { Header = "Add column" };
            foreach (var k in addable) add.Items.Add(Item(k.Replace("_", "__"), () => AddColumn(k)));
            items.Add(add);
        }
        if (items.Count > 0 && items[^1] is not Separator) items.Add(new Separator());
        if (col is not null && col.Key != "__source" && CellValue(row, col.Key) is { Length: > 0 } cv)
            items.Add(Item("Copy value", () => Run(() => CopyText(cv, "Value copied"))));
        items.Add(Item("Copy entry", () => Run(() => CopyText(row.Text, "Entry copied"))));
        items.Add(new MenuItem { Header = "Toggle bookmark", InputGesture = new KeyGesture(Key.B, KeyModifiers.Control), Command = Cmd(() => ToggleBookmark(row)) });
        var menu = new ContextMenu { Placement = PlacementMode.Pointer };
        foreach (var c in items) menu.Items.Add(c);
        menu.Open(_grid);
    }

    /// <summary>Filter and highlight actions for one field value; shared by grid cells and details-pane fields.</summary>
    private List<Control> ValueItems(DocumentTab tab, RowData row, string key)
    {
        var items = new List<Control>();
        if (key == "level")
        {
            if (row.Level == Level.None) return items;
            var name = LevelNames.Display[(int)row.Level];
            items.Add(Item($"Only {name}", () => OnlyLevel(row.Level)));
            items.Add(Item($"Hide {name}", () => HideLevel(row.Level)));
            return items;
        }
        if (key is "time" or "timestamp")
        {
            if (row.Time is not long t) return items;
            items.Add(Item("Show from this time", () => SetTimeBound(t, from: true)));
            items.Add(Item("Show until this time", () => SetTimeBound(t, from: false)));
            return items;
        }
        if (key == "message") return items;
        var v = CellValue(row, key);
        if (v.Length == 0) return items;
        var title = ColumnTitle(tab, key);
        items.Add(Item($"Only {title} = “{Shorten(v)}”".Replace("_", "__"), () => AddValueRule(key, v, RuleMode.Include)));
        items.Add(Item($"Exclude {title} = “{Shorten(v)}”".Replace("_", "__"), () => AddValueRule(key, v, RuleMode.Exclude)));
        items.Add(Item($"Highlight “{Shorten(v)}”".Replace("_", "__"), () => AddHighlight(v)));
        return items;
    }

    public void AddValueRule(string key, string value, RuleMode mode)
    {
        if (_active is not { } tab) return;
        if (key == "level" && Levels.Parse(value) is var lv and not Level.None)
        {
            if (mode == RuleMode.Include) OnlyLevel(lv);
            else HideLevel(lv);
            return;
        }
        var f = tab.Filter;
        f.Rules.RemoveAll(r => r.Exact && r.Column == key && r.Text == value);
        f.Rules.Add(new FilterRule { Text = value, Column = key, Exact = true, Mode = mode });
        ApplyFilter();
    }

    private void OnlyLevel(Level level)
    {
        if (_active is not { } tab) return;
        for (var i = 0; i < tab.Filter.Levels.Length; i++) tab.Filter.Levels[i] = i == (int)level;
        ApplyFilter();
    }

    private void HideLevel(Level level)
    {
        if (_active is not { } tab) return;
        tab.Filter.Levels[(int)level] = false;
        ApplyFilter();
    }

    private void SetTimeBound(long t, bool from)
    {
        if (_active is not { } tab) return;
        if (from) tab.Filter.TimeFrom = t;
        else tab.Filter.TimeTo = t;
        ApplyFilter();
    }

    private void AddHighlight(string text)
    {
        if (_settings.Highlights.Any(h => !h.Regex && h.Text == text)) return;
        _settings.Highlights.Add(new HighlightRule { Text = text, Color = Sidebar.HighlightColors[_settings.Highlights.Count % Sidebar.HighlightColors.Length] });
        _settings.Save();
        HighlightsChanged();
        _sidebar.Render(_active);
    }

    private async Task CopyText(string text, string toast)
    {
        if (Clipboard is not { } cb) return;
        await cb.SetTextAsync(text);
        Toast(toast);
    }

    // ====================================================== show in context

    /// <summary>Shows every entry with the filtered-out ones dimmed (on), or returns to the filtered view (off).</summary>
    public async Task SetContextMode(bool on)
    {
        if (_active is not { Ready: true } tab || tab.Doc.IgnoreFilter == on) return;
        if (on && !tab.HasCustomFilter && tab.Filter.Context == 0)
        {
            Toast("No filter is active, so every entry is already shown");
            return;
        }
        var key = (await RowAsync(tab, tab.Focus))?.Key;
        tab.Doc.IgnoreFilter = on;
        tab.Info = tab.Doc.Info();
        var row = key is null ? null : tab.Doc.RowForKey(key, nearest: true);
        tab.Focus = tab.Anchor = row ?? Math.Clamp(tab.Focus, 0, Math.Max(0, tab.Info.ViewCount - 1));
        UpdateBookmarkRows(tab);
        if (tab != _active) return;
        _grid.Invalidate();
        _grid.Reveal(tab.Focus, center: true);
        UpdateContextBanner();
        UpdateStatus();
        UpdateDetail();
        _sidebar.Render(tab);
        if (tab.SearchPattern.Text.Length > 0) await RunSearch(false);
        await RefreshOverviewAsync();
        await RefreshHistogramAsync();
        ScheduleFieldStats(tab);
    }

    private void UpdateContextBanner()
    {
        var tab = _active;
        var on = tab?.Doc.IgnoreFilter == true;
        _contextBanner.IsVisible = on;
        if (!on) return;
        var line = _grid.GetCached(tab!.Focus)?.Line;
        _contextText.Text = line is int l
            ? $"Showing every entry around line {l:N0}. Rows your filters hide are dimmed."
            : "Showing every entry. Rows your filters hide are dimmed.";
    }

    private Border BuildContextBanner()
    {
        var back = new Button { Content = "Back to filtered view (Esc)", Classes = { "mini" }, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        back.Click += (_, _) => Run(() => SetContextMode(false));
        var dp = new DockPanel { Margin = new Thickness(10, 4) };
        DockPanel.SetDock(back, Dock.Right);
        dp.Children.Add(back);
        dp.Children.Add(_contextText);
        return new Border
        {
            IsVisible = false,
            Background = new SolidColorBrush(Color.Parse("#2F6FDE"), 0.14),
            BorderBrush = new SolidColorBrush(Color.Parse("#2F6FDE"), 0.45),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = dp,
        };
    }

    // ============================================================ field stats

    /// <summary>Recounts the Fields panel's top values shortly after the view changes (coalesced, cancellable).</summary>
    private void ScheduleFieldStats(DocumentTab tab)
    {
        _statsPending = tab;
        if (_statsTimer is null)
        {
            _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _statsTimer.Tick += (_, _) =>
            {
                _statsTimer.Stop();
                if (_statsPending is { } t) Run(() => RefreshFieldStatsAsync(t));
            };
        }
        _statsTimer.Stop();
        _statsTimer.Start();
    }

    public async Task RefreshFieldStatsAsync(DocumentTab tab)
    {
        if (!tab.Ready || !_tabs.Contains(tab)) return;
        _statsCts?.Cancel();
        var cts = _statsCts = new CancellationTokenSource();
        tab.StatsTime = DateTime.UtcNow;
        FieldStats stats;
        try
        {
            stats = await tab.Doc.FieldStatsAsync(ct: cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (cts.IsCancellationRequested) return;
        tab.FieldStats = stats;
        if (tab == _active) _sidebar.UpdateFields(tab);
    }

    // ===================================================== quick-filter help

    public sealed record Suggestion(string Label, string Detail, string Text, int Caret, bool Commit);

    /// <summary>Suggestions for the quick-filter text at a caret position: field values, field names or history.</summary>
    public List<Suggestion> Suggestions(string text, int caret)
    {
        var list = new List<Suggestion>();
        if (text.Trim().Length == 0)
        {
            foreach (var h in _settings.QueryHistory) list.Add(new Suggestion(h, "recent", h, h.Length, true));
            return list;
        }
        var tab = _active;
        var (start, len) = Query.TokenAt(text, caret);
        var token = text.Substring(start, len);
        if (token.Length == 0 || tab is null) return list;
        var (prefix, field, value) = Query.SplitToken(token);
        string Replace(string with, out int newCaret)
        {
            var rest = text[(start + len)..];
            newCaret = start + with.Length;
            return text[..start] + with + rest;
        }
        if (field is not null)
        {
            IEnumerable<(string Value, int Count)> values;
            if (field.Equals("level", StringComparison.OrdinalIgnoreCase))
                values = Enumerable.Range(1, LevelNames.Count - 1).Reverse()
                    .Where(i => tab.Info.LevelCounts[i] > 0)
                    .Select(i => (LevelNames.Labels[i].ToLowerInvariant(), tab.Info.LevelCounts[i]));
            else values = tab.FieldStats?.Get(field)?.Top ?? [];
            foreach (var (v, count) in values)
            {
                if (value.Length > 0 && !v.Contains(value, StringComparison.OrdinalIgnoreCase)) continue;
                if (v.Equals(value, StringComparison.OrdinalIgnoreCase) && value.Length > 0 && token.EndsWith(v, StringComparison.Ordinal)) continue;
                var tok = prefix + Query.FieldToken(field, v);
                var t = Replace(tok + (start + len < text.Length && text[start + len] == ' ' ? "" : " "), out var c);
                list.Add(new Suggestion(tok, count.ToString("N0", System.Globalization.CultureInfo.CurrentCulture), t, c, true));
                if (list.Count >= 10) break;
            }
            return list;
        }
        var keys = new List<string> { "level" };
        if (tab.FieldStats is { } fs) keys.AddRange(fs.Fields.Select(f => f.Key));
        keys.AddRange(Columns(tab).Select(c => c.Key).Where(k => k is not ("__source" or "message" or "time")));
        foreach (var k in keys.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!k.StartsWith(value, StringComparison.OrdinalIgnoreCase)) continue;
            var t = Replace(prefix + k + ":", out var c);
            list.Add(new Suggestion(prefix + k + ":", "field", t, c, false));
            if (list.Count >= 10) break;
        }
        return list;
    }

    private void UpdateSuggestions()
    {
        if (!_quick.IsFocused || _active is null)
        {
            CloseSuggestions();
            return;
        }
        _quickSuggestions = Suggestions(_quick.Text ?? "", _quick.CaretIndex);
        _quickList.ItemsSource = _quickSuggestions;
        _quickList.SelectedIndex = -1;
        _quickPopup.IsOpen = _quickSuggestions.Count > 0;
    }

    private void CloseSuggestions()
    {
        _quickPopup.IsOpen = false;
        _quickSuggestions = [];
    }

    public bool SuggestionsOpen => _quickPopup.IsOpen;
    public IReadOnlyList<Suggestion> CurrentSuggestions => _quickSuggestions;

    public void AcceptSuggestion(Suggestion sg)
    {
        _quick.Text = sg.Text;
        _quick.CaretIndex = sg.Caret;
        if (sg.Commit)
        {
            CloseSuggestions();
            _quickDebounce.Stop();
            CommitQuickFilter();
            RememberQuery(sg.Text);
        }
        else UpdateSuggestions();
        _quick.Focus();
    }

    private void RememberQuery(string q)
    {
        if (string.IsNullOrWhiteSpace(q)) return;
        _settings.AddQueryHistory(q);
        _settings.Save();
    }

    private void OnQuickKey(object? sender, KeyEventArgs e)
    {
        var open = _quickPopup.IsOpen && _quickSuggestions.Count > 0;
        switch (e.Key)
        {
            case Key.Down:
                if (!open)
                {
                    UpdateSuggestions();
                    if (_quickSuggestions.Count == 0) return;
                }
                _quickList.SelectedIndex = Math.Min(_quickSuggestions.Count - 1, _quickList.SelectedIndex + 1);
                _quickList.ScrollIntoView(_quickList.SelectedIndex);
                break;
            case Key.Up when open:
                _quickList.SelectedIndex = Math.Max(-1, _quickList.SelectedIndex - 1);
                if (_quickList.SelectedIndex >= 0) _quickList.ScrollIntoView(_quickList.SelectedIndex);
                break;
            case Key.Enter or Key.Tab when open && _quickList.SelectedIndex >= 0:
                AcceptSuggestion(_quickSuggestions[_quickList.SelectedIndex]);
                break;
            case Key.Enter:
                CloseSuggestions();
                _quickDebounce.Stop();
                CommitQuickFilter();
                RememberQuery(_quick.Text ?? "");
                break;
            case Key.Escape when open:
                CloseSuggestions();
                break;
            case Key.Escape:
                _grid.Focus();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    // ========================================================== detail fields

    private void RenderDetailFields(DocumentTab tab, RowData row)
    {
        _detailFields.Children.Clear();
        _detailFields.RowDefinitions.Clear();
        var p = Palette.Current;
        var n = 0;
        foreach (var (key, value) in row.Fields)
        {
            if (key == "message" || value.Length == 0) continue;
            _detailFields.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var shown = value.Length > 300 ? value[..300] + "…" : value;
            var k = new TextBlock { Text = key, FontFamily = Fonts.Mono, FontSize = 12.5, Foreground = p.Fg2, Margin = new Thickness(0, 1, 14, 1) };
            var v = new TextBlock { Text = shown, FontFamily = Fonts.Mono, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1) };
            var hit = new Border { Background = Brushes.Transparent, Child = v, Cursor = new Cursor(StandardCursorType.Hand) };
            ToolTip.SetTip(hit, "Right-click to filter by this value");
            void Menu(object? s, ContextRequestedEventArgs e)
            {
                e.Handled = true;
                var items = ValueItems(tab, row, key);
                if (!IsColumnShown(tab, key)) items.Add(Item("Add column", () => AddColumn(key)));
                var copy = CellValue(row, key);
                if (items.Count > 0) items.Add(new Separator());
                items.Add(Item("Copy value", () => Run(() => CopyText(copy, "Value copied"))));
                var menu = new ContextMenu { Placement = PlacementMode.Pointer };
                foreach (var c in items) menu.Items.Add(c);
                menu.Open((Control)s!);
            }
            hit.ContextRequested += Menu;
            k.ContextRequested += Menu;
            Grid.SetRow(k, n);
            Grid.SetRow(hit, n);
            Grid.SetColumn(hit, 1);
            _detailFields.Children.Add(k);
            _detailFields.Children.Add(hit);
            n++;
        }
        _detailFields.IsVisible = n > 0;
    }

    internal void ToggleHistogramForTest(bool on) => ToggleHistogram(on);
    internal void RefreshSidebarForTest() => _sidebar.Render(_active);
}
