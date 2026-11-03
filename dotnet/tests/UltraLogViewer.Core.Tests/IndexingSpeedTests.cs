using System.Text;

namespace UltraLogViewer.Core.Tests;

/// <summary>The fast indexing paths must give exactly the answers of the simple ones they replace.</summary>
public class IndexingSpeedTests
{
    [Fact]
    public void LevelDetectionMatchesTheRegexSpecification()
    {
        string[] pieces =
        [
            "INFO", "WARN", "WARNING", "ERROR", "ERR", "ERRORS", "Error", "error", "warn", "Warning", "crit", "CRITICAL", "notice", "alert", "emerg",
            "FINE", "FINER", "INFORMATION", "PANIC", "SEVERE", "DEBUG", "debug", "trace", "VERBOSE", "fatal", "ınfo", "İNFO", "ſevere", "x", "42", "_",
            " ", "  ", "\t", "[", "]", "<", ">", "(", ")", "|", ":", "-", ".", "/", "=", " ", " ", "é", "́", "ः", "‍", "‿", "٣", "Ⅻ", "😀",
        ];
        var rnd = new Random(7);
        for (var n = 0; n < 200_000; n++)
        {
            var sb = new StringBuilder();
            var parts = rnd.Next(1, 12);
            for (var i = 0; i < parts; i++) sb.Append(pieces[rnd.Next(pieces.Length)]);
            if (rnd.Next(20) == 0) sb.Insert(0, new string('a', rnd.Next(230, 245)));
            var s = sb.ToString();
            Assert.True(Levels.DetectWithRegex(s) == Levels.Detect(s), $"\"{s}\": regex {Levels.DetectWithRegex(s)}, fast {Levels.Detect(s)}");
        }
    }

    [Theory]
    [InlineData("{\"@timestamp\":\"2024-05-01T10:00:00.123Z\",\"level\":\"warn\",\"message\":\"m\"}")]
    [InlineData("{\"Level\":\"warn\",\"Time\":\"2024-05-01T10:00:00Z\"}")]
    [InlineData("{\"LEVEL\":\"debug\",\"level\":\"error\",\"TS\":1714550400,\"time\":\"2024-05-01 10:00:00\"}")]
    [InlineData("{\"log\":{\"level\":\"error\",\"x\":{\"level\":\"info\"}},\"ts\":1714550400.5}")]
    [InlineData("{\"log.level\":\"warn\",\"log\":{\"level\":\"error\"}}")]
    [InlineData("{\"level\":30,\"time\":1714550400123,\"msg\":\"pino\"}")]
    [InlineData("{\"level\":30.5,\"time\":\"garbage\"}")]
    [InlineData("{\"level\":null,\"severity\":\"CRITICAL\",\"@t\":null}")]
    [InlineData("{\"level\":{\"name\":\"error\"},\"time\":[1,2]}")]
    [InlineData("{\"le\\u0076el\":\"error\",\"date\":\"01/May/2024:10:00:00 +0000\"}")]
    [InlineData("  {\"severity\":\"W\",\"t\":true}")]
    [InlineData("{\"level\":\"info\"} trailing")]
    [InlineData("{\"broken\": ")]
    [InlineData("[1,2,3]")]
    [InlineData("plain error: text")]
    [InlineData("")]
    [InlineData("{\"time\":1e400,\"level\":\"60\"}")]
    public void JsonByteScanMatchesTheStringPath(string line)
    {
        var f = new JsonFormat();
        var expected = f.Quick(line);
        var fast = f.ScanUtf8(Encoding.UTF8.GetBytes(line), out var q);
        if (fast is null) return; // falls back to Quick itself
        Assert.True(fast);
        Assert.Equal(expected, q);
    }

    private static string Messy()
    {
        var rnd = new Random(3);
        var sb = new StringBuilder("header that is not an entry\r\nsecond header\n");
        for (var i = 0; i < 4000; i++)
        {
            sb.Append($"2024-05-01 10:{i / 60 % 60:00}:{i % 60:00},{i % 1000:000} [t{i % 3}] {(i % 7 == 0 ? "ERROR" : "INFO")} c.x - msg {i} {new string('é', rnd.Next(0, 200))}\n");
            if (rnd.Next(5) == 0) sb.Append("\tat frame\r\n");
            if (rnd.Next(100) == 0) sb.Append(new string('x', rnd.Next(100, 9000))).Append('\n');
            if (rnd.Next(50) == 0) sb.Append("no timestamp here WARN\n");
        }
        return sb.Append("2024-05-01 11:00:00,000 ERROR unterminated").ToString();
    }

    private static async Task<List<(int, int, Level, long)>> Index(string path, int chunk)
    {
        using var s = new FileSource(0, path, new SourceOptions { ChunkSize = chunk });
        await s.OpenAsync();
        var list = new List<(int, int, Level, long)>();
        for (var e = 0; e < s.EntryCount; e++)
        {
            var (a, b) = s.EntryLines(e);
            list.Add((a, b, s.LevelOf(e), s.TimeOf(e)));
        }
        return list;
    }

    [Fact]
    public async Task ChunkSizeDoesNotChangeTheIndex()
    {
        var path = TestFiles.Write("messy.log", Messy());
        var reference = await Index(path, 64 << 20); // one chunk
        Assert.Equal((0, 1), (reference[0].Item1, reference[0].Item2)); // the header lines form the first entry
        foreach (var chunk in new[] { 64, 1000, 4096, 65536 })
            Assert.Equal(reference, await Index(path, chunk));
    }

    [Fact]
    public async Task StatsFollowAppendsAndTruncation()
    {
        var path = TestFiles.Write("stats.log", "2024-01-01 00:00:01 INFO a\n2024-01-01 00:00:05 ERROR b\n");
        using var d = new LogDocument("t");
        await d.AddSourceAsync(path, new SourceOptions());
        var info = d.Info();
        Assert.Equal(1, info.LevelCounts[(int)Level.Info]);
        Assert.Equal(1, info.LevelCounts[(int)Level.Error]);
        File.AppendAllText(path, "2024-01-01 00:00:09 WARN c\n");
        Assert.True(await d.RefreshAsync());
        info = d.Info();
        Assert.Equal([0, 0, 0, 1, 1, 1, 0], info.LevelCounts);
        Assert.Equal(TestFiles.Local(2024, 1, 1, 0, 0, 9) - TestFiles.Local(2024, 1, 1, 0, 0, 1), info.TimeMax - info.TimeMin);
        File.WriteAllText(path, "2024-01-01 00:00:03 DEBUG new\n"); // truncated / rotated
        Assert.True(await d.RefreshAsync());
        info = d.Info();
        Assert.Equal(1, info.LevelCounts.Sum());
        Assert.Equal(info.TimeMin, info.TimeMax);
    }
}
