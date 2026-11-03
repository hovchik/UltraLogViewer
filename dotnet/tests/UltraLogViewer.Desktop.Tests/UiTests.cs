using Avalonia;
using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UltraLogViewer.Core;

namespace UltraLogViewer.Desktop.Tests;

public sealed class UiTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ulv-ui-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>log4j-style sample: every 7th entry a warning, every 13th an error with a stack trace, every 11th a JSON payload.</summary>
    private string Sample(string name, int entries, int startSecond = 0)
    {
        var sb = new StringBuilder();
        var t0 = new DateTime(2024, 3, 1, 10, 0, 0);
        for (var i = 0; i < entries; i++)
        {
            var t = t0.AddSeconds(startSecond + i * 2).ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            if (i % 13 == 12)
            {
                sb.Append($"{t} [worker-{i % 4}] ERROR com.shop.PaymentService - Payment {i} failed: java.net.SocketTimeoutException: Read timed out\n");
                sb.Append("\tat com.shop.http.Client.send(Client.java:88)\n\tat com.shop.PaymentService.charge(PaymentService.java:41)\n");
            }
            else if (i % 7 == 6) sb.Append($"{t} [worker-{i % 4}] WARN  com.shop.Cache - Cache miss ratio high for key user:{i}\n");
            else if (i % 11 == 10) sb.Append($"{t} [main] INFO  com.shop.Api - Request done {{\"order\":{i},\"items\":[1,2],\"user\":{{\"id\":\"u{i}\"}}}}\n");
            else sb.Append($"{t} [main] INFO  com.shop.Api - Handled request {i} for 9f1c2e7a-1b2c-4d5e-8f90-1234567890ab\n");
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
        var frame = w.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(Path.Combine(dir, name));
    }

    private static async Task<DocumentTab> Open(MainWindow w, string path)
    {
        await w.OpenPaths([path], false);
        var tab = w.ActiveTab!;
        await Until(() => tab.Ready);
        return tab;
    }

    [AvaloniaFact]
    public async Task Opens_a_file_and_detects_its_format()
    {
        var (w, _) = NewWindow();
        var tab = await Open(w, Sample("app.log", 500));
        Assert.Equal(500, tab.Info.Total);
        Assert.Equal(500, w.LogView.Count);
        Assert.Contains("Timestamped", tab.Info.Sources[0].Format);
        Assert.Contains(tab.Info.Columns, c => c.Key == "logger");
        // rows are fetched for the grid in the background
        await Until(() => w.LogView.GetCached(0) is not null);
        Assert.Equal(1, w.LogView.GetCached(0)!.Line);
        w.Close();
    }

    [AvaloniaFact]
    public async Task Names_the_format_and_opens_plain_text_without_timestamps()
    {
        var (w, settings) = NewWindow();
        await Open(w, Sample("app.log", 50));
        await Until(() => w.FormatStatusForTest == "Timestamped text · auto");
        Assert.Equal("Auto: Timestamped text", w.FormatPickerTextForTest);
        Assert.True(w.HistogramForTest.IsVisible);

        var notes = Path.Combine(_dir, "notes.txt");
        File.WriteAllText(notes, "Release notes for build 4.2\n\nInstalled packages:\n  - libfoo 1.2\nWARNING: disk almost full\nerror: could not copy config.ini\ndone\n");
        var tab = await Open(w, notes);
        Assert.Equal(7, tab.Info.Total);
        var src = tab.Info.Sources[0];
        Assert.Equal(("plain", "Plain text", true), (src.FormatId, src.FormatLabel, src.FormatAuto));
        Assert.Equal(["level", "message"], tab.Info.Columns.Select(c => c.Key));
        Assert.Equal(Level.Error, tab.Doc.GetRows(5, 1)[0].Level);
        await Until(() => w.FormatStatusForTest == "Plain text · auto");
        Assert.Equal("Auto: Plain text", w.FormatPickerTextForTest);
        Assert.Contains("no known log layout", MainWindow.FormatSummary(tab)!.Value.Tip);
        Assert.False(w.HistogramForTest.IsVisible);
        Assert.False(w.TimelineButtonEnabledForTest);
        w.ToggleHistogramForTest(false); // Ctrl+Shift+H does nothing without a timeline
        Assert.True(settings.ShowHistogram);
        Screenshot(w, "plain-text.png");

        // choosing a format by hand is shown as such, and auto goes back to detection
        await w.ChooseFormatForTest("timestamped");
        await Until(() => w.FormatStatusForTest == "Timestamped text");
        Assert.Contains("chosen by you; 0 of 6 sampled lines start an entry", MainWindow.FormatSummary(tab)!.Value.Tip);
        await w.ChooseFormatForTest("");
        await Until(() => w.FormatStatusForTest == "Plain text · auto");
        w.Close();
    }

    [AvaloniaFact]
    public async Task Quick_filter_and_level_toggles_narrow_the_view()
    {
        var (w, _) = NewWindow();
        var tab = await Open(w, Sample("app.log", 260));
        tab.Filter.Query = "level:error";
        await w.ApplyFilterAsync(tab);
        Assert.Equal(260 / 13, tab.Info.ViewCount);
        Assert.Matches(@"^Filtered 260 entries in \d+[.,]\d\d s$", w.ScanCostStatusForTest);

        tab.Filter.Query = "";
        Array.Fill(tab.Filter.Levels, false);
        tab.Filter.Levels[(int)Level.Warn] = true;
        await w.ApplyFilterAsync(tab);
        Assert.True(tab.Info.ViewCount > 0);
        var rows = tab.Doc.GetRows(0, tab.Info.ViewCount);
        Assert.All(rows, r => Assert.Equal(Level.Warn, r.Level));
        w.Close();
    }

    [AvaloniaFact]
    public async Task Typed_queries_report_errors_and_time_shortcuts_count_back_from_the_last_entry()
    {
        var (w, _) = NewWindow();
        var tab = await Open(w, Sample("app.log", 600)); // one entry every 2 s, 20 minutes in all
        var warns = tab.Doc.GetRows(0, 600).Count(r => r.Level == Level.Warn);
        tab.Filter.Query = "level>=warn and not level:error";
        await w.ApplyFilterAsync(tab);
        Assert.Equal(warns, tab.Info.ViewCount);
        tab.Filter.Query = "since:2024-03-01T10:19:00 (logger:Api or logger:Cache)";
        await w.ApplyFilterAsync(tab);
        Assert.Equal(27, tab.Info.ViewCount); // entries 570..599 without the three PaymentService errors

        // A bad query is reported where quick-filter errors show, and the view keeps the last good filter.
        var before = tab.Info.ViewCount;
        tab.Filter.Query = "level>=loud";
        await w.ApplyFilterAsync(tab);
        Assert.Contains("\"loud\" is not a level", w.ToastForTest);
        Assert.StartsWith("Quick filter: ", w.ToastForTest);
        Assert.Equal(before, tab.Info.ViewCount);
        tab.Filter.Query = "";
        await w.ApplyFilterAsync(tab);

        // Operators after a complete field name, and since: values.
        Assert.Contains("level>=", w.Suggestions("level", 5).Select(s => s.Label));
        Assert.Equal(["since:-15m", "since:-1h", "since:-24h", "since:today"], w.Suggestions("since:", 6).Select(s => s.Label));
        Assert.Equal(["level>=error", "level>=warn"], w.Suggestions("level>=", 7).Select(s => s.Label).Where(l => l is "level>=error" or "level>=warn").Order());

        // Time shortcuts: not live, so they count back from the last entry, not from the clock.
        Button Shortcut(string label) => w.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == label);
        Assert.Contains("last entry", ToolTip.GetTip(Shortcut("-15m")) as string);
        Shortcut("-15m").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await Until(() => tab.Info.ViewCount < 600 && tab.BusyProgress is null);
        Assert.Equal(tab.Info.TimeMax - 15 * 60_000, tab.Filter.TimeFrom);
        Assert.Null(tab.Filter.TimeTo);
        Assert.Equal(451, tab.Info.ViewCount); // entries 149..599
        Screenshot(w, "time-shortcuts.png");
        Shortcut("Now").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await Until(() => tab.Filter.TimeTo is not null && tab.BusyProgress is null);
        Assert.Equal(tab.Info.TimeMax, tab.Filter.TimeTo);
        Assert.Equal(451, tab.Info.ViewCount);
        w.Close();
    }

    [AvaloniaFact]
    public async Task Keyboard_jumps_to_next_error_and_bookmarks_persist()
    {
        var (w, settings) = NewWindow();
        var path = Sample("app.log", 100);
        var tab = await Open(w, path);
        w.LogView.Focus();
        w.KeyPressQwerty(PhysicalKey.E, RawInputModifiers.None);
        await Until(() => tab.Focus > 0);
        Assert.Equal(12, tab.Focus); // entry 13 is the first error

        w.KeyPressQwerty(PhysicalKey.B, RawInputModifiers.Control);
        await Until(() => tab.Bookmarks.Count == 1);
        Assert.True(settings.Bookmarks.ContainsKey(path));
        Assert.Equal(tab.Bookmarks.Values.Single().Line, settings.Bookmarks[path][0].Line);

        // reopening the file restores the bookmark
        w.CloseTab(tab);
        var again = await Open(w, path);
        Assert.Single(again.Bookmarks);
        Assert.Equal(12, again.Bookmarks.Values.Single().Row);
        w.Close();
    }

    [AvaloniaFact]
    public async Task Merging_two_files_interleaves_by_time()
    {
        var (w, _) = NewWindow();
        var a = Sample("a.log", 50);
        var b = Sample("b.log", 50, startSecond: 1);
        await w.OpenPaths([a, b], true);
        var tab = w.ActiveTab!;
        await Until(() => tab.Ready);
        Assert.True(tab.Info.Merged);
        Assert.Equal(100, tab.Info.Total);
        Assert.Equal("__source", tab.Info.Columns[0].Key);
        var rows = tab.Doc.GetRows(0, 4);
        Assert.Equal([0, 1, 0, 1], rows.Select(r => r.Source));
        w.Close();
    }

    [AvaloniaFact]
    public async Task Search_finds_matches_and_screenshots_render()
    {
        var (w, _) = NewWindow();
        var tab = await Open(w, Sample("shop-service.log", 4000));
        var search = w.GetVisualDescendants().OfType<TextBox>().First(t => t.Watermark?.ToString()?.StartsWith("Find") == true);
        search.Text = "timed out";
        search.Focus();
        w.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        await Until(() => tab.SearchResult is not null && tab.Focus > 0);
        Assert.Equal(4000 / 13, tab.SearchResult!.Total);
        Assert.Equal(12, tab.Focus);
        Assert.Matches(@"^Searched 4[,.\u00a0\u202f]?000 entries in \d+[.,]\d\d s$", w.ScanCostStatusForTest);

        tab.Bookmarks["0:25"] = new Bookmark { Key = "0:25", Line = 27, Preview = "bookmarked", Row = 25 };
        w.LogView.Select(30, center: false);
        await Until(() => w.LogView.GetCached(30) is not null);
        Screenshot(w, "light.png");

        Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        var quick = w.GetVisualDescendants().OfType<TextBox>().First(t => t.Watermark?.ToString()?.StartsWith("Quick") == true);
        quick.Text = "PaymentService OR Cache";
        quick.Focus();
        w.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        await Until(() => tab.Info.ViewCount < 4000 && tab.BusyProgress is null);
        Assert.Equal(4000 / 13 + 528, tab.Info.ViewCount);
        // The search that runs again over the new view keeps the filter's cost on show.
        await Until(() => w.ScanCostStatusForTest.StartsWith("Filtered 4"));
        await Until(() => w.LogView.GetCached(5) is not null);
        Screenshot(w, "dark-filtered.png");
        Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        w.Close();
    }

    [AvaloniaFact]
    public async Task Search_hits_follow_appended_lines()
    {
        var (w, _) = NewWindow();
        var path = Sample("live.log", 26);
        var tab = await Open(w, path);
        var search = w.GetVisualDescendants().OfType<TextBox>().First(t => t.Watermark?.ToString()?.StartsWith("Find") == true);
        search.Text = "timed out";
        search.Focus();
        w.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        await Until(() => tab.SearchResult is not null);
        Assert.Equal(2, tab.SearchResult!.Total);
        File.AppendAllText(path, "2024-03-01 11:00:00.000 [main] ERROR com.shop.PaymentService - Read timed out again\n");
        await Until(() => tab.SearchResult?.Total == 3);
        w.Close();
    }

    [AvaloniaFact]
    public async Task Value_filters_show_in_context_and_fields_panel()
    {
        var (w, settings) = NewWindow();
        var tab = await Open(w, Sample("shop-service.log", 400));
        await Until(() => tab.FieldStats is not null);
        var logger = tab.FieldStats!.Get("logger")!;
        Assert.Equal("com.shop.Api", logger.Top[0].Value);
        Assert.Contains(tab.FieldStats.Fields, f => f.Key == "thread");

        // "Only Logger = com.shop.Cache" from a cell or the Fields panel
        w.AddValueRule("logger", "com.shop.Cache", RuleMode.Include);
        await Until(() => tab.Info.ViewCount < 400 && tab.BusyProgress is null);
        Assert.All(tab.Doc.GetRows(0, tab.Info.ViewCount), r => Assert.Equal("com.shop.Cache", r.Field("logger")));
        var cacheCount = tab.Info.ViewCount;
        Assert.True(tab.Filter.Rules.Single().Exact);
        await Until(() => tab.FieldStats?.Get("logger")?.Top.Count == 1);

        // show in context: every entry, filtered-out ones dimmed, same entry selected; Esc returns
        w.LogView.Select(3);
        var key = tab.Doc.GetRows(3, 1)[0].Key;
        w.LogView.Focus();
        w.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.None);
        await Until(() => tab.Doc.IgnoreFilter);
        Assert.Equal(400, tab.Info.ViewCount);
        Assert.Equal(key, tab.Doc.GetRows(tab.Focus, 1)[0].Key);
        Assert.Contains(tab.Doc.GetRows(0, 20), r => r.IsContext);
        w.ToggleHistogramForTest(false);
        await Until(() => w.LogView.GetCached(tab.Focus) is not null);
        tab.ExpandedFields.Add("thread");
        w.RefreshSidebarForTest();
        Screenshot(w, "show-in-context.png");
        w.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        await Until(() => !tab.Doc.IgnoreFilter);
        Assert.Equal(cacheCount, tab.Info.ViewCount);
        Assert.Equal(key, tab.Doc.GetRows(tab.Focus, 1)[0].Key);

        // a level value uses the level filter instead of a rule
        tab.Filter.Rules.Clear();
        w.AddValueRule("level", "ERROR", RuleMode.Include);
        await Until(() => tab.Info.ViewCount == 400 / 13 && tab.BusyProgress is null);
        Assert.Empty(tab.Filter.Rules);

        // fields can become columns, and move
        w.AddColumn("order");
        Assert.Contains(w.Columns(tab), c => c.Key == "order");
        Assert.Equal(["order"], settings.ExtraColumns[tab.Info.Sources[0].Format]);
        w.RemoveColumn("order");
        Assert.DoesNotContain(w.Columns(tab), c => c.Key == "order");
        w.Close();
    }

    [AvaloniaFact]
    public async Task Json_fields_become_columns_and_quick_filter_suggests_values()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 300; i++)
            sb.Append($"{{\"@t\":\"2024-03-01T10:{i / 60:00}:{i % 60:00}Z\",\"@l\":\"{(i % 10 == 0 ? "Error" : "Information")}\",\"@m\":\"GET /api/{i % 3} took {i}ms\",\"RequestPath\":\"/api/{i % 3}\",\"StatusCode\":{(i % 10 == 0 ? 500 : 200)},\"http\":{{\"method\":\"GET\"}}}}\n");
        var path = Path.Combine(_dir, "api.json");
        File.WriteAllText(path, sb.ToString());
        var (w, settings) = NewWindow();
        var tab = await Open(w, path);
        await Until(() => tab.FieldStats is not null);
        Assert.Equal(("200", 270), tab.FieldStats!.Get("StatusCode")!.Top[0]);
        Assert.Equal(3, tab.FieldStats.Get("RequestPath")!.Distinct);
        Assert.NotNull(tab.FieldStats.Get("http.method"));

        // field names, then values, with counts
        var names = w.Suggestions("Stat", 4);
        Assert.Equal("StatusCode:", names.Single().Label);
        Assert.False(names.Single().Commit);
        var values = w.Suggestions("-StatusCode:", 12);
        Assert.Equal(["-StatusCode:200", "-StatusCode:500"], values.Select(v => v.Label));
        Assert.Equal("-StatusCode:200 ", values[0].Text);
        Assert.Equal(["level:error", "level:info"], w.Suggestions("x level:", 8).Select(v => v.Label));

        var quick = w.GetVisualDescendants().OfType<TextBox>().First(t => t.Watermark?.ToString()?.StartsWith("Quick") == true);
        quick.Focus();
        quick.Text = "StatusCode:5";
        quick.CaretIndex = quick.Text.Length;
        await Until(() => w.SuggestionsOpen);
        Assert.Equal("StatusCode:500", w.CurrentSuggestions.Single().Label);
        w.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        w.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        await Until(() => tab.Info.ViewCount == 30 && tab.BusyProgress is null);
        Assert.Equal("StatusCode:500 ", quick.Text);
        Assert.Equal("StatusCode:500", settings.QueryHistory[0]);
        quick.Text = "";
        await Until(() => w.SuggestionsOpen);
        Assert.Equal("StatusCode:500", w.CurrentSuggestions[0].Label);

        w.AddColumn("StatusCode");
        w.AddColumn("RequestPath");
        Assert.Equal(["time", "level", "logger", "StatusCode", "RequestPath", "message"], w.Columns(tab).Select(c => c.Key));
        await Until(() => w.LogView.GetCached(2) is not null);
        w.LogView.Focus();
        Screenshot(w, "json-columns.png");
        w.Close();
    }

    [AvaloniaFact]
    public async Task Display_refresh_wraps_expands_and_formats_time()
    {
        var sb = new StringBuilder();
        var t0 = new DateTime(2024, 3, 1, 10, 0, 0);
        for (var i = 0; i < 60; i++)
        {
            var t = t0.AddSeconds(i * 2 + (i >= 20 ? 30 : 0)).ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            var msg = (i % 6) switch
            {
                0 => $"GET https://shop.example.com/api/orders/{i}?expand=items status=200 took {i * 3}ms from 10.0.4.{i}:51234",
                1 => $"user=alice order={i} total=19.99 paid=true id=9f1c2e7a-1b2c-4d5e-8f90-1234567890ab",
                2 => $"Wrote snapshot to /var/lib/shop/snapshots/{i}.bin in 1.5s (\"compact\" mode)",
                3 => "Cache refresh finished; " + string.Join(" ", Enumerable.Range(0, 40).Select(k => $"region{k}=ok")),
                4 => $"Retrying payment {i}: attempt 2 of 5, backoff 250ms",
                _ => $"Handled request {i} at 2024-03-01T10:00:{i % 60:00}Z",
            };
            var level = i % 9 == 8 ? "ERROR" : i % 5 == 4 ? "WARN " : i % 7 == 3 ? "DEBUG" : "INFO ";
            sb.Append($"{t} [main] {level} com.shop.Api - {msg}\n");
            if (i == 8) sb.Append("java.lang.IllegalStateException: order 8 was already paid\n\tat com.shop.PaymentService.charge(PaymentService.java:41)\n\tat com.shop.Api.handle(Api.java:120)\n");
        }
        var path = Path.Combine(_dir, "display.log");
        File.WriteAllText(path, sb.ToString());
        var (w, settings) = NewWindow();
        Assert.False(settings.TintRows);
        Assert.True(settings.TokenColors);
        var tab = await Open(w, path);
        var grid = w.GridForTest;
        await Until(() => grid.GetCached(30) is not null && grid.LayoutForTest.Count > 20);
        Assert.Equal(20, grid.RowHeight);
        Assert.Equal(64, w.HistogramForTest.Bounds.Height, 0.5);
        Screenshot(w, "display-stripe-tokens.png");

        // inline expand of the multi-line entry with → and ←
        w.LogView.Focus();
        grid.Select(8);
        var key = grid.GetCached(8)!.Key;
        w.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
        Assert.Contains(key, tab.ExpandedRows);
        await Until(() => grid.LayoutForTest.Any(b => b.Row == 8 && b.H > 40));
        w.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
        Assert.Empty(tab.ExpandedRows);
        await Until(() => grid.LayoutForTest.Single(b => b.Row == 8).H == 20);

        // soft wrap with Alt+Z: the long message spans several lines
        w.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Alt);
        Assert.True(settings.Wrap);
        w.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
        await Until(() => grid.LayoutForTest.Any(b => b.Row == 3 && b.H > 30));
        Assert.Equal(20, grid.LayoutForTest.Single(b => b.Row == 4).H);
        Screenshot(w, "display-wrap-expand.png");
        w.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Alt);
        Assert.False(settings.Wrap);

        // time column modes
        w.SetDisplayForTest(s => s.TimeMode = "delta");
        Assert.Equal("", grid.TimeTextForTest(0));
        Assert.Equal("+2.000 s", grid.TimeTextForTest(1));
        Assert.Equal("+32.000 s", grid.TimeTextForTest(20));
        w.SetDisplayForTest(s => s.TimeMode = "relative");
        Assert.Equal("0 ms", grid.TimeTextForTest(8));
        Assert.Equal("-16.000 s", grid.TimeTextForTest(0));
        w.SetDisplayForTest(s => { s.TimeMode = "time"; s.TimeUtc = false; });
        Assert.Equal("10:00:02.000", grid.TimeTextForTest(1));
        w.SetDisplayForTest(s => s.TimeMode = "original");
        Assert.Equal("2024-03-01 10:00:02.000", grid.TimeTextForTest(1));

        // comfortable density, delta time, details docked on the right
        w.SetDisplayForTest(s => { s.Density = "comfortable"; s.TimeMode = "delta"; });
        Assert.Equal(26, grid.RowHeight);
        var before = grid.Bounds.Width;
        w.SetDetailDockForTest(true);
        Assert.Equal("right", settings.DetailDock);
        await Until(() => grid.Bounds.Width < before - 300);
        Screenshot(w, "details-right.png");
        // a saved width wider than the window still leaves room for the grid
        w.SetDetailDockForTest(false);
        settings.DetailWidth = 5000;
        w.SetDetailDockForTest(true);
        await Until(() => grid.Bounds.Width > 150);
        w.SetDetailDockForTest(false);
        await Until(() => Math.Abs(grid.Bounds.Width - before) < 1);
        w.Close();
    }

    [AvaloniaFact]
    public async Task Patterns_repeats_folding_clear_palette_and_workspaces()
    {
        var sb = new StringBuilder();
        var t0 = new DateTime(2024, 3, 1, 10, 0, 0);
        string T(int s) => t0.AddSeconds(s).ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        var n = 0;
        for (var i = 0; i < 40; i++)
        {
            sb.Append($"{T(n++)} [main] INFO  com.shop.Api - Handled request {i} for 9f1c2e7a-1b2c-4d5e-8f90-1234567890ab in {i * 3}ms\n");
            if (i % 10 == 0)
                for (var k = 0; k < 4; k++) sb.Append($"{T(n++)} [main] WARN  com.shop.Cache - Cache miss for key user:7\n");
            if (i % 13 == 5)
            {
                sb.Append($"{T(n++)} [main] ERROR com.shop.PaymentService - Payment {i} failed\n");
                sb.Append("java.lang.IllegalStateException: boom\n\tat com.shop.PaymentService.charge(PaymentService.java:41)\n");
                sb.Append("\tat java.base/java.lang.reflect.Method.invoke(Method.java:580)\n\tat org.springframework.aop.Proxy.invoke(Proxy.java:12)\n\tat org.springframework.web.Servlet.service(Servlet.java:9)\n");
                sb.Append("\tat com.shop.Api.handle(Api.java:120)\n");
            }
        }
        var path = Path.Combine(_dir, "workflow.log");
        File.WriteAllText(path, sb.ToString());
        var (w, settings) = NewWindow();
        var tab = await Open(w, path);
        var grid = w.GridForTest;
        await Until(() => grid.GetCached(5) is not null);

        // patterns view: the request template is the top one; Enter keeps only it
        w.TogglePatternsForTest();
        await Until(() => w.PatternsForTest.Data is not null);
        var top = w.PatternsForTest.Data!.Patterns[0];
        Assert.Equal(("Handled request <num> for <guid> in <num>", 40), (top.Template, top.Count));
        Assert.Contains(w.PatternsForTest.Data.Patterns, p => p.Template == "Cache miss for key user:<num>" && p.Count == 16);
        Screenshot(w, "patterns.png");
        w.PatternsForTest.Focus();
        w.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        await Until(() => !w.PatternsShownForTest && tab.Info.ViewCount == 40 && tab.BusyProgress is null);
        Assert.True(tab.Filter.Rules.Single().PatternMatch);
        tab.Filter.Rules.Clear();
        await w.ApplyFilterAsync(tab);

        // group identical repeats into one row with a count
        w.SetGroupRepeatsForTest("exact");
        await Until(() => tab.Info.ViewCount == tab.Info.Total - 12 && tab.BusyProgress is null);
        await Until(() => grid.GetCached(1)?.Repeat == 4);
        Screenshot(w, "group-repeats.png");
        w.SetGroupRepeatsForTest("off");
        await Until(() => tab.Info.ViewCount == tab.Info.Total && tab.BusyProgress is null);

        // framework frames fold in the inline expansion and the details pane
        var errRow = tab.Doc.GetRows(0, tab.Info.ViewCount).First(r => r.Level == Level.Error).Row;
        grid.Select(errRow);
        w.LogView.Focus();
        w.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
        // A background refresh can re-lay the rows out in between, so wait for both in one go.
        await Until(() => grid.ExpansionForTest(errRow) is var x && x.Contains("… 3 framework frames") && x.Contains("com.shop.Api.handle"));
        await Until(() => grid.BoxForTest(errRow) is { ExpTop: > 0 });
        Screenshot(w, "folded-frames.png");

        // Ctrl+click on the GUID searches for it
        var box = grid.BoxForTest(0)!.Value;
        Point? guidAt = null;
        for (var x = grid.MessageXForTest; x < grid.Bounds.Width && guidAt is null; x += 4)
            if (grid.TokenAtForTest(new Point(x, box.Y + 10)) is { } tk && tk.StartsWith("9f1c2e7a")) guidAt = new Point(x, box.Y + 10);
        Assert.NotNull(guidAt);
        var wp = grid.TranslatePoint(guidAt!.Value, w)!.Value;
        w.MouseDown(wp, MouseButton.Left, RawInputModifiers.Control);
        w.MouseUp(wp, MouseButton.Left, RawInputModifiers.Control);
        await Until(() => tab.SearchResult is { Total: 40 });
        Assert.Equal("9f1c2e7a-1b2c-4d5e-8f90-1234567890ab", tab.SearchPattern.Text);

        // command palette runs a menu action
        w.OpenPaletteForTest();
        await Until(() => w.PaletteWindow is { IsVisible: true });
        var palette = w.PaletteWindow!;
        var paletteBox = palette.GetVisualDescendants().OfType<TextBox>().First();
        paletteBox.Text = "comfortable";
        Dispatcher.UIThread.RunJobs();
        Screenshot(palette, "command-palette.png");
        palette.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        await Until(() => settings.Comfortable);

        // workspace round trip
        tab.Filter.GroupRepeats = "pattern";
        await w.ApplyFilterAsync(tab);
        settings.Highlights.Add(new HighlightRule { Text = "boom", Color = "#2B8A3E" });
        w.ToggleBookmark(grid.GetCached(0) ?? tab.Doc.GetRows(0, 1)[0]);
        var ws = Path.Combine(_dir, "ws", "team.ulvw");
        Directory.CreateDirectory(Path.GetDirectoryName(ws)!);
        await w.SaveWorkspaceAsync(ws);
        Assert.Contains("\"relPaths\"", File.ReadAllText(ws));
        Assert.Contains("../workflow.log", File.ReadAllText(ws));
        w.CloseAllTabsForTest();
        settings.Highlights.Clear();
        settings.Bookmarks.Clear();
        await w.OpenWorkspaceAsync(ws);
        var back = w.ActiveTab!;
        await Until(() => back.Ready && back.BusyProgress is null);
        Assert.Equal("pattern", back.Filter.GroupRepeats);
        Assert.Contains(settings.Highlights, h => h.Text == "boom");
        Assert.Single(back.Bookmarks);
        Assert.Equal("team.ulvw — UltraLogViewer", w.Title);
        Assert.Equal("team.ulvw", Path.GetFileName(settings.RecentWorkspaces[0]));

        // new lines while not following show a pill; clicking it follows
        back.Filter.GroupRepeats = "off";
        await w.ApplyFilterAsync(back);
        grid.Select(0);
        Assert.False(back.Follow);
        var before = back.Info.ViewCount;
        File.AppendAllText(path, $"{T(n++)} [main] INFO  com.shop.Api - Late 1\n{T(n++)} [main] INFO  com.shop.Api - Late 2\n");
        await Until(() => back.Info.ViewCount == before + 2);
        await Until(() => w.NewPillForTest.IsVisible);
        Assert.Equal("↓ 2 new", w.NewPillForTest.Content);
        Screenshot(w, "new-lines-pill.png");
        w.NewPillForTest.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.True(back.Follow);
        Assert.False(w.NewPillForTest.IsVisible);

        // clear hides everything so far; later lines still show up
        w.ClearSoFarForTest();
        await Until(() => back.Info.ViewCount == 0 && back.BusyProgress is null);
        File.AppendAllText(path, $"{T(n++)} [main] INFO  com.shop.Api - After clear\n");
        await Until(() => back.Info.ViewCount == 1);
        w.Close();
    }

    [Fact]
    public void Palette_ranks_prefix_then_word_starts()
    {
        string[] labels = ["View › Wrap long messages", "Edit › Find", "View › Time column › Show in UTC", "File › Open workspace…", "View › Details pane"];
        Assert.Equal(["View › Wrap long messages"], MainWindow.RankPalette(labels, x => x, "wrap"));
        Assert.Equal(["View › Time column › Show in UTC", "Edit › Find"], MainWindow.RankPalette(labels, x => x, "in"));
        Assert.Equal(["File › Open workspace…"], MainWindow.RankPalette(labels, x => x, "op wo"));
        Assert.Equal(5, MainWindow.RankPalette(labels, x => x, "").Count);
    }

    [Fact]
    public void Workspace_paths_fall_back_to_relative()
    {
        var t = new WorkspaceTab { Paths = ["/old/place/a.log", "/old/place/b.log"], RelPaths = ["logs/a.log", "logs/b.log"] };
        var dir = Path.Combine(Path.GetTempPath(), "wsdir");
        var a = Path.GetFullPath(Path.Combine(dir, "logs/a.log"));
        Assert.Equal([a], Workspace.Resolve(t, dir, p => p == a));
        Assert.Equal("logs/a.log", Workspace.Relative(a, dir));
        var json = Workspace.Serialize(new WorkspaceFile { Tabs = [t] });
        Assert.Equal("logs/b.log", Workspace.Deserialize(json).Tabs[0].RelPaths[1]);
        Assert.DoesNotContain("isTrivial", json);
    }

    [Fact]
    public void Histogram_tooltip_names_the_bucket_and_counts()
    {
        var start = new DateTimeOffset(2024, 3, 1, 10, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var h = new Histogram(start, 60_000, [(5, 0, 0), (10, 2, 1)]);
        var local = DateTimeOffset.FromUnixTimeMilliseconds(start + 60_000).ToLocalTime();
        var tip = Controls.HistogramView.Tip(h, 1).Split('\n');
        Assert.Equal($"{local:HH:mm:ss} – {local.AddMinutes(1):HH:mm:ss}", tip[0]);
        Assert.Equal("13 entries", tip[1]);
        Assert.Equal("2 warnings · 1 error", tip[2]);
        Assert.Equal(2, Controls.HistogramView.Tip(h, 0).Split('\n').Length);
    }

    [Fact]
    public void Pretty_prints_embedded_json_and_xml()
    {
        var json = Pretty.Entry("Request done {\"order\":1,\"user\":{\"id\":\"u1\"}}");
        Assert.StartsWith("Request done\n{", json);
        Assert.Contains("\n  \"user\": {\n    \"id\": \"u1\"", json);
        var xml = Pretty.Entry("Payload: <a><b x=\"1\">t</b></a> end");
        Assert.Contains("<a>\n  <b x=\"1\">t</b>\n</a>", xml.Replace("\r", ""));
        var log = Pretty.Entry("2024-05-01 08:00:00 [main] INFO Payload {\"items\":[{\"sku\":\"A-1\"}]}");
        Assert.StartsWith("2024-05-01 08:00:00 [main] INFO Payload\n{\n  \"items\": [", log);
        Assert.Equal("no structure here", Pretty.Entry("no structure here"));
        Assert.Equal("broken {json", Pretty.Entry("broken {json"));
    }

    [AvaloniaFact]
    public async Task Opens_a_time_window_with_a_partial_banner_and_loads_the_whole_file()
    {
        var (w, _) = NewWindow();
        var path = Sample("window.log", 4000); // one entry every 2 s from 10:00
        var t0 = new DateTimeOffset(new DateTime(2024, 3, 1, 10, 0, 0, DateTimeKind.Local)).ToUnixTimeMilliseconds();
        await w.OpenPaths([path], false, new OpenWindow(t0 + 600_000, t0 + 1_200_000));
        var tab = w.ActiveTab!;
        await Until(() => tab.Ready);
        Assert.Equal(301, tab.Info.Total);
        Assert.True(tab.IsPartial);
        Assert.Equal("Showing 2024-03-01 10:10–10:20 · partial file", w.PartialBannerForTest);
        Assert.Contains("partial", Sidebar.SourcesText(tab));
        // A filter survives loading the whole file.
        tab.Filter.Levels[(int)Level.Info] = false;
        await w.ApplyFilterAsync(tab);
        await w.LoadWholeFile();
        tab = w.ActiveTab!;
        await Until(() => tab.Ready);
        Assert.Equal(4000, tab.Info.Total);
        Assert.False(tab.IsPartial);
        Assert.Equal("", w.PartialBannerForTest);
        Assert.False(tab.Filter.Levels[(int)Level.Info]);
        Assert.Contains(w.PaletteActions(), a => a.Label.Contains("Open last"));
        Assert.Contains(w.PaletteActions(), a => a.Label.Contains("Open time range"));
        Assert.Contains(w.PaletteActions(), a => a.Label.Contains("Clear index cache"));
        w.Close();
    }

    private static void SetQuery(MainWindow w, DocumentTab tab, string query)
    {
        w.SetQuickFilterText(query);
        tab.Filter.Query = query;
    }

    [AvaloniaFact]
    public async Task A_superseded_filter_leaves_the_newer_one_its_progress()
    {
        var (w, _) = NewWindow();
        var tab = await Open(w, Sample("slow.log", 200_000));
        // Through the quick filter box too, so its debounce does not later "commit" an empty box over the query.
        SetQuery(w, tab, "/Handled request \\d+3 for/");
        var first = w.ApplyFilterAsync(tab);
        await Until(() => tab.BusyLabel == "Filtering");
        SetQuery(w, tab, "/Handled request \\d+7 for/");
        var second = w.ApplyFilterAsync(tab);
        await first; // returns at once: the second one superseded it
        Assert.False(second.IsCompleted);
        // Still filtering: the status shows the progress, and the tick shows the rows found so far.
        Assert.Equal("Filtering", tab.BusyLabel);
        await second;
        Assert.Equal("", tab.BusyLabel);
        Assert.Null(tab.BusyProgress);
        Assert.True(tab.Info.ViewCount > 0);
        w.Close();
    }

    [AvaloniaFact]
    public async Task Reopens_big_files_from_the_index_cache_and_opens_zips_merged()
    {
        var (w, settings) = NewWindow();
        var path = Sample("big.log", 20_000); // over the 1 MB cache threshold
        var tab = await Open(w, path);
        Assert.Equal("", tab.Info.Sources[0].CacheState);
        await tab.Doc.Sources[0].CacheSave!;
        w.CloseTab(tab);
        tab = await Open(w, path);
        Assert.Equal("hit", tab.Info.Sources[0].CacheState);
        Assert.Contains("cached index", Sidebar.SourcesText(tab));
        Assert.Equal(20_000, tab.Info.Total);
        Assert.True(Directory.Exists(settings.IndexCacheDir));

        var zip = Path.Combine(_dir, "bundle.zip");
        using (var z = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
        {
            System.IO.Compression.ZipFileExtensions.CreateEntryFromFile(z, Sample("a.log", 50), "a.log");
            System.IO.Compression.ZipFileExtensions.CreateEntryFromFile(z, Sample("b.log", 50, startSecond: 1), "nested/b.log");
        }
        tab = await Open(w, zip);
        Assert.True(tab.Info.Merged);
        Assert.Equal(100, tab.Info.Total);
        Assert.Equal(["a.log", "b.log"], tab.Info.Sources.Select(s => s.DisplayName));
        Assert.Equal("zip", tab.Info.Sources[1].Compression);
        w.Close();
    }

    [AvaloniaFact]
    public async Task Duplicated_tabs_share_the_file_and_keep_their_own_filters()
    {
        var (w, _) = NewWindow();
        var path = Sample("dup.log", 260);
        var tab = await Open(w, path);
        tab.Filter.Query = "level:warn";
        await w.ApplyFilterAsync(tab);
        var warns = tab.Info.ViewCount;

        var dup = (await w.DuplicateTab(tab))!;
        Assert.Same(dup, w.ActiveTab);
        Assert.Equal([tab, dup], w.Tabs);
        Assert.Equal("dup.log (2)", dup.Title);
        Assert.Same(tab.Doc.Sources[0], dup.Doc.Sources[0]); // no reindexing
        Assert.Equal(warns, dup.Info.ViewCount); // starts with the same filter
        Assert.Contains(w.PaletteActions(), a => a.Label.Contains("Duplicate tab"));

        w.SetQuickFilterText("level:error");
        dup.Filter.Query = "level:error";
        await w.ApplyFilterAsync(dup);
        Assert.Equal(260 / 13, dup.Info.ViewCount);
        Assert.Equal(warns, tab.Info.ViewCount);

        // Columns are the tab's own: hiding one in the duplicate leaves the original alone.
        w.RemoveColumn("thread");
        Assert.True(w.IsColumnHidden(dup, "thread"));
        Assert.False(w.IsColumnHidden(tab, "thread"));

        // Live tail: the file is read once and both tabs show the new entries.
        File.AppendAllText(path, "2024-03-01 11:00:00.000 [main] ERROR com.shop.Api - Late failure\n2024-03-01 11:00:01.000 [main] WARN  com.shop.Api - Late warning\n");
        await Until(() => dup.Info.Total == 262 && tab.Info.Total == 262 && dup.BusyProgress is null && tab.BusyProgress is null);
        Assert.Equal(260 / 13 + 1, dup.Info.ViewCount);
        Assert.Equal(warns + 1, tab.Info.ViewCount);

        // Closing one tab keeps the other's file open.
        w.CloseTab(tab);
        Assert.False(dup.Doc.Sources[0].Closed);
        Assert.Equal(1, dup.Doc.Set.RefCount);
        Assert.Equal("Late failure", dup.Doc.GetRows(dup.Info.ViewCount - 1, 1)[0].Field("message"));
        var src = dup.Doc.Sources[0];
        w.CloseTab(dup);
        Assert.True(src.Closed);
        w.Close();
    }

    [AvaloniaFact]
    public async Task Split_view_filters_each_pane_and_syncs_the_selection()
    {
        var (w, _) = NewWindow();
        var tab = await Open(w, Sample("split.log", 260));
        var pane = (await w.SplitRight(tab))!;
        Assert.True(w.SplitShownForTest);
        Assert.Same(pane, w.ActiveTab);
        Assert.Same(tab, pane.SplitOf);
        Assert.Equal([tab], w.Tabs); // a pane, not a tab
        Assert.Same(tab.Doc.Sources[0], pane.Doc.Sources[0]);
        Assert.Same(pane, w.SplitGridForTest.Tab);
        Assert.Same(tab, w.MainGridForTest.Tab);

        // Each pane has its own quick filter.
        w.SetPaneQuickForTest(right: false, "level:error");
        await Until(() => tab.Info.ViewCount == 260 / 13 && tab.BusyProgress is null);
        Assert.Same(tab, w.ActiveTab); // typing in a pane's box makes it the active one
        w.SetPaneQuickForTest(right: true, "logger:Cache");
        await Until(() => pane.Info.ViewCount < 260 && pane.BusyProgress is null);
        var warns = pane.Info.ViewCount;
        Assert.Equal(260 / 13, tab.Info.ViewCount);
        Assert.Equal($"{260 / 13} of 260", w.PaneCountForTest(right: false));
        Assert.Equal($"{warns} of 260", w.PaneCountForTest(right: true));

        // Selecting in the left pane moves the right one to the nearest entry by time (errors are not Cache entries).
        w.FocusPaneForTest(right: false);
        w.MainGridForTest.Select(3);
        await Until(() => pane.Focus > 0);
        var picked = tab.Doc.GetRows(3, 1)[0];
        var near = pane.Doc.GetRows(pane.Focus, 1)[0];
        Assert.True(near.Time >= picked.Time && (pane.Focus == 0 || pane.Doc.GetRows(pane.Focus - 1, 1)[0].Time < picked.Time));
        Assert.Same(tab, w.ActiveTab); // the other pane follows without taking the focus

        // The same entry, when the other pane shows it.
        w.SetPaneQuickForTest(right: true, "");
        await Until(() => pane.Info.ViewCount == 260 && pane.BusyProgress is null);
        w.FocusPaneForTest(right: false);
        w.MainGridForTest.Select(5);
        await Until(() => pane.Focus == pane.Doc.RowForKey(tab.Doc.GetRows(5, 1)[0].Key));
        // The details pane follows the focused pane.
        w.FocusPaneForTest(right: true);
        Assert.Same(pane, w.ActiveTab);
        Screenshot(w, "split-view.png");

        // Sync off: the other pane stays where it is.
        w.SetSyncForTest(false);
        var before = tab.Focus;
        w.SplitGridForTest.Select(200);
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(before, tab.Focus);

        Assert.Contains(w.PaletteActions(), a => a.Label.Contains("Split right"));
        w.CloseSplit(tab);
        Assert.False(w.SplitShownForTest);
        Assert.Same(tab, w.ActiveTab);
        Assert.Null(tab.Split);
        Assert.False(tab.Doc.Sources[0].Closed);
        Assert.Equal(1, tab.Doc.Set.RefCount);
        w.Close();
    }

    [AvaloniaFact]
    public void Sources_list_shows_per_file_progress()
    {
        var s = new SourceInfo(0, "/x/a.log", "a.log", 2048, 10, 9, "Timestamped text", "utf-8", false, true, Indexing: true, Progress: 0.42);
        Assert.StartsWith("a.log\nIndexing 42", Sidebar.SourceText(s));
        Assert.DoesNotContain("Indexing", Sidebar.SourceText(s with { Indexing = false, Progress = 1 }));
        Assert.Contains("bzip2", Sidebar.SourceText(s with { Compression = "bzip2", Compressed = true, Live = false }));
    }
}
