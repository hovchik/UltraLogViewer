namespace UltraLogViewer.Core.Tests;

public class DocumentTests
{
    private static FilterSpec Spec(Action<FilterSpec>? f = null)
    {
        var s = new FilterSpec();
        f?.Invoke(s);
        return s;
    }

    private static FilterRule Rule(string text, RuleMode mode, string? column = null) => new() { Text = text, Mode = mode, Column = column };

    private static async Task<LogDocument> Doc(string content = TestFiles.Log4j)
    {
        var d = new LogDocument("test");
        await d.AddSourceAsync(TestFiles.Write("x.log", content), new SourceOptions());
        return d;
    }

    private static int[] Entries(LogDocument d) => d.GetRows(0, 100).Select(r => r.Entry).ToArray();

    [Fact]
    public async Task ReturnsParsedRows()
    {
        using var d = await Doc();
        Assert.Equal(6, d.ViewCount);
        var rows = d.GetRows(2, 2);
        Assert.Equal(["WARN", "ERROR"], rows.Select(r => r.Field("level")));
        Assert.Equal((4, 4, Level.Error, "0:3"), (rows[1].Line, rows[1].LineCount, rows[1].Level, rows[1].Key));
        Assert.Equal(2, d.Info().LevelCounts[(int)Level.Info]);
    }

    [Fact]
    public async Task FiltersByLevelRulesColumnsQueryTimeAndBookmarks()
    {
        using var d = await Doc();
        await d.ApplyFilterAsync(Spec(s => { s.Levels[(int)Level.Info] = false; s.Levels[(int)Level.Debug] = false; }));
        Assert.Equal([2, 3, 5], Entries(d));

        await d.ApplyFilterAsync(Spec(s => s.Rules.Add(Rule("db", RuleMode.Include))));
        Assert.Equal([1, 3], Entries(d));

        await d.ApplyFilterAsync(Spec(s => { s.Rules.Add(Rule("db", RuleMode.Include)); s.Rules.Add(Rule("timeout", RuleMode.Exclude)); }));
        Assert.Equal([1], Entries(d));

        await d.ApplyFilterAsync(Spec(s => s.Rules.Add(Rule("main", RuleMode.Include, "thread"))));
        Assert.Equal([0, 1, 4, 5], Entries(d));

        await d.ApplyFilterAsync(Spec(s => s.Query = "logger:Db OR level:fatal"));
        Assert.Equal([1, 3, 5], Entries(d));

        await d.ApplyFilterAsync(Spec(s => { s.TimeFrom = TestFiles.Local(2024, 5, 1, 10, 0, 2); s.TimeTo = TestFiles.Local(2024, 5, 1, 10, 0, 5) - 1; }));
        Assert.Equal([2, 3, 4], Entries(d));

        await d.ApplyFilterAsync(Spec(s => { s.BookmarksOnly = true; s.Bookmarks = ["0:5", "0:0"]; }));
        Assert.Equal([0, 5], Entries(d));

        await d.ApplyFilterAsync(null);
        Assert.Equal(6, d.ViewCount);
    }

    [Fact]
    public async Task AddsContextRows()
    {
        using var d = await Doc();
        await d.ApplyFilterAsync(Spec(s => { s.Rules.Add(Rule("Cache", RuleMode.Include)); s.Context = 1; }));
        Assert.Equal([(1, true), (2, false), (3, true)], d.GetRows(0, 10).Select(r => (r.Entry, r.IsContext)));
    }

    [Fact]
    public async Task SearchesWithinViewAndNavigates()
    {
        using var d = await Doc();
        Assert.Equal(new[] { 1, 3 }, (await d.SearchAsync(new TextPattern("acme.db"))).Rows);
        Assert.Equal(new[] { 3 }, (await d.SearchAsync(new TextPattern(@"Db\.java:\d+", Regex: true, CaseSensitive: true))).Rows);
        await d.ApplyFilterAsync(Spec(s => s.Levels[(int)Level.Debug] = false));
        Assert.Equal(new[] { 2 }, (await d.SearchAsync(new TextPattern("acme.db"))).Rows);
        await d.ApplyFilterAsync(null);
        Assert.Equal(3, d.FindLevel(0, 1, Level.Error));
        Assert.Equal(5, d.FindLevel(3, 1, Level.Error));
        Assert.Equal(2, d.FindLevel(5, -1, Level.Warn));
        Assert.Equal(3, d.RowForLine(0, 6));
        Assert.Equal(4, d.RowForTime(TestFiles.Local(2024, 5, 1, 10, 0, 3, 500)));
    }

    [Fact]
    public async Task MergesFilesByTimestampAndFollowsAppends()
    {
        var a = TestFiles.Write("a.log", "2024-01-01 00:00:01 INFO a1\n2024-01-01 00:00:03 INFO a3\n");
        var b = TestFiles.Write("b.log", "2024-01-01 00:00:02 ERROR b2\n2024-01-01 00:00:04 INFO b4\n");
        using var d = new LogDocument("merged");
        await d.AddSourceAsync(a, new SourceOptions());
        await d.AddSourceAsync(b, new SourceOptions());
        Assert.Equal(["a1", "b2", "a3", "b4"], d.GetRows(0, 10).Select(r => r.Field("message")));
        Assert.Equal("__source", d.Info().Columns[0].Key);
        await d.ApplyFilterAsync(Spec(s => s.Rules.Add(Rule("b4", RuleMode.Include))));
        Assert.Equal(["1:1"], d.GetRows(0, 10).Select(r => r.Key));
        Assert.Equal(0, d.RowForKey("1:1"));
        File.AppendAllText(a, "2024-01-01 00:00:05 INFO a5 b4\n");
        Assert.True(await d.RefreshAsync());
        Assert.Equal(["1:1", "0:2"], d.GetRows(0, 10).Select(r => r.Key));
    }

    [Fact]
    public async Task BuildsHistogramOverviewAndExports()
    {
        using var d = await Doc();
        var h = d.Histogram(10)!;
        Assert.Equal(1000, h.BucketMs);
        Assert.Equal(6, h.Counts.Sum(c => c.Other + c.Warn + c.Error));
        Assert.Equal(1, h.Counts[3].Error);
        Assert.Equal([0, 3, 2], d.Overview(3).Select(b => (int)b));
        var csv = TestFiles.Write("out.csv", "");
        await d.ExportAsync(csv, "csv");
        var lines = File.ReadAllLines(csv);
        Assert.Equal("\"line\",\"Time\",\"Level\",\"Thread\",\"Logger\",\"Message\"", lines[0]);
        Assert.Contains("\"Starting app\"", lines[1]);
        var txt = TestFiles.Write("out.txt", "");
        await d.ExportAsync(txt, "text");
        Assert.Equal(TestFiles.Log4j, File.ReadAllText(txt));
    }

    [Fact]
    public async Task KeepsFiltersCurrentWhileTailing()
    {
        var p = TestFiles.Write("t.log", "2024-01-01 00:00:01 INFO ok\n");
        using var d = new LogDocument("t");
        await d.AddSourceAsync(p, new SourceOptions());
        await d.ApplyFilterAsync(Spec(s => s.Rules.Add(Rule("boom", RuleMode.Include))));
        Assert.Equal(0, d.ViewCount);
        File.AppendAllText(p, "2024-01-01 00:00:02 ERROR boom\n2024-01-01 00:00:03 INFO fine\n");
        await d.RefreshAsync();
        Assert.Equal([1], Entries(d));
    }

    [Fact]
    public async Task ExactRulesMatchTheWholeValue()
    {
        using var d = await Doc();
        await d.ApplyFilterAsync(Spec(s => s.Rules.Add(new FilterRule { Text = "com.acme.App", Column = "logger", Exact = true })));
        Assert.Equal([0, 4, 5], Entries(d));
        await d.ApplyFilterAsync(Spec(s => s.Rules.Add(new FilterRule { Text = "com.acme", Column = "logger", Exact = true })));
        Assert.Equal(0, d.ViewCount);
        await d.ApplyFilterAsync(Spec(s => s.Rules.Add(new FilterRule { Text = "worker-1", Column = "thread", Exact = true, Mode = RuleMode.Exclude })));
        Assert.Equal([0, 1, 3, 4, 5], Entries(d));
    }

    [Fact]
    public async Task ShowsAllEntriesInContextWithoutLosingTheFilter()
    {
        using var d = await Doc();
        await d.ApplyFilterAsync(Spec(s => s.Rules.Add(Rule("Db", RuleMode.Include))));
        Assert.Equal([1, 3], Entries(d));
        d.IgnoreFilter = true;
        Assert.Equal(6, d.ViewCount);
        Assert.Equal([true, false, true, false, true, true], d.GetRows(0, 10).Select(r => r.IsContext));
        Assert.Equal(3, d.RowForKey("0:3"));
        Assert.Equal(new[] { 2 }, (await d.SearchAsync(new TextPattern("Cache"))).Rows);
        d.IgnoreFilter = false;
        Assert.Equal([1, 3], Entries(d));
        Assert.Null(d.RowForKey("0:2"));
        Assert.Equal(1, d.RowForKey("0:2", nearest: true));
    }

    [Fact]
    public async Task CountsTopFieldValues()
    {
        using var d = await Doc();
        var stats = await d.FieldStatsAsync();
        Assert.Equal((6, 6, false), (stats.Scanned, stats.ViewCount, stats.Sampled));
        Assert.Null(stats.Get("message"));
        var logger = stats.Get("logger")!;
        Assert.Equal(("com.acme.App", 3), logger.Top[0]);
        Assert.Equal(3, logger.Distinct);
        Assert.Equal(6, logger.Present);
        Assert.Equal(("main", 4), stats.Get("thread")!.Top[0]);

        await d.ApplyFilterAsync(Spec(s => s.Rules.Add(Rule("Db", RuleMode.Include))));
        Assert.Equal([("com.acme.Db", 2)], (await d.FieldStatsAsync()).Get("logger")!.Top);
        var sampled = await d.FieldStatsAsync(maxRows: 1);
        Assert.True(sampled.Sampled || sampled.ViewCount <= 1000);
    }

    [Fact]
    public async Task SamplesLargeViewsInBlocks()
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 5000; i++) sb.Append($"2024-01-01 00:00:00 [t{i % 3}] INFO x - m{i}\n");
        using var d = await Doc(sb.ToString());
        var stats = await d.FieldStatsAsync(maxRows: 2000);
        Assert.True(stats.Sampled);
        Assert.Equal(2000, stats.Scanned);
        Assert.Equal(3, stats.Get("thread")!.Distinct);
    }

    [Fact]
    public async Task JsonEntriesExposeEveryPropertyAsAField()
    {
        using var d = await Doc("{\"@t\":\"2024-01-01T00:00:00Z\",\"@l\":\"Warning\",\"@m\":\"slow\",\"Elapsed\":812,\"http\":{\"path\":\"/api\",\"status\":503},\"tags\":[\"a\"]}\n");
        var row = d.GetRows(0, 1)[0];
        Assert.Equal("WARN", row.Field("level"));
        Assert.Equal("slow", row.Field("message"));
        Assert.Equal("812", row.Field("Elapsed"));
        Assert.Equal("/api", row.Field("http.path"));
        Assert.Equal("503", row.Field("http.status"));
        Assert.Equal("[\"a\"]", row.Field("tags"));
        Assert.False(row.Fields.ContainsKey("@m"));
        await d.ApplyFilterAsync(Spec(s => s.Query = "http.status:503"));
        Assert.Equal(1, d.ViewCount);
    }

    [Fact]
    public async Task KeepsLateCommonValuesSkipsUnlistableOnesAndFlattensCanonicalObjects()
    {
        var sb = new System.Text.StringBuilder();
        var longText = new string('x', 300);
        for (var i = 0; i < 6000; i++) sb.Append($"{{\"@m\":\"m\",\"id\":\"u{i}\",\"body\":\"{longText}\"}}\n");
        for (var i = 0; i < 300; i++) sb.Append("{\"@m\":\"m\",\"id\":\"late\",\"error\":{\"code\":\"EHOST\",\"stack\":\"s\"}}\n");
        using var d = await Doc(sb.ToString());
        var stats = await d.FieldStatsAsync(maxRows: 1_000_000);
        var id = stats.Get("id")!;
        Assert.Equal(("late", 300), id.Top[0]);
        Assert.True(id.DistinctCapped);
        var body = stats.Get("body")!;
        Assert.Equal(6000, body.Present);
        Assert.Empty(body.Top);
        Assert.Equal("EHOST", d.GetRows(6000, 1)[0].Field("error.code"));
    }

    private const string Repeats =
        "2024-05-01 10:00:00,000 [main] INFO  com.acme.Api - Payment 1 done\n" +
        "2024-05-01 10:00:01,000 [main] INFO  com.acme.Api - Payment 1 done\n" +
        "2024-05-01 10:00:02,000 [main] INFO  com.acme.Api - Payment 2 done\n" +
        "2024-05-01 10:00:03,000 [main] WARN  com.acme.Api - Payment 3 done\n" +
        "2024-05-01 10:00:04,000 [main] INFO  com.acme.Api - Payment 4 done\n" +
        "2024-05-01 10:00:05,000 [main] INFO  com.acme.Api - Payment 5 done\n";

    [Fact]
    public async Task GroupsRepeatsExactlyOrByPatternAndFollowsTail()
    {
        var path = TestFiles.Write("rep.log", Repeats);
        using var d = new LogDocument("t");
        await d.AddSourceAsync(path, new SourceOptions());
        await d.ApplyFilterAsync(Spec(s => s.GroupRepeats = "exact"));
        Assert.Equal([(0, 2), (2, 1), (3, 1), (4, 1), (5, 1)], d.GetRows(0, 10).Select(r => (r.Entry, r.Repeat)));
        await d.ApplyFilterAsync(Spec(s => s.GroupRepeats = "pattern"));
        Assert.Equal([(0, 3), (3, 1), (4, 2)], d.GetRows(0, 10).Select(r => (r.Entry, r.Repeat)));

        // appended entries extend the last run, or start a new one
        File.AppendAllText(path, "2024-05-01 10:00:06,000 [main] INFO  com.acme.Api - Payment 6 done\n2024-05-01 10:00:07,000 [main] INFO  com.acme.Api - Shutdown\n");
        Assert.True(await d.RefreshAsync());
        Assert.Equal([(0, 3), (3, 1), (4, 3), (7, 1)], d.GetRows(0, 10).Select(r => (r.Entry, r.Repeat)));

        // show in context ignores grouping; context rows never merge
        d.IgnoreFilter = true;
        Assert.Equal(8, d.ViewCount);
        Assert.All(d.GetRows(0, 10), r => Assert.Equal(1, r.Repeat));
        d.IgnoreFilter = false;
        await d.ApplyFilterAsync(Spec(s => { s.GroupRepeats = "pattern"; s.Levels[(int)Level.Warn] = false; s.Query = "Payment"; s.Context = 1; }));
        var rows = d.GetRows(0, 10);
        Assert.Equal([(0, 3, false), (3, 1, true), (4, 3, false), (7, 1, true)], rows.Select(r => (r.Entry, r.Repeat, r.IsContext)));
    }

    [Fact]
    public async Task PatternRulesAndClearAndPatternScan()
    {
        using var d = await Doc(Repeats);
        await d.ApplyFilterAsync(Spec(s => s.Rules.Add(new FilterRule { Text = "Payment <num> done", PatternMatch = true, Mode = RuleMode.Exclude })));
        Assert.Equal(0, d.ViewCount);
        await d.ApplyFilterAsync(Spec(s => s.Rules.Add(new FilterRule { Text = "Payment <num> done", PatternMatch = true })));
        Assert.Equal(6, d.ViewCount);
        await d.ApplyFilterAsync(Spec(s => s.ClearedBefore = [4]));
        Assert.Equal([4, 5], Entries(d));

        await d.ApplyFilterAsync(null);
        var stats = await d.PatternsAsync();
        Assert.Equal((6, 6, false, 1), (stats.Scanned, stats.ViewCount, stats.Sampled, stats.Total));
        var p = stats.Patterns.Single();
        Assert.Equal(("Payment <num> done", 6, "Payment 1 done"), (p.Template, p.Count, p.Sample));
        Assert.Equal(5, p.Levels[(int)Level.Info]);
        Assert.Equal(1, p.Levels[(int)Level.Warn]);
        Assert.Equal(6, p.Spark.Sum());

        using var big = await Doc(TestFiles.Log4j);
        var s2 = await big.PatternsAsync();
        Assert.Equal(s2.Patterns.OrderByDescending(x => x.Count).ThenBy(x => x.Template, StringComparer.Ordinal).Select(x => x.Template), s2.Patterns.Select(x => x.Template));
    }
}
