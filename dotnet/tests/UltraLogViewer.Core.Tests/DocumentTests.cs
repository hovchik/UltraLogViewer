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
}
