using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using UltraLogViewer.Core;

namespace UltraLogViewer.Desktop;

/// <summary>Small modal dialogs built in code.</summary>
public static class Dialogs
{
    private static Window Shell(string title, Control content, double width = 420) => new()
    {
        Title = title,
        Width = width,
        SizeToContent = SizeToContent.Height,
        CanResize = false,
        ShowInTaskbar = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        Content = new Border { Padding = new Thickness(18, 14), Child = content },
    };

    private static StackPanel Buttons(params Button[] buttons)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        foreach (var b in buttons) sp.Children.Add(b);
        return sp;
    }

    /// <summary>Asks for one line of text. Returns null when cancelled.</summary>
    public static async Task<string?> Prompt(Window owner, string title, string label, string initial = "", string? watermark = null, Func<string, string?>? validate = null)
    {
        var box = new TextBox { Text = initial, Watermark = watermark, MinWidth = 300 };
        var error = new TextBlock { Foreground = Brushes.IndianRed, FontSize = 12, IsVisible = false, TextWrapping = TextWrapping.Wrap };
        var ok = new Button { Content = "OK", IsDefault = true, Classes = { "accent" } };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var panel = new StackPanel { Spacing = 8, Children = { new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, box, error, Buttons(ok, cancel) } };
        var w = Shell(title, panel);
        ok.Click += (_, _) =>
        {
            var v = box.Text ?? "";
            if (validate?.Invoke(v) is { } msg)
            {
                error.Text = msg;
                error.IsVisible = true;
                return;
            }
            w.Close(v);
        };
        cancel.Click += (_, _) => w.Close(null);
        w.Opened += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        return await w.ShowDialog<string?>(owner);
    }

    public static async Task<bool> Confirm(Window owner, string title, string message, string okText = "OK")
    {
        var ok = new Button { Content = okText, IsDefault = true, Classes = { "accent" } };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var w = Shell(title, new StackPanel { Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, Buttons(ok, cancel) } });
        ok.Click += (_, _) => w.Close(true);
        cancel.Click += (_, _) => w.Close(false);
        return await w.ShowDialog<bool>(owner);
    }

    public static Task Message(Window owner, string title, string message)
    {
        var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true };
        var w = Shell(title, new StackPanel { Children = { new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, Buttons(ok) } });
        ok.Click += (_, _) => w.Close();
        return w.ShowDialog(owner);
    }

    /// <summary>Picks one item from a list. Returns the index, or -1.</summary>
    public static async Task<int> Choose(Window owner, string title, string label, IReadOnlyList<string> items, int selected = 0)
    {
        var list = new ListBox { ItemsSource = items, SelectedIndex = selected, MaxHeight = 320 };
        var ok = new Button { Content = "OK", IsDefault = true, Classes = { "accent" } };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var w = Shell(title, new StackPanel { Spacing = 8, Children = { new TextBlock { Text = label }, list, Buttons(ok, cancel) } });
        ok.Click += (_, _) => w.Close(list.SelectedIndex);
        cancel.Click += (_, _) => w.Close(-1);
        list.DoubleTapped += (_, _) => w.Close(list.SelectedIndex);
        return await w.ShowDialog<int>(owner);
    }

    public static readonly (string Keys, string What)[] Shortcuts =
    [
        ("Ctrl+O / Ctrl+Shift+O", "Open files / open and merge into one timeline"),
        ("Ctrl+F, /", "Find"),
        ("Enter, F3, n / Shift+Enter, Shift+F3, N", "Next / previous match"),
        ("Ctrl+L", "Quick filter (syntax below)"),
        ("Right-click a cell", "Show only or exclude that value, highlight it, add a field as a column"),
        ("c / Esc", "Show the selected entry in context (filters ignored) / back to the filtered view"),
        ("Alt+Z", "Wrap long messages"),
        ("Ctrl+Shift+P, F1", "Command palette"),
        ("Ctrl+Shift+A", "Patterns view"),
        ("Ctrl+K", "Clear (hide everything so far)"),
        ("Ctrl+click a token", "Search for it"),
        ("Ctrl+Alt+S", "Save workspace"),
        ("→ / ←", "Expand / collapse a multi-line entry under its row"),
        ("Right-click the Time header", "Time as written, full, time of day, relative or delta; UTC; gap markers"),
        ("↓ in quick filter", "Suggestions: field names, their top values and recent queries"),
        ("e / E", "Next / previous error"),
        ("w / W", "Next / previous warning"),
        ("Ctrl+B, b, double-click the line number", "Toggle bookmark"),
        ("Ctrl+P", "Pin the row to the strip above the grid"),
        ("Double-click a word, select text in the details", "Highlight it everywhere on screen"),
        ("Click Foo.java:42 in an expanded entry", "Open it in your editor (Edit › Editor and source roots…)"),
        ("Ctrl+click field values", "Pick several values of a field (logger in (a,b))"),
        ("F2 / Shift+F2", "Next / previous bookmark"),
        ("Ctrl+G", "Go to line"),
        ("Ctrl+T, f, End", "Follow new lines (tail)"),
        ("Shift+click, Shift+arrows", "Select a range (status bar shows the time between rows)"),
        ("Ctrl+C", "Copy selected entries"),
        ("Ctrl+E", "Export the current view (.log, .csv, .jsonl)"),
        ("Ctrl+W, Ctrl+Tab", "Close tab, next tab"),
        ("Timeline: click / drag", "Jump to time / filter to a time range"),
        ("Timeline: Group by", "Stack the bars by level, file or any field; click a legend item to filter to it"),
    ];

    /// <summary>The quick-filter syntax, shown under the shortcuts in Help.</summary>
    public static readonly (string Example, string What)[] QuerySyntax =
    [
        ("error timeout", "Entries containing both words"),
        ("a OR b, a | b", "Either; OR binds tighter than AND, so a b OR c means a and (b or c)"),
        ("-word, NOT word", "Without the word"),
        ("(a or b) and not c", "and / or / not in any case, with parentheses"),
        ("\"exact phrase\", /regex/", "A phrase or a regular expression"),
        ("logger:Db, level:error", "A parsed field contains the value (field:\"two words\", field:/re/)"),
        ("status>=500, status!=200", "Compare a field: > >= < <= == != (numbers numerically, other values as text)"),
        ("duration>2s", "Durations with ms, s, m or h; a bare number is read in the unit you typed"),
        ("level>=warn", "Levels compare by severity"),
        ("user in (alice,bob)", "The field equals one of the values"),
        ("logger:com.acme.*, thread:worker-*", "Wildcard: the whole value matches; a.b.* also matches a.b itself"),
        ("exists(traceId), env?=prod", "The field is present / equals the value or is missing"),
        ("since:-3h, until:-10m", "Relative to now; also since:2024-05-01T10:00, since:today"),
    ];

    public static Task Help(Window owner)
    {
        static Grid Table((string Keys, string What)[] rows)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowSpacing = 6, ColumnSpacing = 14 };
            for (var i = 0; i < rows.Length; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var k = new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#808080"), 0.15),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2),
                    Child = new TextBlock { Text = rows[i].Keys, FontFamily = Fonts.Mono, FontSize = 12 },
                };
                var d = new TextBlock { Text = rows[i].What, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetRow(k, i);
                Grid.SetRow(d, i);
                Grid.SetColumn(d, 1);
                grid.Children.Add(k);
                grid.Children.Add(d);
            }
            return grid;
        }
        var ok = new Button { Content = "Close", IsDefault = true, IsCancel = true };
        var body = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Table(Shortcuts),
                new TextBlock { Text = "Quick filter", FontWeight = FontWeight.SemiBold, FontSize = 15, Margin = new Thickness(0, 10, 0, 0) },
                Table(QuerySyntax),
            },
        };
        var w = Shell("Keyboard shortcuts", new StackPanel { Children = { new ScrollViewer { Content = body, MaxHeight = 560 }, Buttons(ok) } }, 680);
        ok.Click += (_, _) => w.Close();
        return w.ShowDialog(owner);
    }

    /// <summary>Define a custom regex format with named groups. Returns null when cancelled.</summary>
    public static async Task<CustomFormat?> CustomFormatEditor(Window owner, IReadOnlyList<string> sample)
    {
        var name = new TextBox { Watermark = "Name, e.g. My service" };
        var pattern = new TextBox
        {
            Watermark = @"^(?<time>\S+ \S+) \[(?<level>\w+)\] (?<message>.*)$",
            FontFamily = Fonts.Mono,
            AcceptsReturn = false,
        };
        var preview = new TextBlock { FontFamily = Fonts.Mono, FontSize = 12, TextWrapping = TextWrapping.Wrap, Opacity = 0.85 };
        var lines = sample.Where(l => l.Trim().Length > 0).Take(5).ToList();
        void Update()
        {
            var p = pattern.Text ?? "";
            if (p.Length == 0)
            {
                preview.Text = "Sample:\n" + string.Join("\n", lines);
                return;
            }
            try
            {
                var f = new CustomRegexFormat(new CustomFormat("preview", p), DateTime.Now.Year);
                var parts = lines.Select(l => f.IsEntryStart(l)
                    ? "✓ " + string.Join("  ", f.Parse(l).Where(kv => kv.Key != "message").Select(kv => $"{kv.Key}={kv.Value}"))
                    : "✗ " + l);
                preview.Text = string.Join("\n", parts);
            }
            catch (ArgumentException e)
            {
                preview.Text = e.Message;
            }
        }
        pattern.TextChanged += (_, _) => Update();
        Update();
        var ok = new Button { Content = "Save and apply", IsDefault = true, Classes = { "accent" } };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var hint = new TextBlock
        {
            Classes = { "hint" },
            Text = "Use named groups. time, level and message are understood; any other group becomes a column. Lines that don't match continue the previous entry.",
        };
        var w = Shell("Custom format", new StackPanel { Spacing = 8, Children = { name, pattern, hint, new Border { Classes = { "sec" }, Child = preview }, Buttons(ok, cancel) } }, 640);
        ok.Click += (_, _) =>
        {
            var n = (name.Text ?? "").Trim();
            var p = pattern.Text ?? "";
            if (n.Length == 0 || p.Length == 0 || Query.Validate(new TextPattern(p, true)) is not null) return;
            w.Close(new CustomFormat(n, p));
        };
        cancel.Click += (_, _) => w.Close(null);
        return await w.ShowDialog<CustomFormat?>(owner);
    }
}
