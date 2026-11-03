using System.Globalization;
using System.Text.Json;
using UltraLogViewer.Core;
using Xunit;

namespace UltraLogViewer.Core.Tests;

/// <summary>
/// Detection, entry grouping, level, time and fields for every sample in test/fixtures/format-samples.json, which the
/// Electron suite reads too. It holds samples of the existing formats as well, so a new format that steals them fails.
/// </summary>
public class FormatSampleTests
{
    public sealed record Sample(string Name, string Id, string[] Lines, int? Entries, string?[]? Levels, string?[]? Times, Dictionary<string, JsonElement>[]? Fields);

    private static readonly Lazy<Sample[]> All = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "test", "fixtures", "format-samples.json"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var json = File.ReadAllText(Path.Combine(dir.FullName, "test", "fixtures", "format-samples.json"));
        return JsonSerializer.Deserialize<Sample[]>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    });

    public static IEnumerable<object[]> Names() => All.Value.Select(s => new object[] { s.Name });

    [Theory]
    [MemberData(nameof(Names))]
    public void DetectsAndParses(string name)
    {
        var s = All.Value.Single(x => x.Name == name);
        var f = FormatDetector.Detect(s.Lines, 2024);
        Assert.Equal(s.Id, f.Id);

        // Group lines into entries the way indexing does: a line that does not start an entry continues the last one.
        var entries = new List<(string Text, QuickInfo Q)>();
        foreach (var line in s.Lines)
        {
            if (f.Scan(line, out var q) || entries.Count == 0) entries.Add((line, q));
            else entries[^1] = (entries[^1].Text + "\n" + line, entries[^1].Q);
        }
        if (s.Entries is int n) Assert.Equal(n, entries.Count);
        if (s.Levels is { } levels)
            Assert.Equal(levels, entries.Select(e => LevelNames.Labels[(int)e.Q.Level]).ToArray());
        if (s.Times is { } times)
        {
            for (var i = 0; i < times.Length; i++)
            {
                var want = times[i] is { } t ? DateTimeOffset.Parse(t, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal).ToUnixTimeMilliseconds() : Timestamps.None;
                Assert.True(want == entries[i].Q.Time, $"entry {i}: time {entries[i].Q.Time}, expected {times[i]} ({want})");
            }
        }
        foreach (var exp in s.Fields ?? [])
        {
            var i = exp["entry"].GetInt32();
            var parsed = f.Parse(entries[i].Text);
            foreach (var (k, v) in exp)
            {
                if (k == "entry") continue;
                Assert.True(parsed.TryGetValue(k, out var got), $"entry {i}: no field {k} in {string.Join(", ", parsed.Keys)}");
                Assert.Equal(v.GetString(), got);
            }
        }
    }

    [Fact]
    public void NewFormatsCanBeChosenByHand()
    {
        foreach (var id in new[] { "logfmt", "postgres", "mysql-error", "mysql-general", "mysql-slow", "mongodb", "rust", "macos", "laravel", "winevent" })
        {
            var d = FormatDetector.DetectWithInfo(["hello"], 2024, force: id);
            Assert.Equal(id, d.Format.Id);
            Assert.False(d.Auto);
            Assert.Contains(FormatDetector.BuiltIns, b => b.Id == id);
        }
        // Without its "# Query_time:" lines a slow log is not guessed.
        Assert.NotEqual("mysql-slow", FormatDetector.Detect(["# Time: 2024-05-01T10:00:05.123456Z", "SELECT 1;"], 2024).Id);
    }

    [Fact]
    public async Task IndexesASlowQueryLogAsMultiLineEntries()
    {
        var s = All.Value.Single(x => x.Name == "MySQL slow query log");
        using var doc = new LogDocument("slow");
        await doc.AddSourceAsync(TestFiles.Write("slow.log", string.Join("\n", s.Lines) + "\n"), new SourceOptions());
        Assert.Equal("mysql-slow", doc.Info().Sources[0].FormatId);
        Assert.Equal(5, doc.ViewCount);
        Assert.Equal(Level.Info, doc.GetRows(3, 1)[0].Level);
        // Typed queries on the parsed fields: the query time is in seconds, so "1s" compares with "2.500123".
        await doc.ApplyFilterAsync(new FilterSpec { Query = "query_time>1s" });
        Assert.Equal(1, doc.ViewCount);
        await doc.ApplyFilterAsync(new FilterSpec { Query = "rows_examined<10000 and exists(query_time)" });
        Assert.Equal(1, doc.ViewCount);
        await doc.ApplyFilterAsync(new FilterSpec { Query = "db?=shop" });
        Assert.Equal(5, doc.ViewCount); // the header entries have no db, the second query has an empty one
        await doc.ApplyFilterAsync(new FilterSpec { Query = "since:2024-05-01T10:00:06Z" });
        Assert.Equal(1, doc.ViewCount);
    }
}
