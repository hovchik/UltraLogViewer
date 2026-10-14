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
