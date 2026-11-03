namespace UltraLogViewer.Core.Tests;

/// <summary>Exploration tabs and split panes: several documents over one set of indexed files.</summary>
public class SharedSourceTests
{
    private static FilterSpec Query(string q) => new() { Query = q };

    private static string[] Messages(LogDocument d) => d.GetRows(0, 1000).Select(r => r.Field("message").Split('\n')[0]).ToArray();

    [Fact]
    public async Task TwoViewsShareOneFileSourceWithTheirOwnFilters()
    {
        using var a = new LogDocument("a");
        await a.AddSourceAsync(TestFiles.Write("s.log", TestFiles.Log4j), new SourceOptions());
        using var b = a.Duplicate();
        Assert.Same(a.Sources[0], b.Sources[0]);
        Assert.Same(a.Set, b.Set);
        Assert.Equal(2, a.Set.RefCount);
        Assert.Equal("a", b.Title);
        Assert.Equal(6, b.ViewCount); // no indexing: the entries are there at once

        await a.ApplyFilterAsync(Query("level:error"));
        await b.ApplyFilterAsync(Query("logger:App"));
        Assert.Equal(["Query failed"], Messages(a));
        Assert.Equal(["Starting app", "Retrying", "Giving up"], Messages(b));
        Assert.Equal(6, b.Info().Total);
        b.IgnoreFilter = true;
        Assert.Equal(1, a.ViewCount);
        Assert.Equal(6, b.ViewCount);
    }

    [Fact]
    public async Task TailRefreshReadsTheFileOnceAndUpdatesEveryView()
    {
        var p = TestFiles.Write("t.log", "2024-01-01 00:00:01 INFO ok\n2024-01-01 00:00:02 ERROR bad\n");
        using var a = new LogDocument("t");
        await a.AddSourceAsync(p, new SourceOptions());
        using var b = a.Duplicate();
        await a.ApplyFilterAsync(Query("level:error"));
        await b.ApplyFilterAsync(Query("ok"));
        File.AppendAllText(p, "2024-01-01 00:00:03 ERROR worse\n2024-01-01 00:00:04 INFO ok again\n");

        // One poll of the files; both documents take the change up from it.
        Assert.NotNull(await a.Set.RefreshAsync());
        Assert.Null(await a.Set.RefreshAsync());
        Assert.True(await a.SyncAsync());
        Assert.True(await b.SyncAsync());
        Assert.False(await b.SyncAsync());
        Assert.Equal(["bad", "worse"], Messages(a));
        Assert.Equal(["ok", "ok again"], Messages(b));

        // RefreshAsync polls and syncs; the other view sees what that poll found.
        File.AppendAllText(p, "2024-01-01 00:00:05 ERROR ok worst\n");
        Assert.True(await b.RefreshAsync());
        Assert.True(await a.RefreshAsync());
        Assert.Equal(["bad", "worse", "ok worst"], Messages(a));
        Assert.Equal(["ok", "ok again", "ok worst"], Messages(b));

        // Rotation: both start over on the new file.
        File.WriteAllText(p, "2024-01-01 00:01:00 ERROR new\n");
        Assert.True(await a.RefreshAsync());
        Assert.True(await b.SyncAsync());
        Assert.Equal(["new"], Messages(a));
        Assert.Empty(Messages(b));
    }

    [Fact]
    public async Task ClosingOneViewKeepsTheOtherWorking()
    {
        var p = TestFiles.Write("c.log", "2024-01-01 00:00:01 INFO one\n");
        var a = new LogDocument("c");
        await a.AddSourceAsync(p, new SourceOptions());
        var b = a.Duplicate();
        var c = b.Duplicate();
        Assert.Equal(3, a.Set.RefCount);

        a.Dispose();
        a.Dispose(); // twice is harmless
        Assert.Equal(2, b.Set.RefCount);
        Assert.False(b.Sources[0].Closed);
        File.AppendAllText(p, "2024-01-01 00:00:02 WARN two\n");
        Assert.True(await b.RefreshAsync());
        Assert.True(await c.SyncAsync());
        Assert.Equal(["one", "two"], Messages(b));
        await c.ApplyFilterAsync(Query("level:warn"));
        Assert.Equal(["two"], Messages(c));

        c.Dispose();
        Assert.False(b.Sources[0].Closed);
        Assert.Equal(["one", "two"], Messages(b));
        var src = b.Sources[0];
        b.Dispose();
        Assert.True(src.Closed);
        Assert.True(b.Set.Closed);
        Assert.Throws<ObjectDisposedException>(() => b.Duplicate());
    }

    [Fact]
    public async Task MergedViewsShareTheMergeAndReformatReachesEveryView()
    {
        var x = TestFiles.Write("x.log", "2024-01-01 00:00:01 INFO a1\n2024-01-01 00:00:03 INFO a3\n");
        var y = TestFiles.Write("y.log", "2024-01-01 00:00:02 ERROR b2\n2024-01-01 00:00:04 INFO b4\n");
        using var a = new LogDocument("m");
        await a.AddSourcesAsync([x, y], _ => new SourceOptions());
        using var b = a.Duplicate();
        Assert.True(b.Merged);
        Assert.Equal(["a1", "b2", "a3", "b4"], Messages(b));
        await a.ApplyFilterAsync(Query("level:error"));
        await b.ApplyFilterAsync(Query("a"));
        Assert.Equal(["b2"], Messages(a));
        Assert.Equal(["a1", "a3"], Messages(b));

        File.AppendAllText(y, "2024-01-01 00:00:05 ERROR b5 a\n");
        Assert.True(await a.RefreshAsync());
        Assert.True(await b.SyncAsync());
        Assert.Equal(["b2", "b5 a"], Messages(a));
        Assert.Equal(["a1", "a3", "b5 a"], Messages(b));
        Assert.Equal("1:2", b.GetRows(2, 1)[0].Key);

        // Plain text: the message is now the whole line, in both views, which filter again.
        await a.ReformatAsync("plain", null);
        Assert.Equal("Plain text", b.Sources[0].Format.Name);
        Assert.True(await b.SyncAsync());
        Assert.Equal(["2024-01-01 00:00:02 ERROR b2", "2024-01-01 00:00:05 ERROR b5 a"], Messages(a));
        Assert.Equal(3, b.ViewCount);
    }

    [Fact]
    public async Task ARunningFilterInOneViewDoesNotRaceTheTail()
    {
        var lines = string.Concat(Enumerable.Range(0, 20_000).Select(i => $"2024-01-01 00:00:{i % 60:00} {(i % 7 == 0 ? "ERROR" : "INFO")} line {i}\n"));
        var p = TestFiles.Write("r.log", lines);
        using var a = new LogDocument("r");
        await a.AddSourceAsync(p, new SourceOptions());
        using var b = a.Duplicate();
        b.ScanBatch = 64;
        // Small batches keep b's filter running while the tail appends to the shared file.
        var filter = b.ApplyFilterAsync(Query("line"));
        File.AppendAllText(p, "2024-01-01 00:01:00 ERROR line tail\n");
        var refresh = a.RefreshAsync();
        await Task.WhenAll(filter, refresh);
        await b.SyncAsync();
        Assert.Equal(20_001, b.ViewCount);
        Assert.Equal("line tail", b.GetRows(20_000, 1)[0].Field("message"));
        await a.ApplyFilterAsync(Query("level:error"));
        Assert.Equal(20_000 / 7 + 2, a.ViewCount);
    }
}
