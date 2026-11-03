using System.Text;
using System.Text.Json;

namespace UltraLogViewer.Core.Tests;

/// <summary>
/// Search, filtering, grouping, patterns, field stats and export scan entry batches on all cores and merge the results
/// in order. Whatever the batch size, they must give exactly what a sequential pass gives.
/// </summary>
public class ParallelScanTests
{
    private static readonly int[] BatchSizes = [1, 7, 64, 4096];

    /// <summary>Multi-line entries, CRLF and stray CRs, a header that is not an entry, long lines and an unterminated tail.</summary>
    private static string Messy(int n = 3000, int seed = 5)
    {
        var rnd = new Random(seed);
        var sb = new StringBuilder("header that is not an entry\r\nsecond header\n");
        string[] loggers = ["com.acme.Db", "com.acme.Api", "org.pay.Payment", "c.x"];
        string[] levels = ["INFO", "DEBUG", "WARN", "ERROR", "INFO", "TRACE"];
        for (var i = 0; i < n; i++)
        {
            var lv = levels[rnd.Next(levels.Length)];
            var msg = rnd.Next(6) switch
            {
                0 => $"request {rnd.Next(1000)} timed out after {rnd.Next(5000)}ms",
                1 => $"payment id={Guid.NewGuid()} amount={rnd.Next(100)}.{rnd.Next(100):00} ok",
                2 => $"user \"u{rnd.Next(50)}\" logged in from 10.0.{rnd.Next(256)}.{rnd.Next(256)}",
                3 => "cache warm",
                4 => $"msg {i} {new string('é', rnd.Next(0, 80))}",
                _ => $"Timed Out waiting for lock {rnd.Next(10)}",
            };
            sb.Append($"2024-05-01 10:{i / 60 % 60:00}:{i % 60:00},{i % 1000:000} [t{i % 3}] {lv} {loggers[rnd.Next(loggers.Length)]} - {msg}");
            sb.Append(rnd.Next(4) == 0 ? "\r\n" : "\n");
            if (rnd.Next(6) == 0) sb.Append("java.lang.IllegalStateException: timed out\n\tat com.acme.Db.query(Db.java:42)\r\n\tat x.y(z.java:1)\n");
            if (rnd.Next(80) == 0) sb.Append(new string('x', rnd.Next(100, 6000))).Append(" payment\n");
            if (rnd.Next(40) == 0) sb.Append("no timestamp here WARN\r\r\n");
            if (rnd.Next(30) == 0) sb.Append("cache warm\n"); // continuation lines that repeat
        }
        return sb.Append("2024-05-01 11:00:00,000 [t0] ERROR c.x - unterminated timed out").ToString();
    }

    private static async Task<LogDocument> Open(string path, int batch, bool parallel, string? second = null)
    {
        var d = new LogDocument("t") { ScanBatch = batch, ScanInParallel = parallel };
        await d.AddSourceAsync(path, new SourceOptions());
        if (second is not null) await d.AddSourceAsync(second, new SourceOptions());
        return d;
    }

    private static string Rows(LogDocument d) =>
        string.Join(";", d.GetRows(0, d.ViewCount).Select(r => $"{r.Key}{(r.IsContext ? "c" : "")}{(r.Repeat > 1 ? "x" + r.Repeat : "")}"));

    private static FilterSpec[] Specs()
    {
        static FilterRule R(string text, RuleMode mode = RuleMode.Include, string? column = null, bool regex = false) =>
            new() { Text = text, Mode = mode, Column = column, Regex = regex };
        var noDebug = new FilterSpec { Query = "timed out" };
        noDebug.Levels[(int)Level.Debug] = false;
        return
        [
            new() { Query = "payment" },
            new() { Query = "timed out", Context = 2 },
            new() { Query = "logger:Db OR level:error -warm" },
            new() { Rules = [R("timed out"), R("lock", RuleMode.Exclude)] },
            new() { Rules = [R(@"t[12]\]", regex: true)], Context = 1 },
            new() { Rules = [R("Payment", column: "logger")] },
            new() { Rules = [new FilterRule { Text = "cache warm", PatternMatch = true }] },
            noDebug,
            new() { Query = "timed out", GroupRepeats = "pattern" },
            new() { GroupRepeats = "exact", Context = 1, Query = "warm OR lock" },
        ];
    }

    private static async Task<List<string>> Everything(LogDocument d, string prefix)
    {
        var output = new List<string>();
        foreach (var p in new[] { new TextPattern("timed out"), new TextPattern("TIMED", CaseSensitive: true), new TextPattern(@"\d{4}ms", Regex: true) })
            output.Add(JsonSerializer.Serialize(await d.SearchAsync(p)));
        var i = 0;
        foreach (var spec in Specs())
        {
            await d.ApplyFilterAsync(spec);
            output.Add(Rows(d));
            output.Add(JsonSerializer.Serialize(await d.SearchAsync(new TextPattern("timed out"))));
            d.IgnoreFilter = true; // show in context
            output.Add(Rows(d));
            output.Add(JsonSerializer.Serialize(await d.SearchAsync(new TextPattern("payment"))));
            d.IgnoreFilter = false;
            output.Add(JsonSerializer.Serialize(await d.PatternsAsync(maxRows: 900, top: 50), new JsonSerializerOptions { IncludeFields = true }));
            output.Add(JsonSerializer.Serialize(await d.FieldStatsAsync(maxRows: 700), new JsonSerializerOptions { IncludeFields = true }));
            foreach (var fmt in new[] { "text", "csv", "jsonl" })
            {
                var file = $"{prefix}{i++}.{fmt}";
                await d.ExportAsync(file, fmt);
                output.Add(File.ReadAllText(file));
            }
        }
        await d.ApplyFilterAsync(null);
        output.Add(JsonSerializer.Serialize(await d.PatternsAsync(), new JsonSerializerOptions { IncludeFields = true }));
        output.Add(JsonSerializer.Serialize(await d.FieldStatsAsync(), new JsonSerializerOptions { IncludeFields = true }));
        return output;
    }

    [Fact]
    public async Task EveryScanGivesTheSequentialResultForAnyBatchSize()
    {
        var path = TestFiles.Write("messy.log", Messy());
        var dir = Directory.CreateTempSubdirectory("ulv-par-").FullName;
        using var reference = await Open(path, 4096, parallel: false);
        var expected = await Everything(reference, Path.Combine(dir, "ref-"));
        Assert.Contains(expected, s => s.Contains('c') && s.Contains(';')); // context rows were produced
        foreach (var batch in BatchSizes)
        {
            using var d = await Open(path, batch, parallel: true);
            var actual = await Everything(d, Path.Combine(dir, $"{batch}-"));
            Assert.Equal(expected.Count, actual.Count);
            for (var k = 0; k < expected.Count; k++) Assert.True(expected[k] == actual[k], $"batch {batch}, result {k} differs");
        }
    }

    [Fact]
    public async Task MergedViewsGiveTheSequentialResult()
    {
        var a = TestFiles.Write("a.log", Messy(1500, 8));
        var b = TestFiles.Write("b.log", Messy(1200, 9).Replace("10:", "10:3"));
        var dir = Directory.CreateTempSubdirectory("ulv-par-").FullName;
        using var reference = await Open(a, 4096, parallel: false, second: b);
        var expected = await Everything(reference, Path.Combine(dir, "ref-"));
        foreach (var batch in new[] { 5, 300 })
        {
            using var d = await Open(a, batch, parallel: true, second: b);
            Assert.Equal(expected, await Everything(d, Path.Combine(dir, $"{batch}-")));
        }
    }

    [Fact]
    public async Task TailFilteringStaysEqualToASequentialRun()
    {
        var content = Messy(2000, 11);
        var cut = content.IndexOf("\n2024-05-01 10:20", StringComparison.Ordinal) + 1;
        var pathSeq = TestFiles.Write("tail-seq.log", content[..cut]);
        var pathPar = TestFiles.Write("tail-par.log", content[..cut]);
        using var seq = await Open(pathSeq, 4096, parallel: false);
        using var par = await Open(pathPar, 3, parallel: true);
        var spec = new FilterSpec { Query = "timed out OR payment", Context = 1 };
        await seq.ApplyFilterAsync(spec);
        await par.ApplyFilterAsync(spec);
        Assert.Equal(Rows(seq), Rows(par));
        // Grows in a few steps, the first one mid-entry (continuation lines arrive later).
        var rest = content[cut..];
        foreach (var piece in new[] { rest[..40], rest[40..(rest.Length / 2)], rest[(rest.Length / 2)..] })
        {
            File.AppendAllText(pathSeq, piece);
            File.AppendAllText(pathPar, piece);
            Assert.True(await seq.RefreshAsync());
            Assert.True(await par.RefreshAsync());
            Assert.Equal(Rows(seq), Rows(par));
        }
        // And the same as filtering the whole file at once.
        using var whole = await Open(TestFiles.Write("whole.log", content), 4096, parallel: false);
        await whole.ApplyFilterAsync(spec);
        Assert.Equal(Rows(whole), Rows(par));
    }

    [Fact]
    public async Task RowsAppearWhileFilteringAndNeverMove()
    {
        foreach (var context in new[] { 0, 2 })
        {
            using var d = await Open(TestFiles.Write("p.log", Messy(4000, 13)), 50, parallel: true);
            d.PartialViewMs = 0; // publish after every batch
            var startEpoch = d.ViewEpoch;
            var partials = new List<(string Rows, int Epoch)>();
            await d.ApplyFilterAsync(new FilterSpec { Query = "timed out", Context = context }, new SyncProgress(_ => partials.Add((Rows(d), d.ViewEpoch))));
            var final = Rows(d);
            var shown = partials.Where(p => p.Epoch != startEpoch).ToList();
            Assert.True(shown.Count > 5, $"only {shown.Count} partial views");
            Assert.Contains(shown, p => p.Rows.Length > 0 && p.Rows.Length < final.Length);
            foreach (var (rows, epoch) in shown)
            {
                Assert.StartsWith(rows, final); // every partial view is a prefix of the final one ...
                Assert.Equal(d.ViewEpoch, epoch); // ... so all of them, and the final view, share one epoch
            }
            for (var k = 1; k < shown.Count; k++) Assert.StartsWith(shown[k - 1].Rows, shown[k].Rows);
        }
    }

    [Fact]
    public async Task MergedViewsOnlyShowTheSettledTopWhileFiltering()
    {
        var a = TestFiles.Write("a.log", Messy(1500, 21));
        var b = TestFiles.Write("b.log", Messy(1500, 22));
        using var d = await Open(a, 40, parallel: true, second: b);
        d.PartialViewMs = 0;
        var partials = new List<string>();
        await d.ApplyFilterAsync(new FilterSpec { Query = "payment", Context = 1 }, new SyncProgress(_ => partials.Add(Rows(d))));
        var final = Rows(d);
        Assert.Contains(partials, p => p.Length > 0 && p.Length < final.Length);
        foreach (var p in partials.Skip(1)) Assert.StartsWith(p, final);
    }

    [Fact]
    public async Task GroupedFiltersSwapTheViewOnlyAtTheEnd()
    {
        using var d = await Open(TestFiles.Write("g.log", Messy(2000, 17)), 30, parallel: true);
        d.PartialViewMs = 0;
        var epochs = new HashSet<int>();
        var before = d.ViewEpoch;
        await d.ApplyFilterAsync(new FilterSpec { Query = "warm", GroupRepeats = "exact" }, new SyncProgress(_ => epochs.Add(d.ViewEpoch)));
        Assert.Equal([before], epochs);
        Assert.NotEqual(before, d.ViewEpoch);
        Assert.Contains("x", Rows(d));
    }

    /// <summary>The view as it was built before it could be built progressively (grep -C semantics).</summary>
    private static List<(int Row, bool Context)> SimpleView(bool[] hit, int context)
    {
        var rows = new List<(int, bool)>();
        var lastAdded = -1;
        for (var b = 0; b < hit.Length; b++)
        {
            if (!hit[b]) continue;
            for (var c = Math.Max(lastAdded + 1, b - context); c < b; c++) rows.Add((c, true));
            rows.Add((b, false));
            lastAdded = b;
            for (var c = b + 1; c <= Math.Min(hit.Length - 1, b + context); c++)
            {
                if (hit[c]) break;
                rows.Add((c, true));
                lastAdded = c;
            }
        }
        return rows;
    }

    [Fact]
    public async Task ContextRowsAreThoseOfTheSimpleAlgorithm()
    {
        var path = TestFiles.Write("ctx.log", Messy(2500, 31));
        using var d = await Open(path, 64, parallel: true);
        var texts = d.Sources[0].ReadEntries(0, d.BaseCount);
        foreach (var needle in new[] { "timed out", "payment", "cache warm" })
        {
            var hit = texts.Select(t => t.Contains(needle, StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (var context in new[] { 0, 1, 2, 5 })
            {
                await d.ApplyFilterAsync(new FilterSpec { Rules = [new FilterRule { Text = needle }], Context = context });
                var expected = SimpleView(hit, context);
                Assert.Equal(expected, d.GetRows(0, d.ViewCount).Select(r => (r.Entry, r.IsContext)).ToList());
            }
        }
    }

    /// <summary>The old way of reading entries: decode everything, split on '\n', trim each line's '\r's, join.</summary>
    private static List<string> SimpleEntries(byte[] file, Encoding enc, int bom, FileSource s)
    {
        var lines = enc.GetString(file, bom, file.Length - bom).Split('\n');
        var output = new List<string>();
        for (var e = 0; e < s.EntryCount; e++)
        {
            var (a, b) = s.EntryLines(e);
            output.Add(string.Join('\n', lines[a..(b + 1)].Select(l => l.TrimEnd('\r'))));
        }
        return output;
    }

    [Theory]
    [InlineData("utf-8")]
    [InlineData("windows-1252")]
    public async Task ReadEntriesDecodesLikeTheSimpleWay(string encoding)
    {
        var text = Messy(800, 3) + "\n2024-05-01 11:00:01 INFO cr\r in the middle\r\r\n\tand\r\r\r\n\r\n\n2024-05-01 11:00:02 WARN last\r";
        var bytes = encoding == "utf-8" ? Encoding.UTF8.GetBytes(text) : Encoding.GetEncoding(1252, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback).GetBytes(text);
        if (encoding == "utf-8")
        {
            // Broken sequences, also right before a line end.
            bytes = [.. bytes, .. "\n2024-05-01 11:00:03 ERROR bad "u8, 0xe2, 0x82, (byte)'\n', .. "\tcont "u8, 0xff, (byte)'\r', (byte)'\n', .. "2024-05-01 11:00:04 INFO "u8, 0xc3, (byte)'\r', (byte)'\n'];
        }
        var path = TestFiles.Write("enc.log", bytes);
        using var s = new FileSource(0, path, new SourceOptions { Encoding = encoding });
        await s.OpenAsync();
        var enc = encoding == "utf-8" ? new UTF8Encoding(false, false) : Encoding.GetEncoding(1252);
        var expected = SimpleEntries(bytes, enc, 0, s);
        Assert.True(expected.Count > 800);
        Assert.Equal(expected, s.ReadEntries(0, s.EntryCount));
        var rnd = new Random(1);
        for (var k = 0; k < 200; k++)
        {
            var from = rnd.Next(s.EntryCount);
            var to = Math.Min(s.EntryCount, from + rnd.Next(1, 50));
            Assert.Equal(expected[from..to], s.ReadEntries(from, to));
        }
    }
}
