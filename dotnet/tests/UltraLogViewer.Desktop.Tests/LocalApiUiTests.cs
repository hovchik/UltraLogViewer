using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using UltraLogViewer.Core;

namespace UltraLogViewer.Desktop.Tests;

/// <summary>Open requests in the real window: command line, links (confirmed), the local HTTP API, the handoff and "Copy link".</summary>
public sealed class LocalApiUiTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ulv-api-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static readonly long Now = DateTimeOffset.Now.ToUnixTimeMilliseconds();

    /// <summary>Every 5th entry an error, every 3rd a warning; one entry per second from 2024-03-01 10:00 local.</summary>
    private string Sample(string name, int entries)
    {
        var sb = new StringBuilder();
        var t0 = new DateTime(2024, 3, 1, 10, 0, 0);
        for (var i = 0; i < entries; i++)
        {
            var level = i % 5 == 4 ? "ERROR" : i % 3 == 2 ? "WARN " : "INFO ";
            sb.Append($"{t0.AddSeconds(i).ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} [main] {level} com.shop.Api - request {i} db={(i % 2 == 0 ? "orders" : "users")}\n");
        }
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    private (MainWindow Window, AppSettings Settings) NewWindow()
    {
        var settings = new AppSettings { FilePath = Path.Combine(_dir, "settings.json") };
        var w = new MainWindow(settings, []) { Width = 1200, Height = 800 };
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

    /// <summary>Runs work off the UI thread (an HTTP call, a second launch) while the UI thread keeps pumping.</summary>
    private static async Task<T> OffUi<T>(Func<Task<T>> fn)
    {
        var t = Task.Run(fn);
        await Until(() => t.IsCompleted, 20_000);
        return await t;
    }

    private static OpenRequest Cli(params string[] args) => OpenRequest.FromCommandLine(CommandLine.Parse(args, Now), Now);

    [AvaloniaFact]
    public async Task Command_line_request_opens_with_filter_levels_search_and_goto()
    {
        var (w, _) = NewWindow();
        var log = Sample("app.log", 100);
        var tab = await w.HandleRequestAsync(Cli("app.log", "--filter", "db=orders", "--levels", "error,warn", "--search", "request 2", "--goto", "2024-03-01 10:00:40"), _dir);
        Assert.NotNull(tab);
        await Until(() => tab!.Ready && tab.BusyProgress is null);
        Assert.Equal([log], tab!.Paths);
        Assert.Equal("db=orders", tab.Filter.Query);
        Assert.Equal([false, false, false, false, true, true, false], tab.Filter.Levels);
        // errors at 4,14,24…, warnings at 2,8,14… with an even number: 2,4,8,14,20,24,26,32,34,38,44,…
        var rows = tab.Doc.GetRows(0, tab.Info.ViewCount);
        Assert.All(rows, r => Assert.Contains("db=orders", r.Text));
        Assert.All(rows, r => Assert.True(r.Level is Level.Warn or Level.Error));
        Assert.Equal("request 2", tab.SearchPattern.Text);
        Assert.NotNull(tab.SearchResult);
        // "go to" a time picks the first row at or after it: entry 44 (10:00:44)
        await Until(() => w.LogView.GetCached(tab.Focus) is not null);
        Assert.Contains("request 44 ", w.LogView.GetCached(tab.Focus)!.Text);

        // the same files again reuse the tab, --new-tab does not
        var again = await w.HandleRequestAsync(Cli(log, "--filter", "db=users"), _dir);
        Assert.Same(tab, again);
        Assert.Equal("db=users", tab.Filter.Query);
        Assert.Single(w.Tabs);
        await w.HandleRequestAsync(Cli(log, "--new-tab"), _dir);
        Assert.Equal(2, w.Tabs.Count);

        // a line in the second file of a merged view
        var other = Sample("other.log", 10);
        var merged = await w.HandleRequestAsync(Cli(log, other, "--merge", "--goto", "other.log:7"), _dir);
        await Until(() => w.LogView.GetCached(merged!.Focus) is not null);
        var sel = w.LogView.GetCached(merged!.Focus)!;
        Assert.Equal((1, 7), (sel.Source, sel.Line));
        w.Close();
    }

    [AvaloniaFact]
    public async Task Link_asks_before_opening_and_names_the_files()
    {
        var (w, _) = NewWindow();
        var log = Sample("app.log", 20);
        List<string>? asked = null;
        w.ConfirmLinkForTest = files =>
        {
            asked = files;
            return false;
        };
        var link = $"ultralogviewer://open?path={Uri.EscapeDataString(log)}&filter=level%3Aerror";
        Assert.Null(await w.HandleRequestAsync(OpenRequest.FromUrl(link, Now), _dir));
        Assert.Equal([log], asked);
        Assert.Empty(w.Tabs);
        // on the command line (how Windows passes a clicked link) it is still a link
        asked = null;
        Assert.Null(await w.HandleRequestAsync(Cli(link), _dir));
        Assert.Equal([log], asked);

        w.ConfirmLinkForTest = _ => true;
        var tab = await w.HandleRequestAsync(OpenRequest.FromUrl(link, Now), _dir);
        Assert.Equal("level:error", tab!.Filter.Query);
        Assert.Equal(4, tab.Info.ViewCount);
        // a link with a relative path or control characters opens nothing and asks nothing
        asked = null;
        w.ConfirmLinkForTest = files => (asked = files) is null;
        Assert.Null(await w.HandleRequestAsync(OpenRequest.FromUrl("ultralogviewer://open?path=app.log", Now), _dir));
        Assert.Null(await w.HandleRequestAsync(OpenRequest.FromUrl("ultralogviewer://open?path=" + Uri.EscapeDataString(log + "\n"), Now), _dir));
        Assert.Null(asked);
        w.Close();
    }

    [AvaloniaFact]
    public async Task Copy_link_round_trips_to_the_same_view()
    {
        var (w, _) = NewWindow();
        var log = Sample("app.log", 60);
        var tab = await w.HandleRequestAsync(Cli(log, "--filter", "db=orders", "--levels", "error,warn", "--last", "30s"), _dir);
        await Until(() => tab!.Ready && tab.BusyProgress is null);
        w.LogView.Select(2);
        await Until(() => w.LogView.GetCached(2) is not null);
        var row = w.LogView.GetCached(2)!;
        var req = MainWindow.ViewRequest(tab!, row);
        Assert.Equal(new GoToTarget(Time: row.Time), req.GoTo);
        var url = req.ToUrl();
        var back = OpenRequest.FromUrl(url, Now);
        Assert.True(back.IsValid, string.Join(";", back.Errors));
        Assert.True(req.SameAs(back), url);
        Assert.Contains("last=30s", url);

        // opening the link shows the same view in the same tab
        w.ConfirmLinkForTest = _ => true;
        w.LogView.Select(0);
        var same = await w.HandleRequestAsync(back, _dir);
        Assert.Same(tab, same);
        Assert.Equal(2, tab!.Focus);
        Assert.Contains(w.PaletteActions(), a => a.Label == "Edit › Copy link to this view");
        Assert.Contains(w.PaletteActions(), a => a.Label == "File › Local API › Enable local HTTP API");
        w.Close();
    }

    [AvaloniaFact]
    public async Task Http_api_opens_a_tab_with_its_filter_and_serves_rows()
    {
        var (w, settings) = NewWindow();
        settings.LocalApi.Port = 0;
        w.StartApi(quiet: true);
        var port = w.ApiPort!.Value;
        var token = settings.LocalApi.Token;
        Assert.Equal(64, token.Length);
        var log = Sample("app.log", 50);
        using var http = new HttpClient();
        HttpRequestMessage Req(HttpMethod m, string path, string? body = null, string? tok = null)
        {
            var r = new HttpRequestMessage(m, $"http://127.0.0.1:{port}{path}");
            r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tok ?? token);
            if (body is not null) r.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return r;
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await OffUi(() => http.SendAsync(Req(HttpMethod.Get, "/tabs", tok: "x".PadRight(64, 'x'))))).StatusCode);
        var open = await OffUi(() => http.SendAsync(Req(HttpMethod.Post, "/open", JsonSerializer.Serialize(new { path = log, filter = "level:error" }))));
        Assert.Equal(HttpStatusCode.OK, open.StatusCode);
        var id = JsonDocument.Parse(await open.Content.ReadAsStringAsync()).RootElement.GetProperty("tab").GetInt32();
        var tab = Assert.Single(w.Tabs);
        Assert.Equal(id, tab.Id);
        Assert.Equal("level:error", tab.Filter.Query);
        Assert.Equal(10, tab.Info.ViewCount);

        var tabs = JsonDocument.Parse(await OffUi(() => http.SendAsync(Req(HttpMethod.Get, "/tabs")).ContinueWith(t => t.Result.Content.ReadAsStringAsync()).Unwrap())).RootElement;
        Assert.Equal(log, tabs[0].GetProperty("paths")[0].GetString());
        Assert.Equal(50, tabs[0].GetProperty("total").GetInt64());
        Assert.Equal(10, tabs[0].GetProperty("shown").GetInt64());
        var rows = await OffUi(() => http.SendAsync(Req(HttpMethod.Get, $"/tabs/{id}/rows?from=1&count=3")).ContinueWith(t => t.Result.Content.ReadAsStringAsync()).Unwrap());
        var lines = rows.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement).ToList();
        Assert.Equal([10, 15, 20], lines.Select(l => l.GetProperty("line").GetInt32()));
        Assert.All(lines, l => Assert.Equal("ERROR", l.GetProperty("level").GetString()));

        // change the filter and go to a line
        Assert.Equal(HttpStatusCode.OK, (await OffUi(() => http.SendAsync(Req(HttpMethod.Post, $"/tabs/{id}/filter", """{"filter":"db=users","levels":"warn"}""")))).StatusCode);
        Assert.Equal("db=users", tab.Filter.Query);
        Assert.Equal(w.ActiveTab, tab);
        Assert.Equal(HttpStatusCode.OK, (await OffUi(() => http.SendAsync(Req(HttpMethod.Post, $"/tabs/{id}/goto", """{"goto":"line:18"}""")))).StatusCode);
        await Until(() => w.LogView.GetCached(tab.Focus) is not null);
        Assert.Equal(18, w.LogView.GetCached(tab.Focus)!.Line);
        Assert.Equal(HttpStatusCode.NotFound, (await OffUi(() => http.SendAsync(Req(HttpMethod.Post, "/tabs/999999/goto", """{"goto":"1"}""")))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await OffUi(() => http.SendAsync(Req(HttpMethod.Post, "/open", """{"path":"relative.log"}""")))).StatusCode);
        w.Close();
        // closing the window stops the API
        await Until(() => !CanConnect(port));
    }

    private static bool CanConnect(int port)
    {
        try
        {
            using var c = new System.Net.Sockets.TcpClient();
            c.Connect(IPAddress.Loopback, port);
            return true;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return false;
        }
    }

    [AvaloniaFact]
    public async Task A_second_launch_hands_its_command_line_to_the_window()
    {
        var (w, _) = NewWindow();
        var pipe = "ulv-ui-" + Guid.NewGuid().ToString("N")[..12];
        w.ListenForLaunches(pipe);
        var log = Sample("app.log", 30);
        var msg = new SingleInstance.Handoff(_dir, ["app.log", "--filter", "level:error"]).ToJson();
        Assert.True(await OffUi(() => Task.FromResult(SingleInstance.TrySend(pipe, msg))));
        await Until(() => w.Tabs.Count == 1 && w.Tabs[0].Ready && w.Tabs[0].Filter.Query == "level:error");
        Assert.Equal([log], w.Tabs[0].Paths); // relative to the second launch's folder
        await Until(() => w.Tabs[0].Info.ViewCount == 6);
        w.Close();
    }
}
