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
        var (w, _) = NewWindow();
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
        await Until(() => grid.ExpansionForTest(errRow).Contains("… 3 framework frames"));
        Assert.Contains("com.shop.Api.handle", grid.ExpansionForTest(errRow));
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
}
