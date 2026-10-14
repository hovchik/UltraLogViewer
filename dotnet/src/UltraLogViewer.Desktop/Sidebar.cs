using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using UltraLogViewer.Core;

namespace UltraLogViewer.Desktop;

/// <summary>Left panel: levels, field values, filter rules and presets, time range, highlights, bookmarks and sources.</summary>
public sealed class Sidebar : ScrollViewer
{
    private readonly MainWindow _host;
    private readonly StackPanel _root = new();
    private readonly Dictionary<Level, TextBlock> _levelCounts = [];
    private TextBlock? _sourcesInfo;
    private TextBlock? _timeSpan;
    private Control? _timeInputs;
    private readonly Border _fieldsHost = new();
    private DocumentTab? _tab;

    public static readonly string[] HighlightColors = ["#E8590C", "#D6336C", "#9C36B5", "#1971C2", "#0C8599", "#2F9E44", "#F08C00", "#868E96"];

    public Sidebar(MainWindow host)
    {
        _host = host;
        Width = 290;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Content = _root;
    }

    private static readonly Level[] LevelOrder = [Level.Fatal, Level.Error, Level.Warn, Level.Info, Level.Debug, Level.Trace, Level.None];

    public void Render(DocumentTab? tab)
    {
        _tab = tab;
        _root.Children.Clear();
        _levelCounts.Clear();
        if (tab is null)
        {
            _root.Children.Add(Section("Getting started", new TextBlock
            {
                Classes = { "hint" },
                Text = "Open a log file (Ctrl+O), drop files on the window, or open several at once merged into one timeline (Ctrl+Shift+O).",
            }));
            _root.Children.Add(HighlightsSection());
            return;
        }
        _root.Children.Add(LevelsSection(tab));
        _fieldsHost.Child = FieldsSection(tab);
        _root.Children.Add(_fieldsHost);
        _root.Children.Add(FiltersSection(tab));
        _root.Children.Add(TimeSection(tab));
        _root.Children.Add(HighlightsSection());
        _root.Children.Add(BookmarksSection(tab));
        _root.Children.Add(SourcesSection(tab));
    }

    /// <summary>Cheap refresh for tailing: counts and source sizes only, so input focus is kept.</summary>
    public void UpdateCounts(DocumentTab tab)
    {
        if (tab != _tab) return;
        foreach (var (l, tb) in _levelCounts) tb.Text = tab.Info.LevelCounts[(int)l].ToString("N0", CultureInfo.CurrentCulture);
        if (_sourcesInfo is not null) _sourcesInfo.Text = SourcesText(tab);
        if (_timeSpan is not null) _timeSpan.Text = TimeSpanText(tab);
        SyncTimeInputs(tab);
    }

    // ---------------------------------------------------------------- helpers

    private static Border Section(string title, params Control[] body) => Section(new TextBlock { Classes = { "h3" }, Text = title.ToUpperInvariant() }, body);

    private static Border Section(Control header, params Control[] body)
    {
        var sp = new StackPanel { Spacing = 4 };
        sp.Children.Add(header);
        foreach (var c in body) sp.Children.Add(c);
        return new Border { Classes = { "sec" }, Child = sp };
    }

    private static Control Header(string title, params Control[] right)
    {
        var dp = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var r in right) sp.Children.Add(r);
        DockPanel.SetDock(sp, Dock.Right);
        dp.Children.Add(sp);
        dp.Children.Add(new TextBlock { Classes = { "h3" }, Text = title.ToUpperInvariant(), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0) });
        return dp;
    }

    private static Button Mini(string text, Action onClick, string? tip = null)
    {
        var b = new Button { Classes = { "mini" }, Content = text };
        if (tip is not null) ToolTip.SetTip(b, tip);
        b.Click += (_, e) =>
        {
            e.Handled = true;
            onClick();
        };
        return b;
    }

    private static TextBlock Hint(string text) => new() { Classes = { "hint" }, Text = text };

    // ----------------------------------------------------------------- levels

    private Border LevelsSection(DocumentTab tab)
    {
        var f = tab.Filter;
        var p = Palette.Current;
        var rows = new List<Control>();
        foreach (var l in LevelOrder)
        {
            var i = (int)l;
            if (tab.Info.LevelCounts[i] == 0 && f.Levels[i]) continue;
            var cb = new CheckBox { IsChecked = f.Levels[i], Padding = new Thickness(4, 0), MinHeight = 24 };
            cb.IsCheckedChanged += (_, _) =>
            {
                f.Levels[i] = cb.IsChecked == true;
                _host.ApplyFilter();
            };
            var count = new TextBlock { Text = tab.Info.LevelCounts[i].ToString("N0", CultureInfo.CurrentCulture), Opacity = 0.65, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            _levelCounts[l] = count;
            var only = Mini("≥ only", () =>
            {
                for (var k = 0; k < f.Levels.Length; k++) f.Levels[k] = k >= i || (l == Level.None && k == 0);
                _host.ApplyFilter();
            }, "Show only this level and above");
            var label = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children = { new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Background = p.LevelSwatch(l), VerticalAlignment = VerticalAlignment.Center }, new TextBlock { Text = LevelNames.Display[i], VerticalAlignment = VerticalAlignment.Center } },
            };
            cb.Content = label;
            var dp = new DockPanel();
            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { only, count } };
            DockPanel.SetDock(right, Dock.Right);
            dp.Children.Add(right);
            dp.Children.Add(cb);
            rows.Add(dp);
        }
        return Section(Header("Levels", Mini("All", () =>
        {
            Array.Fill(f.Levels, true);
            _host.ApplyFilter();
        })), [.. rows]);
    }

    // ----------------------------------------------------------------- fields

    /// <summary>Rebuilds only the Fields section (new statistics), keeping input focus elsewhere.</summary>
    public void UpdateFields(DocumentTab tab)
    {
        if (tab == _tab) _fieldsHost.Child = FieldsSection(tab);
    }

    private Border FieldsSection(DocumentTab tab)
    {
        var refresh = Mini("↻", () => _ = _host.RefreshFieldStatsAsync(tab), "Count again");
        var stats = tab.FieldStats;
        var body = new List<Control>();
        if (stats is null)
        {
            body.Add(Hint("Counting values…"));
            return Section(Header("Fields", refresh), [.. body]);
        }
        if (stats.Fields.Count == 0) body.Add(Hint("No parsed fields in this view."));
        else body.Add(Hint($"Top values in {stats.Scanned:N0} of {stats.ViewCount:N0} entries{(stats.Sampled ? " (sample)" : "")}. Click a value to show only it, right-click to exclude it."));
        var p = Palette.Current;
        foreach (var f in stats.Fields)
        {
            var expanded = tab.ExpandedFields.Contains(f.Key);
            var shown = _host.IsColumnShown(tab, f.Key);
            var col = Mini(shown ? "✓ column" : "+ column", () =>
            {
                if (shown) _host.RemoveColumn(f.Key);
                else _host.AddColumn(f.Key);
            }, shown ? "Remove this column" : "Show this field as a column");
            var name = new TextBlock
            {
                Text = (expanded ? "▾ " : "▸ ") + f.Key,
                FontFamily = Fonts.Mono,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var count = new TextBlock
            {
                Text = $"{f.Distinct:N0}{(f.DistinctCapped ? "+" : "")} {(f.Distinct == 1 && !f.DistinctCapped ? "value" : "values")}",
                FontSize = 11,
                Opacity = 0.6,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { count, col } };
            var head = new DockPanel { Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand), MinHeight = 22 };
            DockPanel.SetDock(right, Dock.Right);
            head.Children.Add(right);
            head.Children.Add(name);
            head.PointerPressed += (_, e) =>
            {
                if (e.Handled || !e.GetCurrentPoint(head).Properties.IsLeftButtonPressed) return;
                if (!tab.ExpandedFields.Remove(f.Key)) tab.ExpandedFields.Add(f.Key);
                UpdateFields(tab);
            };
            body.Add(head);
            if (!expanded) continue;
            var max = Math.Max(1, f.Present);
            foreach (var (value, n) in f.Top)
            {
                var label = new TextBlock { Text = value, FontFamily = Fonts.Mono, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                var num = new TextBlock { Text = n.ToString("N0", CultureInfo.CurrentCulture), FontSize = 11, Opacity = 0.65, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
                var bar = new Border { Height = 2, CornerRadius = new CornerRadius(1), Background = p.Accent, HorizontalAlignment = HorizontalAlignment.Left, Opacity = 0.7 };
                var row = new DockPanel { Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand), Margin = new Thickness(14, 0, 0, 0) };
                DockPanel.SetDock(num, Dock.Right);
                DockPanel.SetDock(bar, Dock.Bottom);
                row.Children.Add(num);
                row.Children.Add(bar);
                row.Children.Add(label);
                row.SizeChanged += (_, e) => bar.Width = Math.Max(2, e.NewSize.Width * n / max);
                ToolTip.SetTip(row, $"{value}\n{n:N0} of {f.Present:N0} ({(double)n / max:P0}). Click: only this value. Right-click: exclude it.");
                row.PointerPressed += (_, e) =>
                {
                    var props = e.GetCurrentPoint(row).Properties;
                    if (props.IsLeftButtonPressed) _host.AddValueRule(f.Key, value, RuleMode.Include);
                    else if (props.IsRightButtonPressed) _host.AddValueRule(f.Key, value, RuleMode.Exclude);
                    e.Handled = true;
                };
                body.Add(row);
            }
        }
        return Section(Header("Fields", refresh), [.. body]);
    }

    // ---------------------------------------------------------------- filters

    private Border FiltersSection(DocumentTab tab)
    {
        var f = tab.Filter;
        var s = _host.Settings;
        var presets = new ComboBox { PlaceholderText = "Presets…", MinWidth = 100, FontSize = 12, MinHeight = 22, Padding = new Thickness(6, 1) };
        presets.ItemsSource = s.Presets.Select(x => x.Name).ToList();
        presets.SelectionChanged += (_, _) =>
        {
            if (presets.SelectedIndex < 0 || presets.SelectedIndex >= s.Presets.Count) return;
            var preset = s.Presets[presets.SelectedIndex];
            f.Rules = preset.Rules.Select(r => r.Clone()).ToList();
            f.Query = preset.Query;
            _host.SetQuickFilterText(preset.Query);
            _host.ApplyFilter();
        };
        var body = new List<Control>();
        if (f.Rules.Count == 0) body.Add(Hint("Add include (+) or exclude (−) rules. Entries must match any include and no exclude."));
        var columns = _host.Columns(tab).Where(c => c.Key != "__source").ToList();
        foreach (var r in f.Rules.ToList())
        {
            var en = new CheckBox { IsChecked = r.Enabled, MinWidth = 0, Padding = new Thickness(2, 0) };
            en.IsCheckedChanged += (_, _) =>
            {
                r.Enabled = en.IsChecked == true;
                _host.ApplyFilter();
            };
            var mode = new TextBlock
            {
                Text = r.Mode == RuleMode.Include ? "+" : "−",
                FontWeight = FontWeight.Bold,
                Foreground = r.Mode == RuleMode.Include ? Brushes.SeaGreen : Brushes.IndianRed,
                VerticalAlignment = VerticalAlignment.Center,
                Width = 12,
            };
            var text = new TextBlock
            {
                Text = (r.PatternMatch ? $"Pattern ≈ {(r.Text.Length > 60 ? r.Text[..60] + "…" : r.Text)}" : r.Exact ? $"{columns.FirstOrDefault(c => c.Key == r.Column)?.Title ?? r.Column} = {r.Text}" : r.Regex ? $"/{r.Text}/" : r.Text) + (r.CaseSensitive ? "  Aa" : ""),
                FontFamily = Fonts.Mono,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = r.Enabled ? 1 : 0.5,
            };
            ToolTip.SetTip(text, r.Text);
            var col = new ComboBox { FontSize = 11, MinHeight = 22, Padding = new Thickness(4, 0), Width = 82 };
            ToolTip.SetTip(col, "Match in column");
            var colItems = new List<string> { "any" };
            colItems.AddRange(columns.Select(c => c.Title));
            col.ItemsSource = colItems;
            col.SelectedIndex = r.Column is null ? 0 : columns.FindIndex(c => c.Key == r.Column) + 1;
            col.SelectionChanged += (_, _) =>
            {
                var idx = col.SelectedIndex;
                var key = idx <= 0 ? null : columns[idx - 1].Key;
                if (key == r.Column) return;
                r.Column = key;
                _host.ApplyFilter();
            };
            var del = Mini("×", () =>
            {
                f.Rules.Remove(r);
                _host.ApplyFilter();
            }, "Remove");
            var dp = new DockPanel();
            col.IsVisible = !r.Exact && !r.PatternMatch; // the column is part of the rule's text
            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { col, del } };
            DockPanel.SetDock(right, Dock.Right);
            DockPanel.SetDock(en, Dock.Left);
            DockPanel.SetDock(mode, Dock.Left);
            dp.Children.Add(right);
            dp.Children.Add(en);
            dp.Children.Add(mode);
            dp.Children.Add(text);
            body.Add(dp);
        }

        var input = new TextBox { Watermark = "text or regex", FontFamily = Fonts.Mono, FontSize = 12 };
        var modeBox = new ComboBox { ItemsSource = new[] { "+ include", "− exclude" }, SelectedIndex = 0, FontSize = 12, Width = 108 };
        var regex = new CheckBox { Content = "Regex", FontSize = 12 };
        var cs = new CheckBox { Content = "Match case", FontSize = 12 };
        void Add()
        {
            var t = input.Text ?? "";
            if (t.Length == 0) return;
            var rule = new FilterRule { Text = t, Regex = regex.IsChecked == true, CaseSensitive = cs.IsChecked == true, Mode = modeBox.SelectedIndex == 1 ? RuleMode.Exclude : RuleMode.Include };
            if (Query.Validate(rule.Pattern) is { } err)
            {
                _host.Toast(err, true);
                return;
            }
            f.Rules.Add(rule);
            _host.ApplyFilter();
        }
        input.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Add();
        };
        var addRow = new DockPanel();
        DockPanel.SetDock(modeBox, Dock.Left);
        addRow.Children.Add(modeBox);
        addRow.Children.Add(input);
        var opts = new DockPanel();
        var addBtn = Mini("Add", Add);
        DockPanel.SetDock(addBtn, Dock.Right);
        opts.Children.Add(addBtn);
        opts.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { regex, cs } });

        var ctx = new NumericUpDown { Minimum = 0, Maximum = 50, Value = f.Context, Increment = 1, FormatString = "0", Width = 110, FontSize = 12 };
        ToolTip.SetTip(ctx, "Show N entries around each match (like grep -C)");
        ctx.ValueChanged += (_, _) =>
        {
            var v = (int)(ctx.Value ?? 0);
            if (v == f.Context) return;
            f.Context = v;
            _host.ApplyFilter();
        };
        var bmOnly = new CheckBox { Content = "Bookmarks only", IsChecked = f.BookmarksOnly, FontSize = 12 };
        bmOnly.IsCheckedChanged += (_, _) =>
        {
            f.BookmarksOnly = bmOnly.IsChecked == true;
            _host.ApplyFilter();
        };
        var ctxRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { new TextBlock { Text = "Context lines", VerticalAlignment = VerticalAlignment.Center, FontSize = 12 }, ctx },
        };
        body.Add(addRow);
        body.Add(opts);
        body.Add(ctxRow);
        body.Add(bmOnly);
        return Section(Header("Filters", presets, Mini("Save", () => _ = SavePreset(tab), "Save current filters as a preset")), [.. body]);
    }

    private async Task SavePreset(DocumentTab tab)
    {
        var name = await Dialogs.Prompt(_host, "Save filter preset", "Preset name", "", "e.g. Payment errors");
        if (string.IsNullOrWhiteSpace(name)) return;
        var s = _host.Settings;
        s.Presets.RemoveAll(p => p.Name == name);
        s.Presets.Add(new FilterPreset { Name = name, Rules = tab.Filter.Rules.Select(r => r.Clone()).ToList(), Query = tab.Filter.Query });
        s.Save();
        Render(tab);
        _host.Toast($"Saved preset “{name}”");
    }

    // ------------------------------------------------------------------- time

    private static bool Timeless(DocumentTab tab) =>
        tab.Info is { Indexing: false, Total: > 0, TimeMin: null } && tab.Filter.TimeFrom is null && tab.Filter.TimeTo is null;

    private static string TimeSpanText(DocumentTab tab) =>
        tab.Info.TimeMin is long a && tab.Info.TimeMax is long b ? $"{Timestamps.Format(a)} →\n{Timestamps.Format(b)}"
        : Timeless(tab) ? "No timestamps in this file, so entries stay in file order and time filters don’t apply."
        : "No timestamps detected";

    private void SyncTimeInputs(DocumentTab tab)
    {
        if (_timeInputs is not null) _timeInputs.IsVisible = !Timeless(tab);
    }

    private Border TimeSection(DocumentTab tab)
    {
        var f = tab.Filter;
        var from = new TextBox { Text = f.TimeFrom is long a ? Timestamps.Format(a) : "", Watermark = "yyyy-MM-dd HH:mm:ss", FontFamily = Fonts.Mono, FontSize = 12 };
        var to = new TextBox { Text = f.TimeTo is long b ? Timestamps.Format(b) : "", Watermark = "yyyy-MM-dd HH:mm:ss", FontFamily = Fonts.Mono, FontSize = 12 };
        void Apply()
        {
            long? Parse(TextBox box)
            {
                var t = Timestamps.Parse(box.Text);
                return t == Timestamps.None ? null : t;
            }
            var nf = Parse(from);
            var nt = Parse(to);
            if (nf == f.TimeFrom && nt == f.TimeTo) return;
            f.TimeFrom = nf;
            f.TimeTo = nt;
            _host.ApplyFilter();
        }
        foreach (var box in new[] { from, to })
        {
            box.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                Apply();
            };
            box.LostFocus += (_, _) => Apply();
        }
        Control Row(string label, TextBox box)
        {
            var dp = new DockPanel();
            var l = new TextBlock { Text = label, Width = 38, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
            DockPanel.SetDock(l, Dock.Left);
            dp.Children.Add(l);
            dp.Children.Add(box);
            return dp;
        }
        _timeSpan = Hint(TimeSpanText(tab));
        _timeInputs = new StackPanel { Spacing = 4, Children = { Row("From", from), Row("To", to), Hint("Tip: drag across the timeline to select a range.") } };
        SyncTimeInputs(tab);
        return Section(Header("Time range", Mini("Clear", () =>
        {
            f.TimeFrom = f.TimeTo = null;
            _host.ApplyFilter();
        })), _timeSpan, _timeInputs);
    }

    // ------------------------------------------------------------- highlights

    private Border HighlightsSection()
    {
        var s = _host.Settings;
        var body = new List<Control>();
        void Changed()
        {
            s.Save();
            _host.HighlightsChanged();
            Render(_tab);
        }
        foreach (var r in s.Highlights.ToList())
        {
            var en = new CheckBox { IsChecked = r.Enabled, Padding = new Thickness(2, 0), MinWidth = 0 };
            en.IsCheckedChanged += (_, _) =>
            {
                r.Enabled = en.IsChecked == true;
                Changed();
            };
            var swatch = ColorButton(r.Color, c =>
            {
                r.Color = c;
                Changed();
            });
            var text = new TextBlock
            {
                Text = r.Regex ? $"/{r.Text}/" : r.Text,
                FontFamily = Fonts.Mono,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse(r.Color)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0),
            };
            ToolTip.SetTip(text, r.Text);
            var row = new ToggleButton { Classes = { "mini" }, Content = "row", IsChecked = r.Row, Padding = new Thickness(6, 1), MinHeight = 22, FontSize = 11 };
            ToolTip.SetTip(row, "Color the whole row");
            row.IsCheckedChanged += (_, _) =>
            {
                r.Row = row.IsChecked == true;
                Changed();
            };
            var del = Mini("×", () =>
            {
                s.Highlights.Remove(r);
                Changed();
            }, "Remove");
            var dp = new DockPanel();
            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { row, del } };
            DockPanel.SetDock(right, Dock.Right);
            DockPanel.SetDock(en, Dock.Left);
            DockPanel.SetDock(swatch, Dock.Left);
            dp.Children.Add(right);
            dp.Children.Add(en);
            dp.Children.Add(swatch);
            dp.Children.Add(text);
            body.Add(dp);
        }
        var color = HighlightColors[s.Highlights.Count % HighlightColors.Length];
        var input = new TextBox { Watermark = "word or regex to highlight", FontFamily = Fonts.Mono, FontSize = 12 };
        Button? colorBtn = null;
        colorBtn = ColorButton(color, c =>
        {
            color = c;
            ((Border)colorBtn!.Content!).Background = new SolidColorBrush(Color.Parse(c));
        });
        var regex = new CheckBox { Content = "Regex", FontSize = 12 };
        var whole = new CheckBox { Content = "Whole row", FontSize = 12 };
        void Add()
        {
            var t = input.Text ?? "";
            if (t.Length == 0) return;
            var rule = new HighlightRule { Text = t, Regex = regex.IsChecked == true, Color = color, Row = whole.IsChecked == true };
            if (Query.Validate(rule.Pattern) is { } err)
            {
                _host.Toast(err, true);
                return;
            }
            s.Highlights.Add(rule);
            Changed();
        }
        input.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Add();
        };
        var addRow = new DockPanel();
        DockPanel.SetDock(colorBtn, Dock.Left);
        addRow.Children.Add(colorBtn);
        addRow.Children.Add(input);
        var opts = new DockPanel();
        var addBtn = Mini("Add", Add);
        DockPanel.SetDock(addBtn, Dock.Right);
        opts.Children.Add(addBtn);
        opts.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { regex, whole } });
        body.Add(addRow);
        body.Add(opts);
        return Section("Highlights", [.. body]);
    }

    /// <summary>A swatch button that opens a flyout of preset colors.</summary>
    private static Button ColorButton(string color, Action<string> pick)
    {
        var b = new Button
        {
            Padding = new Thickness(3),
            MinHeight = 0,
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Content = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.Parse(color)) },
        };
        var panel = new WrapPanel { MaxWidth = 150 };
        var flyout = new Flyout { Content = panel };
        foreach (var c in HighlightColors)
        {
            var sw = new Button { Padding = new Thickness(3), Margin = new Thickness(2), Content = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.Parse(c)) } };
            sw.Click += (_, _) =>
            {
                flyout.Hide();
                pick(c);
            };
            panel.Children.Add(sw);
        }
        b.Flyout = flyout;
        return b;
    }

    // -------------------------------------------------------------- bookmarks

    private Border BookmarksSection(DocumentTab tab)
    {
        var body = new List<Control>();
        if (tab.Bookmarks.Count == 0) body.Add(Hint("Ctrl+B or double-click a row to bookmark it. F2 / Shift+F2 jump between bookmarks."));
        foreach (var b in tab.Bookmarks.Values.OrderBy(b => b.Source).ThenBy(b => b.Line))
        {
            var ln = new TextBlock
            {
                Text = (tab.Info.Merged && b.Source < tab.Info.Sources.Count ? tab.Info.Sources[b.Source].DisplayName + ":" : "") + b.Line.ToString(CultureInfo.InvariantCulture),
                FontFamily = Fonts.Mono,
                FontSize = 12,
                Foreground = Palette.Current.Accent,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var text = new TextBlock
            {
                Text = b.Note.Length > 0 ? b.Note : b.Preview,
                FontStyle = b.Note.Length > 0 ? FontStyle.Italic : FontStyle.Normal,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = b.Row is null ? 0.5 : 1,
            };
            ToolTip.SetTip(text, b.Preview);
            var right = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Children = { Mini("✎", () => _ = _host.EditBookmarkNote(b), "Edit note"), Mini("×", () => _host.RemoveBookmark(b), "Remove") },
            };
            var dp = new DockPanel { Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand) };
            DockPanel.SetDock(right, Dock.Right);
            DockPanel.SetDock(ln, Dock.Left);
            dp.Children.Add(right);
            dp.Children.Add(ln);
            dp.Children.Add(text);
            dp.PointerPressed += (_, e) =>
            {
                if (e.Handled) return;
                _host.JumpToBookmark(b);
            };
            body.Add(dp);
        }
        return Section($"Bookmarks ({tab.Bookmarks.Count})", [.. body]);
    }

    // ---------------------------------------------------------------- sources

    private static string SourcesText(DocumentTab tab) => string.Join("\n\n", tab.Info.Sources.Select(s =>
        $"{s.DisplayName}\n{FormatSize(s.Size)} · {s.Lines:N0} lines · {s.Entries:N0} entries\n{s.Format} · {s.Encoding}{(s.Compressed ? " · gzip" : "")}{(s.Live ? "" : " · static")}"));

    public static string FormatSize(long b) => b switch
    {
        < 1024 => $"{b} B",
        < 1024 * 1024 => $"{b / 1024.0:0.0} KB",
        < 1024L * 1024 * 1024 => $"{b / 1024.0 / 1024:0.0} MB",
        _ => $"{b / 1024.0 / 1024 / 1024:0.00} GB",
    };

    private Border SourcesSection(DocumentTab tab)
    {
        _sourcesInfo = new TextBlock { Classes = { "hint" }, Text = SourcesText(tab) };
        ToolTip.SetTip(_sourcesInfo, string.Join("\n", tab.Paths));
        return Section(tab.Paths.Length > 1 ? "Sources (merged by time)" : "Source", _sourcesInfo);
    }
}
