using System.Text;

namespace UltraLogViewer.Core.Tests;

public class AnalyticsTests
{
    private static string Ts(int sec) => $"2024-05-01 {10 + sec / 3600:00}:{sec / 60 % 60:00}:{sec % 60:00},000";

    private static string SampleLog()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 600; i++)
        {
            var sec = i * 5;
            if (i % 10 == 0) sb.Append($"{Ts(sec)} [worker-{i % 3}] WARN  com.acme.Cache - Cache miss for key {i}\n");
            else sb.Append($"{Ts(sec)} [main] INFO  com.acme.Http - GET https://api.acme.io/users/{i}?x=1 from 10.0.0.{i % 4} took {i % 50} ms\n");
        }
        for (var i = 0; i < 40; i++)
            sb.Append($"{Ts(3000 + i)} [worker-1] ERROR com.acme.Db - Query {i} failed\njava.sql.SQLTimeoutException: timeout\n\tat com.acme.Db.query(Db.java:42)\n");
        return sb.ToString();
    }

    private static async Task<LogDocument> Doc(string content)
    {
        var d = new LogDocument("test");
        await d.AddSourceAsync(TestFiles.Write("a.log", content), new SourceOptions());
        return d;
    }

    /// <summary>Runs a job until it reports Done; collects every snapshot.</summary>
    private static async Task<(AnalyticsResult Result, List<AnalyticsResult> Snaps)> Run(AnalyticsJob job)
    {
        var snaps = new List<AnalyticsResult>();
        var done = new TaskCompletionSource<AnalyticsResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        job.Updated += r =>
        {
            lock (snaps) snaps.Add(r);
            if (r.State == AnalyticsState.Done) done.TrySetResult(r);
        };
        job.Start();
        var result = await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
        lock (snaps) return (result, snaps.ToList());
    }

    [Fact]
    public void ParsesDurationsAndFindsBursts()
    {
        Assert.Equal(120, AnalyticsRows.DurationMs("120ms"));
        Assert.Equal(1500, AnalyticsRows.DurationMs("1.5s"));
        Assert.True(double.IsNaN(AnalyticsRows.DurationMs("3x")));
        var counts = Enumerable.Range(0, 20).Select(i =>
        {
            var c = new int[LevelNames.Count];
            c[(int)Level.Info] = 10;
            c[(int)Level.Error] = i is 15 or 16 ? 50 : 1;
            return c;
        }).ToArray();
        var b = Assert.Single(AnalyticsMath.FindBursts(new AnalyticsTimeline(0, 1000, counts)));
        Assert.Equal((15_000L, 17_000L, 100), (b.From, b.To, b.Errors));
        Assert.True(b.Factor > 10);
    }

    [Fact]
    public async Task AnalysesTheWholeLog()
    {
        using var d = await Doc(SampleLog());
        using var job = new AnalyticsJob(d, () => false) { Batch = 100, Slice = 50, EmitMs = 0 };
        var (r, snaps) = await Run(job);
        Assert.Equal(1, r.Progress);
        Assert.Equal(640, r.Scanned);
        Assert.Equal(640, r.Total);
        Assert.True(snaps.Count(s => s.State == AnalyticsState.Running) > 3);
        Assert.Equal(540, r.Levels[(int)Level.Info]);
        Assert.Equal(60, r.Levels[(int)Level.Warn]);
        Assert.Equal(40, r.Levels[(int)Level.Error]);
        Assert.Equal(40, r.Summary.Multiline);
        Assert.Equal(640, r.Timeline!.Counts.Sum(c => c.Sum()));
        var top = r.Patterns[0];
        Assert.Equal(540, top.Count);
        Assert.Equal(Patterns.Template("GET https://api.acme.io/users/1?x=1 from 10.0.0.1 took 1 ms"), top.Template);
        Assert.Equal(540, top.DurCount);
        Assert.Equal(540, top.Spark.Sum());
        Assert.Equal("Query <num> failed", r.ErrorPatterns[0].Template);
        Assert.Contains(r.NewPatterns, p => p.Template == "Query <num> failed");
        Assert.NotEmpty(r.Bursts);
        Assert.Equal(("java.sql.SQLTimeoutException", 40), (r.Exceptions[0].Name, r.Exceptions[0].Count));
        Assert.Equal(("main", 540), r.Fields.First(f => f.Key == "thread").Top[0]);
        Assert.Equal(["10.0.0.0", "10.0.0.1", "10.0.0.2", "10.0.0.3"], r.Ips.Select(x => x.Value).Order().ToArray());
        Assert.Matches(@"^https://api\.acme\.io/users/\d+$", r.Urls[0].Value);
        Assert.Equal(("GET", 540), r.Methods[0]);
        Assert.Equal(540, r.Durations!.Count);
        Assert.Equal(49, r.Durations.Max);
    }

    [Fact]
    public async Task WaitsWhileBusy()
    {
        using var d = await Doc(SampleLog());
        var busy = true;
        using var job = new AnalyticsJob(d, () => busy) { Batch = 100, EmitMs = 0 };
        var run = Run(job);
        await Task.Delay(400);
        Assert.Equal(0, job.Snapshot().Scanned);
        Assert.Equal(AnalyticsState.Waiting, job.Snapshot().State);
        busy = false;
        Assert.Equal(640, (await run).Result.Scanned);
    }

    [Fact]
    public async Task PausesAndResumes()
    {
        using var d = await Doc(SampleLog());
        using var job = new AnalyticsJob(d, () => false) { Batch = 50, EmitMs = 0 };
        job.Start();
        job.Pause();
        Assert.Equal(AnalyticsState.Paused, job.Snapshot().State);
        var (r, _) = await Run(job);
        Assert.Equal(640, r.Scanned);
        Assert.Equal(1, r.Run);
    }

    [Fact]
    public async Task ReadsOnlyTheAppendedEntries()
    {
        var file = TestFiles.Write("tail.log", $"{Ts(0)} [main] INFO  a.B - hello 1\n{Ts(1)} [main] INFO  a.B - hello 2\n");
        using var d = new LogDocument("t");
        await d.AddSourceAsync(file, new SourceOptions());
        using var job = new AnalyticsJob(d, () => false) { EmitMs = 0 };
        var next = new TaskCompletionSource<AnalyticsResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        job.Updated += r =>
        {
            if (r.State == AnalyticsState.Done) next.TrySetResult(r);
        };
        job.Start();
        var r = await next.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, r.Scanned);
        next = new TaskCompletionSource<AnalyticsResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        File.AppendAllText(file, $"{Ts(2)} [main] ERROR a.B - boom 3\n");
        await d.RefreshAsync();
        r = await next.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(3, r.Scanned);
        Assert.Equal(2, r.Patterns.First(p => p.Template == "hello <num>").Count);
        Assert.Equal("boom <num>", r.ErrorPatterns[0].Template);
        Assert.Equal(1, r.Run);
    }
}
