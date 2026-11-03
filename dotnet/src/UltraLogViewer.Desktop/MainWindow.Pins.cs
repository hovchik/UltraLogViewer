using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using UltraLogViewer.Core;

namespace UltraLogViewer.Desktop;

/// <summary>
/// Pinned rows: entries kept in a compact strip above the grid (a request start, the first error), so they stay in
/// sight while scrolling. Click one to jump to it, × to unpin. Saved per file next to the bookmarks, and in workspaces.
/// </summary>
public sealed partial class MainWindow
{
    private readonly StackPanel _pinList = new() { Spacing = 0 };
    private Border? _pinStrip;

    private Control BuildPinStrip()
    {
        _pinStrip = new Border
        {
            IsVisible = false,
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = new SolidColorBrush(Color.Parse("#808080"), 0.35),
            Child = new ScrollViewer { MaxHeight = 5 * 21 + 2, Content = _pinList, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
        };
        return _pinStrip;
    }

    private void TogglePinAtFocus()
    {
        if (_active is not { } tab) return;
        Run(async () =>
        {
            if (await RowAsync(tab, tab.Focus) is { } row) TogglePin(row);
        });
    }

    public bool IsPinned(RowData row) => _active?.Pins.Any(p => p.Key == row.Key) == true;

    public void TogglePin(RowData row)
    {
        if (_active is not { } tab) return;
        if (tab.Pins.RemoveAll(p => p.Key == row.Key) == 0)
            tab.Pins.Add(new Bookmark { Key = row.Key, Line = row.Line, Source = row.Source, Preview = Preview(row.Text), Row = row.Row });
        SavePins(tab);
        RenderPins();
    }

    private void Unpin(DocumentTab tab, Bookmark pin)
    {
        tab.Pins.Remove(pin);
        SavePins(tab);
        RenderPins();
    }

    private void JumpToPin(DocumentTab tab, Bookmark pin)
    {
        if (tab.Doc.RowForKey(pin.Key) is int r) _grid.Select(r, center: true);
        else Toast("That pinned entry is hidden by the current filter");
    }

    private void UpdatePinRows(DocumentTab tab)
    {
        foreach (var p in tab.Pins) p.Row = tab.Doc.RowForKey(p.Key);
        if (tab == _active) RenderPins();
    }

    private void RenderPins()
    {
        _pinList.Children.Clear();
        var tab = _active;
        if (_pinStrip is null) return;
        _pinStrip.IsVisible = tab is { Pins.Count: > 0 };
        if (tab is null) return;
        var p = Palette.Current;
        foreach (var pin in tab.Pins.OrderBy(x => x.Row ?? int.MaxValue).ThenBy(x => x.Source).ThenBy(x => x.Line))
        {
            var close = new Button { Content = "×", Classes = { "mini" }, Padding = new Thickness(6, 0), MinHeight = 0, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(close, "Unpin");
            close.Click += (_, e) =>
            {
                e.Handled = true;
                Unpin(tab, pin);
            };
            var where = tab.Info.Merged && pin.Source < tab.Info.Sources.Count ? $"{tab.Info.Sources[pin.Source].DisplayName}:{pin.Line}" : $"{pin.Line}";
            var line = new TextBlock { Text = where, FontFamily = Fonts.Mono, FontSize = 12, Foreground = p.Fg3, MinWidth = 60, TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            var text = new TextBlock { Text = pin.Preview, FontFamily = Fonts.Mono, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            var row = new DockPanel
            {
                Background = Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.Hand),
                Height = 21,
                Opacity = pin.Row is null ? 0.5 : 1,
                Children = { close, new TextBlock { Text = "📌", FontSize = 11, Margin = new Thickness(6, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center }, line, text },
            };
            DockPanel.SetDock(close, Dock.Right);
            ToolTip.SetTip(row, pin.Row is null ? "Hidden by the current filter" : "Click to jump to this entry");
            row.PointerPressed += (_, e) =>
            {
                if (e.Handled || !e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) return;
                e.Handled = true;
                JumpToPin(tab, pin);
            };
            _pinList.Children.Add(new Border { Background = p.Bg2, Child = row });
        }
    }

    private void SavePins(DocumentTab tab)
    {
        if (tab.IsPartial) return; // line numbers of a time window are not the file's
        for (var i = 0; i < tab.Info.Sources.Count; i++)
        {
            var path = tab.Info.Sources[i].Path;
            var lines = tab.Pins.Where(p => p.Source == i).Select(p => p.Line).Distinct().Order().ToList();
            if (lines.Count == 0) _settings.Pins.Remove(path);
            else _settings.Pins[path] = lines;
        }
        _settings.Save();
    }

    private void RestorePins(DocumentTab tab)
    {
        for (var i = 0; i < tab.Info.Sources.Count; i++)
        {
            if (!_settings.Pins.TryGetValue(tab.Info.Sources[i].Path, out var lines)) continue;
            foreach (var line in lines)
            {
                if (tab.Doc.KeyForLine(i, line) is not { } key || tab.Pins.Any(p => p.Key == key)) continue;
                var row = tab.Doc.RowForKey(key, nearest: true);
                var text = row is int r ? tab.Doc.GetRows(r, 1).FirstOrDefault(x => x.Key == key)?.Text ?? "" : "";
                tab.Pins.Add(new Bookmark { Key = key, Line = line, Source = i, Preview = Preview(text), Row = tab.Doc.RowForKey(key) });
            }
        }
        if (tab == _active) RenderPins();
    }

    /// <summary>Workspace: the pins of a tab, by file path.</summary>
    private static List<WorkspaceBookmark> WorkspacePins(DocumentTab t) =>
        t.Pins.Where(p => p.Source < t.Info.Sources.Count).Select(p => new WorkspaceBookmark { Path = t.Info.Sources[p.Source].Path, Line = p.Line }).ToList();

    /// <summary>Workspace: puts a tab's pins where opening its files reads them from.</summary>
    private void StageWorkspacePins(WorkspaceTab wt, string[] paths)
    {
        foreach (var g in wt.Pins.GroupBy(b => b.Path))
        {
            var target = paths.FirstOrDefault(p => Path.GetFileName(p) == Path.GetFileName(g.Key)) ?? g.Key;
            _settings.Pins[target] = (_settings.Pins.GetValueOrDefault(target) ?? []).Concat(g.Select(b => b.Line)).Distinct().Order().ToList();
        }
    }

    internal IReadOnlyList<string> PinStripForTest => _pinStrip is { IsVisible: true }
        ? _pinList.Children.OfType<Border>().Select(b => string.Join(" ", ((DockPanel)b.Child!).Children.OfType<TextBlock>().Skip(1).Select(t => t.Text))).ToList()
        : [];

    internal void ClickPinForTest(int index) => JumpToPin(_active!, _active!.Pins.OrderBy(x => x.Row ?? int.MaxValue).ThenBy(x => x.Source).ThenBy(x => x.Line).ElementAt(index));
}
