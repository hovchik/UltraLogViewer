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
}
