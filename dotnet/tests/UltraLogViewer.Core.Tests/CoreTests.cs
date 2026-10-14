using System.IO.Compression;
using System.Text;

namespace UltraLogViewer.Core.Tests;

public class TimestampTests
{
    [Fact]
    public void ParsesIsoWithZoneAndFractions()
    {
        Assert.True(Timestamps.TryMatch("2024-05-01T10:00:00.123Z rest", 2024, out var ms, out _));
        Assert.Equal(new DateTimeOffset(2024, 5, 1, 10, 0, 0, 123, TimeSpan.Zero).ToUnixTimeMilliseconds(), ms);
        Assert.True(Timestamps.TryMatch("2024-05-01 10:00:00,5+02:00 x", 2024, out ms, out _));
        Assert.Equal(new DateTimeOffset(2024, 5, 1, 8, 0, 0, 500, TimeSpan.Zero).ToUnixTimeMilliseconds(), ms);
        Assert.True(Timestamps.TryMatch("[2024-05-01 10:00:00] local.ERROR", 2024, out _, out var len));
        Assert.Equal(21, len);
    }

    [Fact]
    public void ParsesApacheSyslogDayFirstAndEpoch()
    {
        Assert.True(Timestamps.TryMatch("10/Oct/2023:13:55:36 -0700", 0, out var ms, out _));
        Assert.Equal(new DateTimeOffset(2023, 10, 10, 20, 55, 36, TimeSpan.Zero).ToUnixTimeMilliseconds(), ms);
        Assert.True(Timestamps.TryMatch("Oct  3 04:05:06 host", 2022, out ms, out _));
        Assert.Equal(TestFiles.Local(2022, 10, 3, 4, 5, 6), ms);
        Assert.True(Timestamps.TryMatch("31.12.2024 23:59:58 x", 0, out ms, out _));
        Assert.Equal(TestFiles.Local(2024, 12, 31, 23, 59, 58), ms);
        Assert.Equal(1700000000123, Timestamps.Parse("1700000000123"));
        Assert.Equal(1700000000000, Timestamps.FromNumber(1700000000));
        Assert.False(Timestamps.TryMatch("hello", 0, out _, out _));
        Assert.False(Timestamps.TryMatch("2024-13-45 99:00:00", 0, out _, out _));
    }
}

public class LevelTests
{
    [Fact]
    public void MapsWordsAndNumbers()
    {
        Assert.Equal(Level.Warn, Levels.Parse("warning"));
        Assert.Equal(Level.Error, Levels.Parse("50"));
        Assert.Equal(Level.Error, Levels.Parse("E"));
        Assert.Equal(Level.None, Levels.Parse("constructor"));
        Assert.Equal(Level.Error, Levels.Detect("12:00 [error] something"));
        Assert.Equal(Level.None, Levels.Detect("nothing here"));
    }
}

public class FormatTests
{
    [Fact]
    public void DetectsLog4jStyleTextAndParsesColumns()
    {
        var f = FormatDetector.Detect(TestFiles.Log4j.Split('\n'));
        Assert.Equal("timestamped", f.Id);
        var p = f.Parse("2024-05-01 10:00:03,004 [worker-2] ERROR com.acme.Db - Query failed\njava.sql.SQLException: timeout");
        Assert.Equal("2024-05-01 10:00:03,004", p["time"]);
        Assert.Equal("worker-2", p["thread"]);
        Assert.Equal("ERROR", p["level"]);
        Assert.Equal("com.acme.Db", p["logger"]);
        Assert.Equal("Query failed\njava.sql.SQLException: timeout", p["message"]);
        Assert.False(f.IsEntryStart("\tat x"));
    }

    [Fact]
    public void ParsesNLogPipesAndSpringBoot()
    {
        var f = new TimestampedFormat(2024);
        var a = f.Parse("2024-05-01 10:00:00.1234|WARN|Acme.Service|Low disk");
        Assert.Equal(("WARN", "Acme.Service", "Low disk"), (a["level"], a["logger"], a["message"]));
        var b = f.Parse("2024-05-01T10:00:00.123Z ERROR 1234 --- [worker-1] c.a.Service : boom");
        Assert.Equal(("ERROR", "worker-1", "c.a.Service", "boom"), (b["level"], b["thread"], b["logger"], b["message"]));
    }

    [Fact]
    public void DetectsJsonLines()
    {
        var f = FormatDetector.Detect(["{\"@timestamp\":\"2024-05-01T10:00:00Z\",\"level\":\"error\",\"message\":\"x\",\"logger\":\"a\"}"]);
        Assert.Equal("json", f.Id);
        Assert.Equal(new QuickInfo(Level.Error, 1700000000000), f.Quick("{\"time\":1700000000000,\"level\":50,\"msg\":\"m\"}"));
        var p = f.Parse("{\"level\":\"info\",\"msg\":\"hello\",\"name\":\"svc\"}");
        Assert.Equal(("INFO", "hello", "svc"), (p["level"], p["message"], p["logger"]));
    }

    [Fact]
    public void DetectsAccessSyslogRfc5424LogcatAndW3c()
    {
        Assert.Equal("access", FormatDetector.Detect(["127.0.0.1 - frank [10/Oct/2023:13:55:36 -0700] \"GET /a.gif HTTP/1.0\" 500 2326 \"-\" \"curl\""]).Id);
        Assert.Equal("syslog", FormatDetector.Detect(["Oct  3 04:05:06 myhost sshd[123]: Accepted password"]).Id);
        var f5 = FormatDetector.Detect(["<165>1 2003-10-11T22:14:15.003Z mymachine evntslog - ID47 [exampleSDID@32473 iut=\"3\"] An application event"]);
        Assert.Equal("syslog5424", f5.Id);
        var p = f5.Parse("<165>1 2003-10-11T22:14:15.003Z mymachine evntslog - ID47 - hi");
        Assert.Equal(("mymachine", "INFO", "hi"), (p["host"], p["level"], p["message"]));
        Assert.Equal("logcat", FormatDetector.Detect(["10-14 12:00:00.123  1234  5678 E ActivityManager: crash"]).Id);
        var w = FormatDetector.Detect(["#Software: IIS", "#Fields: date time cs-method cs-uri-stem sc-status", "2024-05-01 10:00:00 GET /x 404"]);
        Assert.Equal("w3c", w.Id);
        Assert.Equal(Level.Warn, w.Quick("2024-05-01 10:00:00 GET /x 404").Level);
    }

    [Fact]
    public void FallsBackToPlainAndSupportsCustomRegex()
    {
        Assert.Equal("plain", FormatDetector.Detect(["hello", "world"]).Id);
        var f = FormatDetector.Detect(["A|2024-05-01 10:00:00|E|boom"], custom: [new CustomFormat("Mine", @"^(?<app>\w)\|(?<timestamp>[^|]+)\|(?<level>\w)\|(?<message>.*)$")]);
        Assert.Equal("custom:Mine", f.Id);
        Assert.Equal(Level.Error, f.Quick("A|2024-05-01 10:00:00|E|boom").Level);
        var p = f.Parse("A|2024-05-01 10:00:00|E|boom");
        Assert.Equal(("A", "2024-05-01 10:00:00", "boom"), (p["app"], p["time"], p["message"]));
    }
}

public class QueryTests
{
    [Fact]
    public void HandlesAndOrNotPhrasesRegexAndFields()
    {
        Assert.Equal(2, Query.Parse("a OR b -c").Count);
        var q = Query.CompileQuery("timeout OR refused -retry logger:Db");
        Func<string, string> F(string logger) => k => k == "logger" ? logger : "";
        Assert.True(q("query timeout", F("com.acme.Db")));
        Assert.True(q("connection refused", F("com.acme.Db")));
        Assert.False(q("timeout retry", F("com.acme.Db")));
        Assert.False(q("timeout", F("com.acme.App")));
        Assert.True(Query.CompileQuery(@"/err(or)?\s+\d+/")("ERROR 42", _ => ""));
        Assert.True(Query.CompileQuery("\"exact phrase\"")("an exact phrase here", _ => ""));
        Assert.True(Query.CompileQuery("logger:Db OR level:fatal")("x", k => k == "level" ? "FATAL" : ""));
    }
}

public class FileSourceTests
{
    [Fact]
    public async Task IndexesMultiLineEntries()
    {
        using var s = new FileSource(0, TestFiles.Write("a.log", TestFiles.Log4j), new SourceOptions { ChunkSize = 64 });
        await s.OpenAsync();
        Assert.Equal(9, s.LineCount);
        Assert.Equal(6, s.EntryCount);
        Assert.Equal([3, 2, 4, 5, 3, 6], s.Levels.ToArray().Select(b => (int)b));
        Assert.Equal((3, 6), s.EntryLines(3));
        Assert.Equal(4, s.ReadEntries(3, 4)[0].Split('\n').Length);
        Assert.Equal(3, s.EntryForLine(5));
    }

    [Fact]
    public async Task HandlesCrlfBomAndUnterminatedLastLine()
    {
        using var s = new FileSource(0, TestFiles.Write("b.log", "﻿one\r\ntwo\r\nthree"));
        await s.OpenAsync();
        Assert.Equal(["one", "two", "three"], s.ReadEntries(0, 3));
    }

    [Fact]
    public void DetectsWindows1252WhenTheOnlyHighByteIsLast()
    {
        Assert.Equal("windows-1252", FileSource.DetectEncoding(TestFiles.Write("cafe.log", [.. "caf"u8, 0xE9])).Encoding);
        Assert.Equal("utf-8", FileSource.DetectEncoding(TestFiles.Write("cafe8.log", "café")).Encoding);
        // A 64 KB sample cut inside a UTF-8 sequence is still UTF-8.
        var big = Enumerable.Repeat((byte)'a', 64 * 1024 - 1).Concat("é"u8.ToArray()).ToArray();
        Assert.Equal("utf-8", FileSource.DetectEncoding(TestFiles.Write("cut.log", big)).Encoding);
    }

    [Theory]
    [InlineData("utf-8")]
    [InlineData("utf-16le")]
    [InlineData("utf-16be")]
    public async Task ForcedEncodingSkipsItsByteOrderMark(string enc)
    {
        Encoding e = enc switch { "utf-16le" => new UnicodeEncoding(false, true), "utf-16be" => new UnicodeEncoding(true, true), _ => new UTF8Encoding(true) };
        var bytes = e.GetPreamble().Concat(e.GetBytes("first\nsecond\n")).ToArray();
        using var s = new FileSource(0, TestFiles.Write($"bom-{enc}.log", bytes), new SourceOptions { Encoding = enc });
        await s.OpenAsync();
        Assert.Equal(["first", "second"], s.ReadEntries(0, 2));
    }

    [Fact]
    public async Task FollowsAppendsContinuationLinesAndTruncation()
    {
        var p = TestFiles.Write("c.log", "2024-01-01 00:00:00 INFO a\n2024-01-01 00:00:01 ERROR b\n");
        using var s = new FileSource(0, p);
        await s.OpenAsync();
        Assert.Equal(2, s.EntryCount);
        File.AppendAllText(p, "  at stack\n2024-01-01 00:00:02 WARN c\npartial");
        var r = await s.RefreshAsync();
        Assert.Equal(new RefreshResult(true, false, 1), r);
        Assert.Equal(3, s.EntryCount);
        Assert.Equal("2024-01-01 00:00:01 ERROR b\n  at stack", s.ReadEntries(1, 2)[0]);
        Assert.Equal("2024-01-01 00:00:02 WARN c\npartial", s.ReadEntries(2, 3)[0]);
        File.AppendAllText(p, " line done\n");
        await s.RefreshAsync();
        Assert.Equal("2024-01-01 00:00:02 WARN c\npartial line done", s.ReadEntries(2, 3)[0]);
        File.WriteAllText(p, "2024-01-01 00:00:09 INFO new\n");
        r = await s.RefreshAsync();
        Assert.True(r.Reset);
        Assert.Equal(["2024-01-01 00:00:09 INFO new"], s.ReadEntries(0, 5));
    }

    [Fact]
    public async Task OpensGzipAndUtf16()
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, true)) gz.Write(Encoding.UTF8.GetBytes(TestFiles.Log4j));
        using var g = new FileSource(0, TestFiles.Write("d.log.gz", ms.ToArray()));
        await g.OpenAsync();
        Assert.Equal(6, g.EntryCount);
        Assert.True(g.Compressed);
        Assert.False(g.Live);

        var bytes = new byte[] { 0xff, 0xfe }.Concat(Encoding.Unicode.GetBytes("héllo\nwörld\n")).ToArray();
        using var u = new FileSource(0, TestFiles.Write("e.log", bytes));
        await u.OpenAsync();
        Assert.Equal("utf-16le", u.EncodingName);
        Assert.Equal(["héllo", "wörld"], u.ReadEntries(0, 2));
    }

    [Fact]
    public async Task DecodesWindows1252()
    {
        using var s = new FileSource(0, TestFiles.Write("f.log", [0x63, 0x61, 0x66, 0xe9, 0x0a]));
        await s.OpenAsync();
        Assert.Equal("windows-1252", s.EncodingName);
        Assert.Equal(["café"], s.ReadEntries(0, 1));
    }

    [Fact]
    public void QuotedFieldValuesAndAutocompleteTokens()
    {
        var t = Query.Parse("logger:\"com.acme Db\" -thread:\"worker 1\" http.status:5");
        Assert.Equal(3, t.Count);
        Assert.Equal(("logger", "com.acme Db", false), (t[0].Any[0].Field, t[0].Any[0].Pattern.Text, t[0].Negate));
        Assert.Equal(("thread", "worker 1", true), (t[1].Any[0].Field, t[1].Any[0].Pattern.Text, t[1].Negate));
        Assert.Equal("http.status", t[2].Any[0].Field);
        Assert.Equal("re", Query.Parse("-/re/")[0].Any[0].Pattern.Text);
        Assert.Single(Query.Parse("error logger:")); // a field name being typed is ignored

        Assert.Equal((6, 9), Query.TokenAt("error logger:Db", 15));
        Assert.Equal((6, 10), Query.TokenAt("error level:\"a b", 16));
        Assert.Equal((5, 0), Query.TokenAt("abc  ", 5));
        Assert.Equal(("-", "logger", "Db"), Query.SplitToken("-logger:Db"));
        Assert.Equal(("", null, "plain"), Query.SplitToken("plain"));
        Assert.Equal(("", "level", "a b"), Query.SplitToken("level:\"a b"));
        Assert.Equal("logger:com.acme", Query.FieldToken("logger", "com.acme"));
        Assert.Equal("logger:\"a b\"", Query.FieldToken("logger", "a b"));
    }
}

public class TokenizerTests
{
    private static List<(string, TokenKind)> T(string s) => Tokenizer.Tokenize(s).Select(t => (s.Substring(t.Start, t.Length), t.Kind)).ToList();

    [Fact]
    public void FindsKeysGuidsIpsAndDurations()
    {
        Assert.Equal(
            [("user", TokenKind.Key), ("id", TokenKind.Key), ("550e8400-e29b-41d4-a716-446655440000", TokenKind.Guid), ("10.0.0.12:8080", TokenKind.Ip), ("35ms", TokenKind.Duration)],
            T("user=bob id=550e8400-e29b-41d4-a716-446655440000 from 10.0.0.12:8080 took 35ms"));
    }

    [Fact]
    public void UrlsPathsExceptionsAndLiterals()
    {
        Assert.Equal([("GET", TokenKind.Method), ("https://x.io/a?b=1", TokenKind.Url)], T("GET https://x.io/a?b=1, done"));
        Assert.Equal([("/var/log/app.log", TokenKind.Path)], T("at /var/log/app.log"));
        Assert.Equal([(@"C:\logs\app.log", TokenKind.Path)], T(@"open C:\logs\app.log failed"));
        Assert.Equal([("java.lang.NullPointerException", TokenKind.Exception)], T("java.lang.NullPointerException"));
        Assert.Equal([("42", TokenKind.Number), ("ok", TokenKind.Key), ("true", TokenKind.Keyword)], T("count 42 ok=true"));
        Assert.Equal([("\"quoted text\"", TokenKind.String)], T("\"quoted text\""));
        Assert.Equal([("2024-01-02T03:04:05.123Z", TokenKind.Date)], T("2024-01-02T03:04:05.123Z"));
        Assert.Equal([("12:30:01.5", TokenKind.Date)], T("at 12:30:01.5"));
        Assert.Empty(T("v1.2.3 don't"));
    }

    [Fact]
    public void CapsTokenCountAndLength()
    {
        Assert.Equal(5, Tokenizer.Tokenize(string.Join(' ', Enumerable.Range(0, 50)), 5).Count);
        var tokens = Tokenizer.Tokenize(new string('x', 2500) + " 42");
        Assert.Empty(tokens);
    }
}

public class TimeFormatTests
{
    [Fact]
    public void FormatsModes()
    {
        var ms = new DateTimeOffset(2024, 5, 1, 10, 2, 3, 45, TimeSpan.Zero).ToUnixTimeMilliseconds();
        Assert.Equal("2024-05-01 10:02:03.045Z", Timestamps.Format(ms, true));
        Assert.Equal("10:02:03.045Z", Timestamps.FormatTimeOfDay(ms, true));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("HH:mm:ss.fff"), Timestamps.FormatTimeOfDay(ms, false));
        Assert.Equal("+1.250 s", Timestamps.FormatSigned(1250));
        Assert.Equal("-03m 02.000s", Timestamps.FormatSigned(-182000));
        Assert.Equal("0 ms", Timestamps.FormatSigned(0));
        Assert.Equal(["+850ms", "+32s", "+2m", "+3h", "+1d"], new[] { 850L, 32_000, 125_000, 3 * 3_600_000 + 5, 90_000_000 }.Select(Timestamps.FormatGap));
    }
}

public class PatternTests
{
    [Fact]
    public void TemplatesMaskVariableParts()
    {
        Assert.Equal("Payment <num> failed after <num> ms (req <num>)", Patterns.Template("Payment 42 failed after 30000 ms (req 133)"));
        Assert.Equal("user=bob id=<guid> from <ip>", Patterns.Template("user=bob id=550e8400-e29b-41d4-a716-446655440000 from 10.0.0.12:8080"));
        Assert.Equal("GET <url> took <num>", Patterns.Template("GET https://x.io/a?b=1 took 35ms"));
        Assert.Equal("token <str> for <hex>", Patterns.Template("token \"abc def\" for deadbeef42"));
        Assert.Equal("at <time> ok", Patterns.Template("at 2024-01-02T03:04:05Z ok"));
        Assert.Equal("first line", Patterns.Template("first   line\nsecond 2"));
    }

    [Fact]
    public void MessageAndRepeatKeys()
    {
        var none = new Dictionary<string, string>();
        Assert.Equal("[main] hello", Patterns.MessageOf("2024-05-01 10:00:00 [main] hello", none));
        Assert.Equal("msg", Patterns.MessageOf("x", new Dictionary<string, string> { ["message"] = "msg" }));
        var f = new Dictionary<string, string> { ["message"] = "Payment 1 done", ["logger"] = "Api" };
        Assert.Equal("3|Api|Payment 1 done", Patterns.RepeatKey(Level.Info, f, "", "exact"));
        Assert.Equal("3|Payment <num> done", Patterns.RepeatKey(Level.Info, f, "", "pattern"));
        Assert.NotEqual(Patterns.Hash("a"), Patterns.Hash("b"));
    }

    [Fact]
    public void FoldsFrameworkFrames()
    {
        var p = StackFolding.DefaultPackages;
        string[] java = ["Boom", "\tat com.shop.A.run(A.java:1)", "\tat com.shop.B.run(B.java:2)", "\tat java.lang.Thread.run(Thread.java:3)",
            "\tat java.util.concurrent.X.y(X.java:4)", "\tat java.base/jdk.internal.Z.z(Z.java:5)", "\tat com.shop.C.run(C.java:6)"];
        var f = StackFolding.Fold(java, p);
        Assert.Equal(["Boom", "\tat com.shop.A.run(A.java:1)", "\tat com.shop.B.run(B.java:2)", "… 3 framework frames", "\tat com.shop.C.run(C.java:6)"], f.Select(x => x.Line));
        Assert.Equal(3, f[3].Folded);
        Assert.Equal(3, StackFolding.Fold(["x", "  at java.lang.Thread.run(Thread.java:1)", "  at app.Main(Main.java:2)"], p).Count);
        string[] py = ["Traceback (most recent call last):", "  File \"/app/main.py\", line 3, in <module>", "    run()",
            "  File \"/usr/lib/python3/site-packages/flask/app.py\", line 9, in run", "    self.go()",
            "  File \"/usr/lib/python3/site-packages/flask/x.py\", line 2, in go", "    raise E", "ValueError: bad"];
        var pf = StackFolding.Fold(py, p);
        Assert.Equal(["Traceback (most recent call last):", "  File \"/app/main.py\", line 3, in <module>", "    run()", "… 4 framework lines", "ValueError: bad"], pf.Select(x => x.Line));
    }
}
