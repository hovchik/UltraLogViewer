using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using UltraLogViewer.Core;

namespace UltraLogViewer.Desktop.Tests;

/// <summary>Pinned rows, the grouped timeline, source links, picked field values and auto-highlight, in the real window.</summary>
public sealed class InvestigationUiTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ulv-inv-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string Sample(string name, int entries)
    {
        var sb = new StringBuilder();
        var t0 = new DateTime(2024, 3, 1, 10, 0, 0);
        for (var i = 0; i < entries; i++)
        {
            var t = t0.AddSeconds(i * 2).ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            if (i % 13 == 12)
            {
                sb.Append($"{t} [worker-{i % 4}] ERROR com.shop.PaymentService - Payment {i} failed: java.net.SocketTimeoutException: Read timed out\n");
                sb.Append("\tat com.shop.http.Client.send(Client.java:88)\n\tat com.shop.PaymentService.charge(PaymentService.java:41)\n");
            }
            else if (i % 7 == 6) sb.Append($"{t} [worker-{i % 4}] WARN  com.shop.Cache - Cache miss ratio high for key user:{i}\n");
            else sb.Append($"{t} [main] INFO  com.shop.Api - Handled request {i}\n");
        }
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    private (MainWindow Window, AppSettings Settings) NewWindow()
    {
        var settings = new AppSettings { FilePath = Path.Combine(_dir, "settings.json") };
        var w = new MainWindow(settings, []) { Width = 1400, Height = 860 };
        w.Show();
        return (w, settings);
    }

    private static async Task Until(Func<bool> cond, int timeoutMs = 10_000)
    {
        var start = Environment.TickCount64;
        while (!cond())
        {
            if (Environment.TickCount64 - start > timeoutMs) throw new TimeoutException("Condition not met in time");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(15);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static void Screenshot(Window w, string name)
    {
        var dir = Environment.GetEnvironmentVariable("ULV_SCREENSHOTS") ?? Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(dir);
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        w.CaptureRenderedFrame()!.Save(Path.Combine(dir, name));
    }

    private static async Task<DocumentTab> Open(MainWindow w, string path)
    {
        await w.OpenPaths([path], false);
        var tab = w.ActiveTab!;
        await Until(() => tab.Ready && tab.BusyProgress is null);
        return tab;
    }

    private static void RunMenu(MainWindow w, string label) =>
        w.PaletteActions().Single(a => a.Label.EndsWith(label, StringComparison.Ordinal)).Item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    [AvaloniaFact]
    public async Task Pins_rows_above_the_grid_and_keeps_them_per_file_and_in_workspaces()
    {
        var (w, settings) = NewWindow();
        var path = Sample("pins.log", 400);
        var tab = await Open(w, path);
        Assert.Empty(w.PinStripForTest);
        w.LogView.Select(12);
        RunMenu(w, "Pin row");
        await Until(() => tab.Pins.Count == 1);
        w.LogView.Select(200);
        RunMenu(w, "Pin row");
        await Until(() => tab.Pins.Count == 2);
        Assert.Equal(2, w.PinStripForTest.Count);
        Assert.StartsWith("13 2024-03-01 10:00:24.000 [worker-0] ERROR", w.PinStripForTest[0]);
        var second = tab.Doc.GetRows(200, 1)[0].Line;
        Assert.Equal([13, second], settings.Pins[path]);
        // the strip stays while the grid scrolls, and a click jumps back
        w.LogView.ScrollTo(int.MaxValue);
        Assert.Equal(2, w.PinStripForTest.Count);
        w.ClickPinForTest(0);
        Assert.Equal(12, tab.Focus);
        Screenshot(w, "pinned-rows.png");

        // filtered out: kept, dimmed, and a click says why it does not jump
        tab.Filter.Levels[(int)Level.Error] = false;
        await w.ApplyFilterAsync(tab);
        Assert.Null(tab.Pins.Single(p => p.Line == 13).Row);
        w.ClickPinForTest(1);
        Assert.Contains("hidden by the current filter", w.ToastForTest);
        tab.Filter.Levels[(int)Level.Error] = true;
        await w.ApplyFilterAsync(tab);

        // reopening the file brings them back
        w.CloseTab(tab);
        tab = await Open(w, path);
        Assert.Equal([13, second], tab.Pins.Select(p => p.Line).Order());
        Assert.Equal(2, w.PinStripForTest.Count);

        // A duplicate and a split pane start with the tab's pins, so pinning there keeps the file's other pins.
        var dup = (await w.DuplicateTab(tab))!;
        Assert.Equal([13, second], dup.Pins.Select(p => p.Line).Order());
        Assert.Equal(2, w.PinStripForTest.Count);
        var third = dup.Doc.GetRows(100, 1)[0];
        w.TogglePin(third);
        Assert.Equal([13, third.Line, second], settings.Pins[path]);
        w.TogglePin(third);
        Assert.Equal([13, second], settings.Pins[path]);
        w.CloseTab(dup);
        Assert.Same(tab, w.ActiveTab);
        var pane = (await w.SplitRight(tab))!;
        Assert.Same(pane, w.ActiveTab);
        Assert.Equal(2, w.PinStripForTest.Count);
        w.TogglePin(third);
        Assert.Equal([13, third.Line, second], settings.Pins[path]);
        w.TogglePin(third);
        w.CloseSplit(tab);
        Assert.Equal([13, second], settings.Pins[path]);

        // and so does a workspace
        var ws = Path.Combine(_dir, "pins.ulvw");
        await w.SaveWorkspaceAsync(ws);
        Assert.Contains("\"pins\"", File.ReadAllText(ws));
        w.CloseAllTabsForTest();
        settings.Pins.Clear();
        await w.OpenWorkspaceAsync(ws);
        tab = w.ActiveTab!;
        await Until(() => tab.Ready && tab.BusyProgress is null);
        Assert.Equal([13, second], tab.Pins.Select(p => p.Line).Order());

        // the row menu unpins
        RunMenu(w, "Pin row"); // focus is row 0: a third pin
        await Until(() => tab.Pins.Count == 3);
        w.TogglePin(tab.Doc.GetRows(0, 1)[0]);
        Assert.Equal(2, tab.Pins.Count);
        w.Close();
    }

    [AvaloniaFact]
    public async Task Groups_the_timeline_by_a_field_and_filters_from_the_legend()
    {
        var (w, _) = NewWindow();
        var tab = await Open(w, Sample("groups.log", 600));
        await Until(() => tab.FieldStats is not null);
        Assert.Equal(["level", "thread", "logger"], w.GroupChoices(tab));
        w.SetHistogramGroup("logger");
        await Until(() => w.HistogramForTest.Groups is not null);
        var g = w.HistogramForTest.Groups!;
        Assert.Equal(["com.shop.Api", "com.shop.Cache", "com.shop.PaymentService"], g.Keys);
        Assert.Equal(600, g.Totals.Sum());
        Screenshot(w, "timeline-grouped.png");
        Assert.Equal([0, 1, 2], w.HistogramForTest.LegendForTest);

        w.HistogramForTest.ClickLegendForTest(1);
        await Until(() => tab.Info.ViewCount == g.Totals[1]);
        Assert.Equal("logger==com.shop.Cache", tab.Filter.Query);
        // the grouping follows the filter
        await Until(() => w.HistogramForTest.Groups is { Keys.Count: 1 });

        w.SetHistogramGroup("level");
        await Until(() => w.HistogramForTest.Groups is null);
        w.Close();
    }

    [AvaloniaFact]
    public async Task Opens_source_locations_under_the_source_roots()
    {
        var (w, settings) = NewWindow();
        var tab = await Open(w, Sample("frames.log", 40));
        var launched = new List<string>();
        w.LaunchForTest = (what, args) => launched.Add(string.Join(" ", new[] { what }.Concat(args)));
        w.LogView.Select(12);
        await Until(() => w.DetailLinksForTest.Count == 2);
        Assert.Equal(["Client.java:88", "PaymentService.java:41"], w.DetailLinksForTest);

        // no source roots: a toast says how to set them
        w.ClickDetailLinkForTest(0);
        Assert.Contains("Editor and source roots", w.ToastForTest);
        Assert.Empty(launched);

        var root = Path.Combine(_dir, "src");
        Directory.CreateDirectory(Path.Combine(root, "com", "shop", "http"));
        File.WriteAllText(Path.Combine(root, "com", "shop", "http", "Client.java"), "class Client {}");
        settings.SourceRoots = [Path.Combine(_dir, "missing"), root];
        w.ClickDetailLinkForTest(0);
        Assert.Equal("vscode://file/" + Path.Combine(root, "com", "shop", "http", "Client.java").Replace('\\', '/').TrimStart('/') + ":88", launched.Single());

        settings.Editor = "custom";
        settings.EditorCommand = "myeditor --line {line} \"{path}\"";
        w.ClickDetailLinkForTest(0);
        Assert.Equal($"myeditor --line 88 {Path.Combine(root, "com", "shop", "http", "Client.java")}", launched[1]);

        // a file:// URL would hand the file to its default program (which runs a script): refused
        settings.EditorCommand = "file://{path}";
        w.ClickDetailLinkForTest(0);
        Assert.Equal(2, launched.Count);
        Assert.Contains("file://", w.ToastForTest);
        w.Close();
    }

    [AvaloniaFact]
    public async Task Picks_several_field_values_and_packages_through_the_quick_filter()
    {
        var (w, _) = NewWindow();
        var tab = await Open(w, Sample("picks.log", 300));
        var counts = tab.Doc.GetRows(0, 300).GroupBy(r => r.Field("logger")).ToDictionary(g => g.Key, g => g.Count());
        w.TogglePick("logger", "com.shop.Api");
        w.TogglePick("logger", "com.shop.Cache");
        await Until(() => tab.Info.ViewCount == counts["com.shop.Api"] + counts["com.shop.Cache"]);
        Assert.Equal("logger in (com.shop.Api,com.shop.Cache)", tab.Filter.Query);
        Assert.Equal(["com.shop.Api", "com.shop.Cache"], w.FieldPicks(tab, "logger").Values);
        Assert.Empty(w.FieldPicks(tab, "logger").Prefixes);
        w.TogglePick("logger", "com.shop.Api");
        await Until(() => tab.Info.ViewCount == counts["com.shop.Cache"]);
        Assert.Equal("logger in (com.shop.Cache)", tab.Filter.Query);

        w.PickPackage("logger", "com.shop.", toggle: false);
        await Until(() => tab.Info.ViewCount == 300);
        Assert.Equal("logger:com.shop.*", tab.Filter.Query);
        w.SetFieldPicks("logger", [], []);
        await Until(() => tab.Filter.Query == "" && tab.BusyProgress is null);

        var tree = Sidebar.Packages([("com.shop.Api", 10), ("com.shop.web.Cart", 4), ("com.shop.web.Pay", 3), ("org.x.Y", 1), ("1.2.3", 9)]);
        Assert.Equal([("com.shop.", 0, 17), ("com.shop.web.", 1, 7)], tree);
        w.Close();
    }

    [AvaloniaFact]
    public async Task Highlights_the_selected_text_on_screen_until_the_selection_clears()
    {
        var (w, _) = NewWindow();
        var tab = await Open(w, Sample("hl.log", 50));
        w.LogView.Select(6);
        await Until(() => w.DetailTextForTest.Contains("Cache miss"));
        var at = w.DetailTextForTest.IndexOf("Cache miss", StringComparison.Ordinal);
        w.SelectDetailTextForTest(at, at + "Cache miss".Length);
        Assert.Equal("Cache miss", w.GridForTest.AutoHighlight);
        Screenshot(w, "auto-highlight.png");
        w.SelectDetailTextForTest(at, at);
        Assert.Null(w.GridForTest.AutoHighlight);
        Assert.Null(MainWindow.AutoHighlightText("x"));
        Assert.Null(MainWindow.AutoHighlightText("two\nlines"));

        // In a split, moving the focus to the other pane leaves no highlight behind in the first one.
        await w.SplitRight(tab);
        w.FocusPaneForTest(right: false);
        w.MainGridForTest.Select(6);
        await Until(() => w.DetailTextForTest.Contains("Cache miss"));
        at = w.DetailTextForTest.IndexOf("Cache miss", StringComparison.Ordinal);
        w.SelectDetailTextForTest(at, at + "Cache miss".Length);
        Assert.Equal("Cache miss", w.MainGridForTest.AutoHighlight);
        w.FocusPaneForTest(right: true);
        Assert.Null(w.MainGridForTest.AutoHighlight);
        Assert.Null(w.SplitGridForTest.AutoHighlight);
        w.Close();
    }
}
