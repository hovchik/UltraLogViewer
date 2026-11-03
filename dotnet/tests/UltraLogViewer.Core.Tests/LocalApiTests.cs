using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace UltraLogViewer.Core.Tests;

/// <summary>The open request (command line, deep links, JSON), the local HTTP API and the single-instance handoff.</summary>
public class LocalApiTests
{
    private static readonly long Now = TestFiles.Local(2024, 5, 1, 12, 0, 0);

    // ============================================================ request parsing

    [Fact]
    public void ParsesTheNewCommandLineSwitches()
    {
        var f = TestFiles.Write("cli.log", TestFiles.Log4j);
        var c = CommandLine.Parse([f, "--filter", "level:error db", "--search=timeout", "--goto", "10:00:03", "--levels", "error,warn",
            "--format", "timestamped", "--since", "-1h", "--merge", "--new-tab"], Now);
        Assert.Empty(c.Errors);
        Assert.Equal([f], c.Files); // the switch values are not taken for files
        var r = OpenRequest.FromCommandLine(c, Now);
        Assert.True(r.IsValid, string.Join(";", r.Errors));
        Assert.Equal(RequestSource.CommandLine, r.Source);
        Assert.Equal("level:error db", r.Filter);
        Assert.Equal("timeout", r.Search);
        Assert.Equal(TestFiles.Local(2024, 5, 1, 10, 0, 3), r.GoTo!.Time);
        Assert.Equal([false, false, false, false, true, true, false], r.Levels!);
        Assert.Equal("timestamped", r.Format);
        Assert.Equal(Now - 3_600_000, r.Window!.Since);
        Assert.True(r.Merge && r.NewTab);

        var bad = OpenRequest.FromCommandLine(CommandLine.Parse(["--levels", "loud", "--goto", "soon", "--filter"], Now), Now);
        Assert.Equal(3, bad.Errors.Count);
        Assert.True(OpenRequest.FromCommandLine(CommandLine.Parse(["-"], Now), Now).Stdin);
    }

    [Fact]
    public void ParsesGoToTargets()
    {
        Assert.Equal(new GoToTarget(Line: 1234), OpenRequest.ParseGoTo("1234", Now));
        Assert.Equal(new GoToTarget(Line: 1234, File: "2"), OpenRequest.ParseGoTo("2:1234", Now));
        Assert.Equal(new GoToTarget(Line: 30, File: "2"), OpenRequest.ParseGoTo("line:2:30", Now)); // "2:30" alone is a time
        Assert.Equal(TestFiles.Local(2024, 5, 1, 2, 30, 0), OpenRequest.ParseGoTo("2:30", Now)!.Time);
        Assert.Equal(new GoToTarget(Line: 12, File: @"C:\logs\app.log"), OpenRequest.ParseGoTo(@"C:\logs\app.log:12", Now));
        Assert.Equal(new GoToTarget(Line: 7, File: "app.log"), OpenRequest.ParseGoTo("app.log:7", Now));
        Assert.Equal(Now - 300_000, OpenRequest.ParseGoTo("-5m", Now)!.Time);
        Assert.Equal(Date(2024, 5, 1, 10, 0, 3, 4), OpenRequest.ParseGoTo("2024-05-01T10:00:03.004Z", Now)!.Time);
        Assert.Null(OpenRequest.ParseGoTo("0", Now));
        Assert.Null(OpenRequest.ParseGoTo("later", Now));
    }

    private static long Date(int y, int mo, int d, int h, int mi, int s, int ms) => new DateTimeOffset(y, mo, d, h, mi, s, ms, TimeSpan.Zero).ToUnixTimeMilliseconds();

    [Fact]
    public void ParsesDeepLinks()
    {
        var r = OpenRequest.FromUrl("ultralogviewer://open?path=%2Fvar%2Flog%2Fapp.log&path=/var/log/b.log&merge=1&since=2024-05-01T10:00:00Z" +
                                    "&last=15m&filter=level%3Aerror+db&levels=error&goto=line:2:30&newtab=1&unknown=x", Now);
        Assert.True(r.IsValid, string.Join(";", r.Errors));
        Assert.Equal(RequestSource.Url, r.Source);
        Assert.Equal(["/var/log/app.log", "/var/log/b.log"], r.Paths);
        Assert.Equal(Date(2024, 5, 1, 10, 0, 0, 0), r.Window!.Since);
        Assert.Null(r.Window.Last); // since wins
        Assert.Equal("level:error db", r.Filter);
        Assert.Equal(new GoToTarget(Line: 30, File: "2"), r.GoTo);
        Assert.True(r.Merge && r.NewTab);
        Assert.True(OpenRequest.FromUrl("ULTRALOGVIEWER:open?path=C:%5Clogs%5Ca.log", Now).IsValid);

        // Untrusted input: links come from any web page.
        Assert.False(OpenRequest.FromUrl("ultralogviewer://open?path=/var/log/a%0A.log", Now).IsValid);
        Assert.False(OpenRequest.FromUrl("ultralogviewer://open?path=/var/log/a%07.log", Now).IsValid);
        Assert.False(OpenRequest.FromUrl("ultralogviewer://open?path=-", Now).IsValid);
        Assert.False(OpenRequest.FromUrl("ultralogviewer://open?path=app.log", Now).IsValid);
        Assert.False(OpenRequest.FromUrl("ultralogviewer://open?path=%5C%5Cevil%5Cshare%5Ca.log", Now).IsValid);
        Assert.False(OpenRequest.FromUrl("ultralogviewer://open?path=//evil/share/a.log", Now).IsValid);
        Assert.False(OpenRequest.FromUrl("ultralogviewer://open?path=/%5Cevil%5Cshare%5Ca.log", Now).IsValid); // a network path on Windows too
        Assert.False(OpenRequest.FromUrl("ultralogviewer://open?path=%5C/evil/share/a.log", Now).IsValid);
        // never a remote file: that would run ssh
        foreach (var remote in new[] { "ssh://web1/var/log/a.log", "web1:/var/log/a.log", "ops@web1:/var/log/*.log" })
        {
            Assert.False(OpenRequest.FromUrl("ultralogviewer://open?path=" + Uri.EscapeDataString(remote), Now).IsValid, remote);
            Assert.False(OpenRequest.FromJson(JsonSerializer.Serialize(new { path = remote }), Now).IsValid, remote);
        }
        Assert.False(OpenRequest.FromUrl("ultralogviewer://run?cmd=calc", Now).IsValid);
        Assert.False(OpenRequest.FromUrl("ultralogviewer://open?path=/a.log&filter=a%0Db", Now).IsValid);
        Assert.False(OpenRequest.FromUrl("ultralogviewer://open?path=/a.log&format=../../x", Now).IsValid);
        Assert.False(OpenRequest.FromUrl("ultralogviewer://open?path=/a.log&path=%zz", Now).IsValid);
        var many = string.Join("&", Enumerable.Range(0, OpenRequest.MaxFiles + 1).Select(i => $"path=/l/{i}.log"));
        Assert.False(OpenRequest.FromUrl("ultralogviewer://open?" + many, Now).IsValid);

        // and a link on the command line (how Windows starts the app for a clicked link) is a URL request
        var c = OpenRequest.FromCommandLine(CommandLine.Parse(["ultralogviewer://open?path=/var/log/app.log"], Now), Now);
        Assert.Equal(RequestSource.Url, c.Source);
        Assert.Equal(["/var/log/app.log"], c.Paths);
    }

    [Fact]
    public void ParsesJson()
    {
        var r = OpenRequest.FromJson("""{"paths":["/var/log/a.log"],"since":1714557600000,"until":"2024-05-01T11:00:00Z","filter":"status>=500","levels":["error","fatal"],"goto":"line:4","newTab":true,"extra":{"x":1}}""", Now);
        Assert.Equal(["\"extra\": unexpected value"], r.Errors);
        r = OpenRequest.FromJson("""{"path":"/var/log/a.log","last":"30m","search":"timeout","goto":1714557600000,"merge":false,"format":null}""", Now);
        Assert.True(r.IsValid, string.Join(";", r.Errors));
        Assert.Equal(["/var/log/a.log"], r.Paths);
        Assert.Equal(1_800_000, r.Window!.Last);
        Assert.Equal(1714557600000, r.GoTo!.Time);
        Assert.Equal("timeout", r.Search);
        Assert.Equal(RequestSource.Api, r.Source);
        Assert.False(OpenRequest.FromJson("{nope", Now).IsValid);
        Assert.False(OpenRequest.FromJson("[1]", Now).IsValid);
        Assert.False(OpenRequest.FromJson("""{"path":"/a\u0000.log"}""", Now).IsValid);
        Assert.Equal(["A full path is needed: relative.log"], OpenRequest.FromJson("""{"path":"relative.log"}""", Now).Errors);
    }

    [Fact]
    public void LinksAndJsonRoundTrip()
    {
        var r = new OpenRequest
        {
            Paths = ["/var/log/app one.log", @"C:\logs\b&c.log", "/x/bundle.zip::logs/app.log"],
            Merge = true,
            Format = "json",
            Window = new OpenWindow(Date(2024, 5, 1, 10, 0, 0, 0), Date(2024, 5, 1, 11, 0, 0, 0)),
            Filter = "level:error and \"a&b=c\" or #tag ?x",
            Search = "100% done",
            GoTo = new GoToTarget(Time: Date(2024, 5, 1, 10, 30, 0, 123)),
            Levels = [false, false, false, false, true, true, true],
            NewTab = true,
        };
        var url = r.ToUrl();
        Assert.StartsWith("ultralogviewer://open?path=", url);
        Assert.DoesNotContain(" ", url);
        var back = OpenRequest.FromUrl(url, Now);
        Assert.True(back.IsValid, string.Join(";", back.Errors));
        Assert.True(r.SameAs(back), url);
        var json = OpenRequest.FromJson(r.ToJson(), Now);
        Assert.True(r.SameAs(json), r.ToJson());

        var line = new OpenRequest { Paths = ["/a.log"], GoTo = new GoToTarget(Line: 42, File: "2"), Window = new OpenWindow(Last: 5_400_000) };
        Assert.True(line.SameAs(OpenRequest.FromUrl(line.ToUrl(), Now)), line.ToUrl());
        Assert.Contains("last=90m", line.ToUrl());
        // the Electron app builds the same link
        Assert.Equal("ultralogviewer://open?path=%2Fvar%2Flog%2Fa%20b.log&filter=level%3Aerror&goto=2024-03-01T09%3A00%3A02.000Z",
            new OpenRequest { Paths = ["/var/log/a b.log"], Filter = "level:error", GoTo = new GoToTarget(Time: Date(2024, 3, 1, 9, 0, 2, 0)) }.ToUrl());
    }

    [Fact]
    public void TellsLogsFromOtherFiles()
    {
        foreach (var p in new[] { "/var/log/app.log", "/var/log/app.log.3", "/var/log/syslog", "C:\\x\\a.JSONL", "/b.zip::x/y.txt", "/a.log.2024-05-01", "/x.gz" })
            Assert.True(OpenRequest.LooksLikeLog(p), p);
        foreach (var p in new[] { "/home/u/.ssh/id_rsa.pub", "/home/u/.bashrc", "C:\\Windows\\calc.exe", "/x/report.docx", "/etc/shadow.bak" })
            Assert.False(OpenRequest.LooksLikeLog(p), p);
    }

    [Fact]
    public void ExpandsGlobsAndZipMembers()
    {
        var dir = Directory.CreateTempSubdirectory("ulv-glob-").FullName;
        foreach (var n in new[] { "b.log", "a.log", "c.txt" }) File.WriteAllText(Path.Combine(dir, n), "x\n");
        var zip = Path.Combine(dir, "bundle.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create)) z.CreateEntry("app.log");
        var r = new OpenRequest { Paths = ["*.log", Path.Combine(dir, "c.txt"), "bundle.zip::app.log", "-", "missing.log", "*.nope"] };
        var files = r.ExpandPaths(dir);
        Assert.Equal([Path.Combine(dir, "a.log"), Path.Combine(dir, "b.log"), Path.Combine(dir, "c.txt"), zip + "::app.log", "-"], files);
        Assert.Equal(["File not found: missing.log", "No files match *.nope"], r.Errors);
    }

    // =================================================================== HTTP API

    /// <summary>A host over real documents: "open" indexes the files, rows come from the view.</summary>
    private sealed class DocHost : ILocalApiHost
    {
        public readonly List<(int Id, string[] Paths, LogDocument Doc, OpenRequest Req)> Tabs = [];

        public async Task<ApiResult> OpenAsync(OpenRequest request)
        {
            var paths = request.ExpandPaths(Directory.GetCurrentDirectory());
            if (!request.IsValid) return new ApiResult(404, string.Join("; ", request.Errors));
            var doc = new LogDocument("t");
            await doc.AddSourcesAsync(paths, _ => new SourceOptions());
            var id = Tabs.Count + 1;
            Tabs.Add((id, paths.ToArray(), doc, request));
            return await UpdateAsync(id, request);
        }

        public Task<IReadOnlyList<ApiTab>> TabsAsync() => Task.FromResult<IReadOnlyList<ApiTab>>(
            Tabs.Select(t => new ApiTab(t.Id, Path.GetFileName(t.Paths[0]), t.Paths, t.Doc.Info().Total, t.Doc.ViewCount, t.Req.Filter ?? "", true, true)).ToList());

        public async Task<ApiResult> UpdateAsync(int tab, OpenRequest request)
        {
            if (Tabs.FirstOrDefault(t => t.Id == tab) is not { Doc: not null } t) return new ApiResult(404, "no tab");
            if (request.Filter is not null || request.Levels is not null)
                await t.Doc.ApplyFilterAsync(new FilterSpec { Query = request.Filter ?? "", Levels = request.Levels ?? [true, true, true, true, true, true, true] });
            return ApiResult.Ok(tab);
        }

        public Task<IReadOnlyList<RowData>?> RowsAsync(int tab, int from, int count) =>
            Task.FromResult<IReadOnlyList<RowData>?>(Tabs.FirstOrDefault(t => t.Id == tab).Doc?.GetRows(from, Math.Min(count, Math.Max(0, Tabs.First(t => t.Id == tab).Doc.ViewCount - from))));
    }

    private const string Token = "0123456789abcdef0123456789abcdef";

    private static HttpRequestMessage Req(HttpMethod m, int port, string path, string? body = null, string? token = Token)
    {
        var r = new HttpRequestMessage(m, $"http://127.0.0.1:{port}{path}");
        if (token is not null) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) r.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return r;
    }

    [Fact]
    public async Task ServesTheApiOnLoopbackWithTheToken()
    {
        var host = new DocHost();
        using var server = new LocalApiServer(host, Token, () => Now);
        var port = server.Start(0);
        using var http = new HttpClient();

        // auth required, wrong token, token in the query
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Req(HttpMethod.Get, port, "/health", token: null))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Req(HttpMethod.Get, port, "/health", token: "wrong-token-wrong-token"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Req(HttpMethod.Get, port, "/health?token=" + Token, token: null))).StatusCode);
        var health = await http.SendAsync(Req(HttpMethod.Get, port, "/health"));
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.False(health.Headers.Contains("Access-Control-Allow-Origin"));

        // DNS rebinding: a page on evil.example resolving to 127.0.0.1 sends its own Host
        var rebind = Req(HttpMethod.Get, port, "/tabs");
        rebind.Headers.Host = "evil.example:" + port;
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(rebind)).StatusCode);
        var local = Req(HttpMethod.Get, port, "/tabs");
        local.Headers.Host = "localhost:" + port;
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(local)).StatusCode);
        // a web page's request carries its Origin, even with the right token
        var page = Req(HttpMethod.Post, port, "/open", """{"path":"/x.log"}""");
        page.Headers.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(page)).StatusCode);
        var own = Req(HttpMethod.Get, port, "/health");
        own.Headers.Add("Origin", $"http://127.0.0.1:{port}");
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(own)).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await http.SendAsync(Req(HttpMethod.Options, port, "/open"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.SendAsync(Req(HttpMethod.Get, port, "/nope"))).StatusCode);

        // open, then tabs, then rows
        var f = TestFiles.Write("api.log", TestFiles.Log4j);
        var open = await http.SendAsync(Req(HttpMethod.Post, port, "/open", JsonSerializer.Serialize(new { paths = new[] { f }, filter = "level:error" })));
        Assert.Equal(HttpStatusCode.OK, open.StatusCode);
        Assert.Equal(1, JsonDocument.Parse(await open.Content.ReadAsStringAsync()).RootElement.GetProperty("tab").GetInt32());
        var tabs = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/tabs?token={Token}")).RootElement;
        Assert.Equal(1, tabs.GetArrayLength());
        Assert.Equal(f, tabs[0].GetProperty("paths")[0].GetString());
        Assert.Equal(6, tabs[0].GetProperty("total").GetInt64());
        Assert.Equal(1, tabs[0].GetProperty("shown").GetInt64());
        Assert.Equal("level:error", tabs[0].GetProperty("filter").GetString());
        var rows = await http.SendAsync(Req(HttpMethod.Get, port, "/tabs/1/rows?from=0&count=10"));
        Assert.Equal("application/x-ndjson", rows.Content.Headers.ContentType!.MediaType);
        var lines = (await rows.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var row = Assert.Single(lines);
        var jr = JsonDocument.Parse(row).RootElement;
        Assert.Equal("ERROR", jr.GetProperty("level").GetString());
        Assert.Equal(4, jr.GetProperty("line").GetInt32());
        Assert.StartsWith("2024-05-01 10:00:03,004 [worker-2] ERROR com.acme.Db - Query failed", jr.GetProperty("text").GetString());

        // change the filter of the tab
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Req(HttpMethod.Post, port, "/tabs/1/filter", """{"levels":"info,error"}"""))).StatusCode);
        Assert.Equal(3, host.Tabs[0].Doc.ViewCount);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.SendAsync(Req(HttpMethod.Post, port, "/tabs/1/goto", "{}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.SendAsync(Req(HttpMethod.Get, port, "/tabs/9/rows"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.SendAsync(Req(HttpMethod.Post, port, "/open", "{nope"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.SendAsync(Req(HttpMethod.Post, port, "/open", """{"path":"/no/such.log"}"""))).StatusCode);
    }

    [Fact]
    public void FallsBackToAnotherPortAndChecksHosts()
    {
        using var a = new LocalApiServer(new DocHost(), Token);
        var pa = a.Start(0);
        using var b = new LocalApiServer(new DocHost(), Token);
        var pb = b.Start(pa); // taken: the next free one
        Assert.NotEqual(pa, pb);
        Assert.True(pb > 0);
        Assert.Throws<ArgumentException>(() => new LocalApiServer(new DocHost(), "short"));
        Assert.True(LocalApiServer.HostAllowed("127.0.0.1:7700"));
        Assert.True(LocalApiServer.HostAllowed("LocalHost"));
        Assert.False(LocalApiServer.HostAllowed("127.0.0.1.evil.example"));
        Assert.False(LocalApiServer.HostAllowed("localhost.evil.example:7700"));
        Assert.False(LocalApiServer.HostAllowed(null));
        Assert.NotEqual(LocalApiServer.NewToken(), LocalApiServer.NewToken());
        Assert.Equal(64, LocalApiServer.NewToken().Length);
        Assert.Contains("Authorization: Bearer " + Token, LocalApiServer.CurlExample(7700, Token, "/var/log/a.log"));
    }

    [Fact]
    public async Task RefusesRawRequestsThatSmuggleHeaders()
    {
        using var server = new LocalApiServer(new DocHost(), Token);
        var port = server.Start(0);
        async Task<string> Raw(string text)
        {
            using var c = new System.Net.Sockets.TcpClient();
            await c.ConnectAsync(IPAddress.Loopback, port);
            var s = c.GetStream();
            await s.WriteAsync(Encoding.ASCII.GetBytes(text));
            return await new StreamReader(s).ReadToEndAsync();
        }
        Assert.StartsWith("HTTP/1.1 400", await Raw($"GET /health HTTP/1.1\r\nHost: 127.0.0.1\r\nHost: evil.example\r\nAuthorization: Bearer {Token}\r\n\r\n"));
        Assert.StartsWith("HTTP/1.1 403", await Raw($"GET /health HTTP/1.1\r\nAuthorization: Bearer {Token}\r\n\r\n")); // no Host
        Assert.StartsWith("HTTP/1.1 411", await Raw($"POST /open HTTP/1.1\r\nHost: 127.0.0.1\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n"));
        Assert.StartsWith("HTTP/1.1 200", await Raw($"GET /health HTTP/1.1\r\nHost: 127.0.0.1\r\nAuthorization: Bearer {Token}\r\n\r\n"));
    }

    // ======================================================= single-instance handoff

    [Fact]
    public async Task HandsTheCommandLineToTheRunningInstance()
    {
        var name = "ulv-test-" + Guid.NewGuid().ToString("N")[..12];
        Assert.False(SingleInstance.TrySend(name, "nobody", 200)); // none running: the launch goes on
        // a socket file left by a crashed instance does not stop the next one from listening
        if (!OperatingSystem.IsWindows()) File.WriteAllText(Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + name), "");
        var got = new TaskCompletionSource<string>();
        using (SingleInstance.Listen(name, m => got.TrySetResult(m)) ?? throw new InvalidOperationException("not listening"))
        {
            var msg = new SingleInstance.Handoff("/home/u", ["app.log", "--filter", "level:error ü"]).ToJson();
            Assert.True(await Task.Run(() => SingleInstance.TrySend(name, msg)));
            var h = SingleInstance.Handoff.FromJson(await got.Task.WaitAsync(TimeSpan.FromSeconds(5)))!;
            Assert.Equal("/home/u", h.Cwd);
            Assert.Equal(["app.log", "--filter", "level:error ü"], h.Args);
            // several launches in a row all get through
            for (var i = 0; i < 3; i++) Assert.True(await Task.Run(() => SingleInstance.TrySend(name, "{}")));
        }
        Assert.Null(SingleInstance.Handoff.FromJson("garbage"));
        Assert.StartsWith("UltraLogViewer-", SingleInstance.DefaultName);
    }
}
