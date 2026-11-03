namespace UltraLogViewer.Core.Tests;

public class SqlTests
{
    private static async Task<LogDocument> Doc(string content, string name = "x.log")
    {
        var d = new LogDocument("test");
        await d.AddSourceAsync(TestFiles.Write(name, content), new SourceOptions());
        return d;
    }

    /// <summary>JSON lines with nested fields, mixed with plain lines that are not JSON.</summary>
    private const string MessyJson =
        "{\"@timestamp\":\"2024-05-01T10:00:00.000Z\",\"level\":\"INFO\",\"logger\":\"api\",\"message\":\"GET /a completed\",\"http\":{\"status\":200,\"path\":\"/a\"},\"duration_ms\":12,\"traceId\":\"007\"}\n" +
        "plain startup line without json\n" +
        "{\"@timestamp\":\"2024-05-01T10:00:01.500Z\",\"level\":\"ERROR\",\"logger\":\"db\",\"message\":\"query failed\",\"http\":{\"status\":500,\"path\":\"/b\"},\"duration_ms\":950.5,\"order\":\"x\"}\n" +
        "{\"@timestamp\":\"2024-05-01T10:00:03.000Z\",\"level\":\"WARN\",\"logger\":\"api\",\"message\":\"slow\",\"http\":{\"status\":200,\"path\":\"/c\"},\"duration_ms\":400}\n" +
        "{\"@timestamp\":\"2024-05-01T10:00:07.000Z\",\"level\":\"ERROR\",\"logger\":\"api\",\"message\":\"boom\",\"http\":{\"status\":503,\"path\":\"/d\"}}\n";

    [Fact]
    public void SanitisesColumnNames()
    {
        Assert.Equal("http_status", SqlSchema.Sanitize("http.status"));
        Assert.Equal("t", SqlSchema.Sanitize("@t"));
        Assert.Equal("f_1st", SqlSchema.Sanitize("1st"));
        Assert.Equal("order_", SqlSchema.Sanitize("order"));
        Assert.Equal("a_b", SqlSchema.Sanitize("a -- b"));
        Assert.Equal("field", SqlSchema.Sanitize("¤"));
        Assert.Equal("traceId", SqlSchema.Sanitize("traceId"));
    }

    [Fact]
    public void BuildsTheSchema()
    {
        var cols = SqlSchema.Build(["time", "level", "logger", "message", "http.status", "http_status", "source", "Level", "__source", "x"]);
        Assert.Equal(["row", "line", "source", "time", "time_iso", "level", "logger", "thread", "message", "text"], cols.Take(10).Select(c => c.Name));
        Assert.True(cols.Take(10).All(c => c.BuiltIn));
        Assert.Equal([("http_status", "http.status"), ("http_status_2", "http_status"), ("source_2", "source"), ("x", "x")], cols.Skip(10).Select(c => (c.Name, c.Field!)));
    }

    [Theory]
    [InlineData("SELECT 1", null)]
    [InlineData("  with x as (select 1) select * from x;  ", null)]
    [InlineData("select replace(message, 'a', 'b') from logs -- delete", null)]
    [InlineData("select 'attach' as \"drop\" from logs", null)]
    [InlineData("ATTACH DATABASE '/tmp/x.db' AS x", "ATTACH is not allowed: queries cannot open other files.")]
    [InlineData("select * from logs; attach 'x' as y", "Run one statement at a time.")]
    [InlineData("select load_extension('/tmp/evil')", "load_extension is not allowed.")]
    [InlineData("with x as (select 1) delete from logs_data", "DELETE is not allowed: the table is read-only.")]
    [InlineData("PRAGMA temp_store = FILE", "PRAGMA is not allowed: the table is read-only.")]
    [InlineData("VACUUM INTO '/tmp/copy.db'", "VACUUM is not allowed: the table is read-only.")]
    [InlineData("explain select 1", "Only SELECT queries can run here: the table is read-only.")]
    [InlineData(" -- nothing", "Enter a query.")]
    public void GuardsQueries(string sql, string? error) => Assert.Equal(error, SqlSchema.Guard(sql));

    [Fact]
    public void FindsTheColumnsAQueryNames()
    {
        var m = SqlSchema.Mentioned("select count(*), \"Logger\" from logs where message like '%text%'", out var star);
        Assert.False(star);
        Assert.Contains("logger", m);
        Assert.Contains("message", m);
        Assert.DoesNotContain("text", m);
        SqlSchema.Mentioned("select l.* from logs l", out star);
        Assert.True(star);
        SqlSchema.Mentioned("select line * 2 from logs", out star);
        Assert.False(star);
    }

    [Fact]
    public void StoresCanonicalNumbersAsNumbers()
    {
        Assert.Equal(200L, SqlSchema.Value("200"));
        Assert.Equal(950.5, SqlSchema.Value("950.5"));
        Assert.Equal("007", SqlSchema.Value("007"));
        Assert.Equal("12ms", SqlSchema.Value("12ms"));
        Assert.Null(SqlSchema.Value(""));
    }

    [Fact]
    public async Task LoadsAMessyJsonFile()
    {
        using var d = await Doc(MessyJson);
        using var sql = new LogSql(d);
        var cols = await sql.SchemaAsync();
        Assert.Equal(["http_status", "http_path", "duration_ms", "traceId", "order_"], cols.Where(c => !c.BuiltIn).Select(c => c.Name));

        var r = await sql.QueryAsync("SELECT row, line, source, time, time_iso, level, logger, message, http_status, duration_ms, traceId FROM logs ORDER BY line");
        Assert.Null(r.Error);
        Assert.Equal(5, r.Rows.Count);
        Assert.Equal([0L, 1L, "x.log", 1714557600000L, "2024-05-01T10:00:00.000Z", "INFO", "api", "GET /a completed", 200L, 12L, "007"], r.Rows[0].Select(v => v is string s && s.EndsWith("x.log") ? "x.log" : v));
        // The plain line: its message is the line, no level or fields (the index gives it the time of the entry before).
        Assert.Equal([1L, 2L, 1714557600000L, null, null, "plain startup line without json"], new[] { 0, 1, 3, 5, 6, 7 }.Select(i => r.Rows[1][i]));
        Assert.Null(r.Rows[1][8]);
        Assert.Equal(950.5, r.Rows[2][9]);
        Assert.Equal(["duration_ms", "http_status", "logger", "message", "traceId"], sql.LoadedColumns.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(5, r.Loaded);
        Assert.False(r.Capped);

        var n = await sql.QueryAsync("SELECT count(*) FROM logs WHERE http_status >= 500");
        Assert.Equal(2L, n.Rows[0][0]);
    }

    [Fact]
    public async Task LoadsMultiLineEntriesAndOnlyTheNamedColumns()
    {
        using var d = await Doc(TestFiles.Log4j);
        using var sql = new LogSql(d);
        var r = await sql.QueryAsync("SELECT line, level FROM logs");
        Assert.Equal([1L, 2L, 3L, 4L, 8L, 9L], r.Rows.Select(x => x[0]));
        Assert.Empty(sql.LoadedColumns);
        r = await sql.QueryAsync("SELECT logger, thread, message, text FROM logs WHERE level = 'ERROR'");
        Assert.Equal(["logger", "message", "text", "thread"], sql.LoadedColumns.Order(StringComparer.Ordinal));
        Assert.Equal("com.acme.Db", r.Rows[0][0]);
        Assert.Equal("worker-2", r.Rows[0][1]);
        Assert.StartsWith("Query failed\njava.sql.SQLException: timeout\n\tat com.acme.Db.query", (string)r.Rows[0][2]!);
        Assert.StartsWith("2024-05-01 10:00:03,004 [worker-2] ERROR", (string)r.Rows[0][3]!);
        Assert.EndsWith("(App.java:10)", (string)r.Rows[0][3]!);
        var all = await sql.QueryAsync("SELECT * FROM logs");
        Assert.Equal(["row", "line", "source", "time", "time_iso", "level", "logger", "thread", "message", "text"], all.Columns);
    }

    [Fact]
    public async Task RunsWindowFunctionsAndRegexHelpers()
    {
        using var d = await Doc(TestFiles.Log4j);
        using var sql = new LogSql(d);
        var r = await sql.QueryAsync("SELECT line, time - LAG(time) OVER (ORDER BY time, line) AS gap, count(*) OVER (PARTITION BY level) AS same FROM logs ORDER BY line");
        Assert.Null(r.Error);
        Assert.Equal([null, 1001L, 1001L, 1001L, 1001L, 1001L], r.Rows.Select(x => x[1]));
        Assert.Equal([2L, 1L, 1L, 1L, 2L, 1L], r.Rows.Select(x => x[2]));
        r = await sql.QueryAsync("SELECT regexp_extract(message, 'Db\\.java:(\\d+)') AS at FROM logs WHERE message REGEXP 'time(out|d)'");
        Assert.Equal("42", Assert.Single(r.Rows)[0]);
        foreach (var ex in SqlSchema.Examples(await sql.SchemaAsync()))
            Assert.True((await sql.QueryAsync(ex.Sql)).Error is null, ex.Title);
    }

    [Fact]
    public async Task RowAndViewFollowTheFilter()
    {
        using var d = await Doc(TestFiles.Log4j);
        using var sql = new LogSql(d);
        var spec = new FilterSpec();
        spec.Levels[(int)Level.Info] = false;
        await d.ApplyFilterAsync(spec);
        var r = await sql.QueryAsync("SELECT line, row FROM logs ORDER BY line");
        Assert.Equal([null, 0L, 1L, 2L, null, 3L], r.Rows.Select(x => x[1]));
        var v = await sql.QueryAsync("SELECT count(*) FROM view");
        Assert.Equal(4L, v.Rows[0][0]);
        await d.ApplyFilterAsync(null);
        v = await sql.QueryAsync("SELECT count(*) FROM view");
        Assert.Equal(6L, v.Rows[0][0]);
    }

    [Fact]
    public async Task ShowsSqlErrors()
    {
        using var d = await Doc(TestFiles.Log4j);
        using var sql = new LogSql(d);
        var r = await sql.QueryAsync("SELECT nope FROM logs");
        Assert.Equal("no such column: nope", r.Error);
        r = await sql.QueryAsync("DELETE FROM logs_data");
        Assert.Equal("DELETE is not allowed: the table is read-only.", r.Error);
    }

    [Fact]
    public async Task TheEngineBlocksAttachWritesAndExtensionsOnItsOwn()
    {
        using var d = await Doc(TestFiles.Log4j);
        using var sql = new LogSql(d) { SkipGuard = true };
        var file = Path.Combine(Path.GetTempPath(), $"ulv-attach-{Guid.NewGuid():N}.db");
        foreach (var q in new[] { $"ATTACH DATABASE '{file}' AS x", "SELECT load_extension('/tmp/nothing')", "DELETE FROM logs_data", "PRAGMA temp_store = FILE", $"VACUUM INTO '{file}'", "CREATE TABLE t(a)" })
        {
            var r = await sql.QueryAsync(q);
            Assert.False(r.Error is null, q);
            Assert.True(r.Error!.Contains("not authorized") || r.Error.Contains("authorization denied"), q + " => " + r.Error);
        }
        Assert.False(File.Exists(file));
        Assert.Equal(6L, (await sql.QueryAsync("SELECT count(*) FROM logs")).Rows[0][0]);
    }

    [Fact]
    public async Task CapsBigFilesAndLoadsEverythingOnRequest()
    {
        var lines = string.Concat(Enumerable.Range(0, 3000).Select(i => $"2024-05-01 10:00:00,000 [main] INFO  com.acme.App - entry {i}\n"));
        using var d = await Doc(lines);
        using var sql = new LogSql(d) { Cap = 1000, Slice = 300, Batch = 64 };
        var r = await sql.QueryAsync("SELECT count(*), max(line) FROM logs WHERE message LIKE 'entry%'");
        Assert.Equal([1000L, 1000L], r.Rows[0]);
        Assert.True(r.Capped);
        Assert.Equal((1000, 3000), (r.Loaded, r.Total));
        sql.Cap = 0;
        r = await sql.QueryAsync("SELECT count(*) FROM logs");
        Assert.Equal(3000L, r.Rows[0][0]);
        Assert.False(r.Capped);
    }

    [Fact]
    public async Task CancellingALoadKeepsThePreviousTable()
    {
        var lines = string.Concat(Enumerable.Range(0, 5000).Select(i => $"2024-05-01 10:00:00,000 [main] INFO  com.acme.App - entry {i}\n"));
        using var d = await Doc(lines);
        using var sql = new LogSql(d) { Slice = 500, Batch = 50 };
        Assert.Equal(5000L, (await sql.QueryAsync("SELECT count(*) FROM logs")).Rows[0][0]);
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(f => { if (f > 0.2) cts.Cancel(); });
        var r = await sql.QueryAsync("SELECT count(message) FROM logs", progress, cts.Token);
        Assert.True(r.Cancelled);
        Assert.Empty(sql.LoadedColumns);
        Assert.Equal(5000L, (await sql.QueryAsync("SELECT count(*) FROM logs")).Rows[0][0]);
        Assert.Equal(5000L, (await sql.QueryAsync("SELECT count(message) FROM logs")).Rows[0][0]);
    }

    [Fact]
    public async Task LoadsAppendedEntriesAfterACancelledAppend()
    {
        static string Lines(int n) => string.Concat(Enumerable.Range(0, n).Select(i => $"2024-05-01 10:00:00,000 [main] INFO  com.acme.App - entry {i}\n"));
        var path = TestFiles.Write("grow-cancel.log", Lines(2000));
        using var d = new LogDocument("grow-cancel");
        await d.AddSourceAsync(path, new SourceOptions());
        using var sql = new LogSql(d) { Slice = 500, Batch = 50 };
        Assert.Equal(2000L, (await sql.QueryAsync("SELECT count(message) FROM logs")).Rows[0][0]);
        File.AppendAllText(path, Lines(3000));
        Assert.True(await d.RefreshAsync());
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(f => { if (f > 0.2) cts.Cancel(); });
        var r = await sql.QueryAsync("SELECT count(message) FROM logs", progress, cts.Token);
        Assert.True(r.Cancelled);
        Assert.Equal(5000L, (await sql.QueryAsync("SELECT count(message) FROM logs")).Rows[0][0]);
    }

    [Fact]
    public async Task FollowsAGrowingFileAndANewFormat()
    {
        var path = TestFiles.Write("grow.log", TestFiles.Log4j);
        using var d = new LogDocument("grow");
        await d.AddSourceAsync(path, new SourceOptions());
        using var sql = new LogSql(d);
        Assert.Equal(6L, (await sql.QueryAsync("SELECT count(*) FROM logs")).Rows[0][0]);
        File.AppendAllText(path, "\tat com.acme.More(More.java:1)\n2024-05-01 10:00:06,007 [main] INFO  com.acme.App - Again\n");
        Assert.True(await d.RefreshAsync());
        var r = await sql.QueryAsync("SELECT line, message FROM logs ORDER BY line DESC LIMIT 2");
        Assert.Equal([11L, "Again"], r.Rows[0]);
        Assert.Equal(9L, r.Rows[1][0]);
        Assert.Contains("More.java", (string)r.Rows[1][1]!);
        Assert.Equal(7L, (await sql.QueryAsync("SELECT count(*) FROM logs")).Rows[0][0]);
        await d.ReformatAsync("plain", null);
        r = await sql.QueryAsync("SELECT count(*), count(logger) FROM logs");
        Assert.Equal([11L, 0L], r.Rows[0]);
    }

    [Fact]
    public async Task MergedFilesKeepTheirSourceAndBaseOrder()
    {
        var a = TestFiles.Write("a.log", "2024-05-01 10:00:00,000 INFO a1\n2024-05-01 10:00:02,000 INFO a2\n");
        var b = TestFiles.Write("b.log", "2024-05-01 10:00:01,000 WARN b1\n");
        using var d = new LogDocument("m");
        await d.AddSourcesAsync([a, b], _ => new SourceOptions());
        using var sql = new LogSql(d);
        var r = await sql.QueryAsync("SELECT row, line, source, level FROM logs");
        Assert.Equal([0L, 1L, 2L], r.Rows.Select(x => x[0]));
        Assert.Equal([Path.GetFileName(a), Path.GetFileName(b), Path.GetFileName(a)], r.Rows.Select(x => (string)x[2]!));
        Assert.Equal([1L, 1L, 2L], r.Rows.Select(x => x[1]));
    }
}
