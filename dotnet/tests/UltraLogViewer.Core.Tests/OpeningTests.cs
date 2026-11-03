using System.IO.Compression;
using System.Text;

namespace UltraLogViewer.Core.Tests;

/// <summary>The file-opening paths: index cache, time windows, compressed inputs, parallel merges, stdin and the command line.</summary>
public class OpeningTests
{
    private static string CacheDir() => Directory.CreateTempSubdirectory("ulv-cache-").FullName;

    /// <summary>One entry per second from 2024-05-01 00:00 local; every 7th has a three-line stack trace.</summary>
    private static string BigLog(int entries, int startSecond = 0)
    {
        var sb = new StringBuilder();
        var t0 = new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Local);
        for (var i = startSecond; i < startSecond + entries; i++)
        {
            sb.Append(t0.AddSeconds(i).ToString("yyyy-MM-dd HH:mm:ss,fff")).Append(i % 5 == 0 ? " [main] ERROR com.acme.Svc - failed " : " [main] INFO  com.acme.Svc - handled ").Append(i).Append('\n');
            if (i % 7 == 0) sb.Append("java.lang.IllegalStateException: at 2024-05-01 00:00:00\n\tat com.acme.Svc.run(Svc.java:10)\n\tat java.lang.Thread.run(Thread.java:833)\n");
        }
        return sb.ToString();
    }

    private static async Task<FileSource> Open(string path, SourceOptions? opts = null)
    {
        var s = new FileSource(0, path, opts);
        await s.OpenAsync();
        return s;
    }

    private static void SameIndex(FileSource a, FileSource b)
    {
        Assert.Equal(a.LineCount, b.LineCount);
        Assert.Equal(a.EntryCount, b.EntryCount);
        Assert.Equal(a.DataEnd, b.DataEnd);
        Assert.True(a.Levels.SequenceEqual(b.Levels));
        Assert.True(a.Times.SequenceEqual(b.Times));
        for (var e = 0; e < a.EntryCount; e += Math.Max(1, a.EntryCount / 50)) Assert.Equal(a.EntryLines(e), b.EntryLines(e));
        Assert.Equal(a.ReadEntries(a.EntryCount - 3, a.EntryCount), b.ReadEntries(b.EntryCount - 3, b.EntryCount));
    }

    // ------------------------------------------------------------ index cache

    [Fact]
    public async Task IndexCacheIsReusedWhenTheFileIsUnchanged()
    {
        var dir = CacheDir();
        var p = TestFiles.Write("cache.log", BigLog(3000));
        var opts = () => new SourceOptions { CacheDir = dir, CacheMinBytes = 0 };
        using var first = await Open(p, opts());
        Assert.Equal("", first.CacheState);
        await first.CacheSave!;
        Assert.Single(Directory.GetFiles(dir, "*.idx"));
        using var second = await Open(p, opts());
        Assert.Equal("hit", second.CacheState);
        Assert.Null(second.CacheSave);
        SameIndex(first, second);
        Assert.Equal(first.Stats().LevelCounts, second.Stats().LevelCounts);
        Assert.Equal("hit", second.Info().CacheState);
    }

    [Fact]
    public async Task IndexCacheIndexesOnlyTheNewBytesOfAGrownFile()
    {
        var dir = CacheDir();
        var text = BigLog(3000);
        // The last entry is multi-line and unterminated, so the cached tail must be re-read when the file grows.
        var p = TestFiles.Write("grow.log", text + "2024-05-02 00:00:00,000 [main] WARN  x - tail\n\tcontinued");
        using (var first = await Open(p, new SourceOptions { CacheDir = dir, CacheMinBytes = 0 })) await first.CacheSave!;
        File.AppendAllText(p, " more\n\tline two\n" + BigLog(500, 90_000));
        using var grown = await Open(p, new SourceOptions { CacheDir = dir, CacheMinBytes = 0 });
        Assert.Equal("grown", grown.CacheState);
        using var fresh = await Open(p);
        SameIndex(fresh, grown);
        Assert.Contains(grown.ReadEntries(0, grown.EntryCount), t => t == "2024-05-02 00:00:00,000 [main] WARN  x - tail\n\tcontinued more\n\tline two");
        await grown.CacheSave!; // the grown index replaces the old one
        using var again = await Open(p, new SourceOptions { CacheDir = dir, CacheMinBytes = 0 });
        Assert.Equal("hit", again.CacheState);
    }

    [Fact]
    public async Task IndexCacheIsIgnoredForAModifiedFileOrOtherSettings()
    {
        var dir = CacheDir();
        var text = BigLog(3000);
        var p = TestFiles.Write("mod.log", text);
        using (var first = await Open(p, new SourceOptions { CacheDir = dir, CacheMinBytes = 0 })) await first.CacheSave!;
        var mtime = File.GetLastWriteTimeUtc(p);

        // Same size and the same modification time, but different bytes at the start: the head hash catches it.
        File.WriteAllText(p, "X" + text[1..]);
        File.SetLastWriteTimeUtc(p, mtime);
        using (var s = await Open(p, new SourceOptions { CacheDir = dir, CacheMinBytes = 0 }))
        {
            Assert.Equal("", s.CacheState);
            Assert.StartsWith("X", s.ReadEntries(0, 1)[0]);
            await s.CacheSave!;
        }
        // Same bytes, newer modification time: rebuilt too.
        File.SetLastWriteTimeUtc(p, mtime.AddMinutes(5));
        using (var s = await Open(p, new SourceOptions { CacheDir = dir, CacheMinBytes = 0 })) Assert.Equal("", s.CacheState);
        File.SetLastWriteTimeUtc(p, mtime);
        // A shrunk file is a different file.
        File.WriteAllText(p, text[..(text.Length / 2)]);
        using (var s = await Open(p, new SourceOptions { CacheDir = dir, CacheMinBytes = 0 }))
        {
            Assert.Equal("", s.CacheState);
            await s.CacheSave!;
        }
        // A forced format or encoding needs its own index.
        using (var s = await Open(p, new SourceOptions { CacheDir = dir, CacheMinBytes = 0, Format = "plain" })) Assert.Equal("", s.CacheState);
        using (var s = await Open(p, new SourceOptions { CacheDir = dir, CacheMinBytes = 0, Encoding = "windows-1252" })) Assert.Equal("", s.CacheState);
        using (var s = await Open(p, new SourceOptions { CacheDir = dir, CacheMinBytes = 0, CustomFormats = [new CustomFormat("mine", @"^(?<time>\S+ \S+) (?<message>.*)")] }))
            Assert.Equal("", s.CacheState);
        // Small files are not worth a cache file.
        using (var s = await Open(TestFiles.Write("small.log", TestFiles.Log4j), new SourceOptions { CacheDir = dir })) Assert.Null(s.CacheSave);
    }

    [Fact]
    public async Task ACorruptIndexCacheFileFallsBackToAFullIndex()
    {
        var dir = CacheDir();
        var p = TestFiles.Write("corrupt.log", BigLog(3000));
        using var reference = await Open(p, new SourceOptions { CacheDir = dir, CacheMinBytes = 0 });
        await reference.CacheSave!;
        var file = Directory.GetFiles(dir, "*.idx").Single();
        var good = File.ReadAllBytes(file);
        var rnd = new Random(5);
        byte[][] variants =
        [
            good[..(good.Length / 2)],                     // truncated
            good[..^1],                                     // trailer cut
            [.. good, 1, 2, 3],                             // trailing garbage
            [],                                             // empty
            Encoding.ASCII.GetBytes("ULVIDX not really"),   // header only
        ];
        for (var i = 0; i < 40; i++)
        {
            var b = (byte[])good.Clone();
            b[rnd.Next(8, b.Length)] ^= (byte)(1 << rnd.Next(8)); // one flipped bit anywhere after the magic
            variants = [.. variants, b];
        }
        foreach (var v in variants)
        {
            File.WriteAllBytes(file, v);
            using var s = await Open(p, new SourceOptions { CacheDir = dir, CacheMinBytes = 0 });
            // Either rejected (full index) or, for a flip that leaves every check intact (e.g. in a hash, a detection
            // count or an equal-length string), still an index of this file.
            if (s.CacheState == "hit") Assert.Equal(reference.EntryCount, s.EntryCount);
            else SameIndex(reference, s);
            if (s.CacheSave is { } save) await save;
        }
    }

    [Fact]
    public async Task IndexCacheStaysUnderItsSizeCap()
    {
        var dir = CacheDir();
        var paths = Enumerable.Range(0, 4).Select(i => TestFiles.Write($"cap{i}.log", BigLog(3000, i * 10_000))).ToArray();
        long one;
        using (var s = await Open(paths[0], new SourceOptions { CacheDir = dir, CacheMinBytes = 0 }))
        {
            await s.CacheSave!;
            one = IndexCache.Size(dir);
        }
        File.SetLastWriteTimeUtc(Directory.GetFiles(dir).Single(), DateTime.UtcNow.AddHours(-1));
        foreach (var p in paths.Skip(1))
        {
            using var s = await Open(p, new SourceOptions { CacheDir = dir, CacheMinBytes = 0, CacheMaxBytes = one * 2 + one / 2 });
            await s.CacheSave!;
        }
        Assert.True(IndexCache.Size(dir) <= one * 2 + one / 2);
        Assert.Equal(2, Directory.GetFiles(dir, "*.idx").Length);
        Assert.False(File.Exists(IndexCache.FileFor(dir, paths[0]))); // the least recently used went first
        Assert.True(File.Exists(IndexCache.FileFor(dir, paths[3])));
        Assert.True(IndexCache.Clear(dir) > 0);
        Assert.Equal(0, IndexCache.Size(dir));
    }

    // ------------------------------------------------------------ time window

    [Theory]
    [InlineData(4096)]
    [InlineData(4 * 1024 * 1024)]
    public async Task OpensOnlyATimeWindowWithoutCuttingMultiLineEntries(int chunk)
    {
        var p = TestFiles.Write("window.log", BigLog(40_000)); // ~2.6 MB, so the binary search really probes
        using var full = await Open(p);
        var t0 = TestFiles.Local(2024, 5, 1, 0, 0, 0);
        // 00:00:00 + 7000 s is an entry with a stack trace (7000 % 7 == 0) right at the start boundary.
        long since = t0 + 7000_000, until = t0 + 21_000_000 + 500;
        using var w = await Open(p, new SourceOptions { ChunkSize = chunk, Window = new OpenWindow(since, until) });
        var expected = Enumerable.Range(0, full.EntryCount).Where(e => full.TimeOf(e) >= since && full.TimeOf(e) <= until).ToArray();
        Assert.Equal(expected.Length, w.EntryCount);
        Assert.Equal(full.ReadEntries(expected[0], expected[0] + 1), w.ReadEntries(0, 1));
        Assert.Contains("\tat java.lang.Thread.run", w.ReadEntries(0, 1)[0]);
        Assert.Equal(full.ReadEntries(expected[^1], expected[^1] + 1), w.ReadEntries(w.EntryCount - 1, w.EntryCount));
        Assert.True(w.Partial);
        Assert.False(w.Live); // the window ends before the end of the file
        Assert.Equal((since, until), (w.WindowFrom, w.WindowTo));
        var info = w.Info();
        Assert.True(info.Partial);
        Assert.Equal(since, info.WindowFrom);
    }

    [Fact]
    public async Task TimeWindowEdgesAndLast()
    {
        var p = TestFiles.Write("edges.log", BigLog(40_000));
        var t0 = TestFiles.Local(2024, 5, 1, 0, 0, 0);
        using (var all = await Open(p, new SourceOptions { Window = new OpenWindow(t0 - 1000, t0 + 100_000_000) }))
        {
            Assert.False(all.Partial);
            Assert.Equal(40_000, all.EntryCount);
        }
        using (var none = await Open(p, new SourceOptions { Window = new OpenWindow(t0 + 100_000_000) }))
        {
            Assert.Equal(0, none.EntryCount);
            Assert.True(none.Live); // open-ended: new lines will still be tailed
        }
        using var last = await Open(p, new SourceOptions { Window = new OpenWindow(Last: 600_000) });
        Assert.Equal(601, last.EntryCount); // the newest entry and the 600 s before it
        Assert.True(last.Live && last.Partial);
        Assert.Equal(t0 + 39_999_000 - 600_000, last.WindowFrom);

        // New lines after an open-ended window are tailed as usual.
        File.AppendAllText(p, BigLog(3, 40_000));
        var r = await last.RefreshAsync();
        Assert.True(r.Changed);
        Assert.Equal(604, last.EntryCount);
    }

    // ------------------------------------------------------------ compressed

    private static string Bz2Text()
    {
        var sb = new StringBuilder();
        string[] lv = ["INFO", "WARN", "ERROR"];
        for (var i = 0; i < 3600; i++)
        {
            sb.Append($"2024-05-01 10:{i / 60 % 60:00}:{i % 60:00},000 {lv[i % 3]} app - message {i % 10}\n");
            if (i % 10 == 0) sb.Append($"\tat X.y(X.java:{i % 5})\n");
        }
        return sb.ToString();
    }

    /// <summary>bzip2 -1 of <see cref="Bz2Text"/> (two blocks), made with the reference implementation.</summary>
    private const string Bz2A =
        "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" +        "";

    /// <summary>A second stream (as pbzip2 or cat-ing two .bz2 files produces).</summary>
    private const string Bz2B = "QlpoOTFBWSZTWWIkklwAAAxdgAAQQAZ2EAEhrgPcACAAIinqfqmnpMyj0RmkKAAaBkyL4RPJ3E1UbMpox8ElGEjIPJ39B/ZjERka6Ln8XckU4UJBiJJJcA==";
    private const string Bz2BText = "2024-05-01 11:00:00,000 INFO app - second stream\n";

    [Fact]
    public void Bzip2DecodesMultiBlockAndConcatenatedStreams()
    {
        var data = Convert.FromBase64String(Bz2A).Concat(Convert.FromBase64String(Bz2B)).ToArray();
        using var bz = new Bzip2Stream(new MemoryStream(data));
        var output = new MemoryStream();
        var buf = new byte[777]; // odd reads cross run-length and block boundaries
        int n;
        while ((n = bz.Read(buf, 0, buf.Length)) > 0) output.Write(buf, 0, n);
        Assert.Equal(Bz2Text() + Bz2BText, Encoding.UTF8.GetString(output.ToArray()));
        Assert.Equal(data.Length, bz.CompressedPosition);
    }

    [Fact]
    public void Bzip2RejectsCorruptData()
    {
        var data = Convert.FromBase64String(Bz2A);
        data[data.Length / 2] ^= 0x10;
        Assert.Throws<InvalidDataException>(() =>
        {
            using var bz = new Bzip2Stream(new MemoryStream(data));
            bz.CopyTo(Stream.Null);
        });
        Assert.Throws<InvalidDataException>(() => new Bzip2Stream(new MemoryStream("plain text"u8.ToArray())));
        Assert.Throws<InvalidDataException>(() =>
        {
            using var bz = new Bzip2Stream(new MemoryStream(data[..(data.Length - 20)]));
            bz.CopyTo(Stream.Null);
        });
    }

    [Fact]
    public async Task OpensBzip2AndZipFiles()
    {
        var text = Bz2Text() + Bz2BText;
        using var plain = await Open(TestFiles.Write("ref.log", text));
        using (var bz = await Open(TestFiles.Write("a.log.bz2", [.. Convert.FromBase64String(Bz2A), .. Convert.FromBase64String(Bz2B)])))
        {
            Assert.Equal(("bzip2", false), (bz.Compression, bz.Live));
            SameIndex(plain, bz);
        }
        var zipPath = TestFiles.Write("bundle.zip", Array.Empty<byte>());
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Update))
        {
            using (var w = new StreamWriter(zip.CreateEntry("logs/app.log").Open())) w.Write(text);
            using (var w = new StreamWriter(zip.CreateEntry("logs/other.txt").Open())) w.Write(TestFiles.Log4j);
            zip.CreateEntry("logs/");
            using (var w = zip.CreateEntry("logs/screenshot.png").Open()) w.Write([1, 2, 3]);
            zip.CreateEntry("empty.log");
        }
        var members = Archives.Expand([zipPath]);
        Assert.Equal([Archives.MemberPath(zipPath, "logs/app.log"), Archives.MemberPath(zipPath, "logs/other.txt")], members);
        using (var m = await Open(members[0]))
        {
            Assert.Equal("zip", m.Compression);
            Assert.Equal("app.log", m.Info().DisplayName);
            SameIndex(plain, m);
        }
        // A zip opened directly reads its first text member.
        using (var direct = await Open(zipPath)) SameIndex(plain, direct);

        // A zip with several logs opens as one merged view.
        using var doc = new LogDocument("bundle");
        var failed = await doc.AddSourcesAsync(members, _ => new SourceOptions());
        Assert.Empty(failed);
        Assert.Equal(plain.EntryCount + 6, doc.BaseCount);
        Assert.Equal(["app.log", "other.txt"], doc.Info().Sources.Select(s => s.DisplayName));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            var empty = TestFiles.Write("empty.zip", Array.Empty<byte>());
            using (var z = ZipFile.Open(empty, ZipArchiveMode.Update)) z.CreateEntry("x.png");
            Archives.Expand([empty]);
            await Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData(4096)]
    [InlineData(4 * 1024 * 1024)]
    public async Task IndexesGzipWhileDecompressing(int chunk)
    {
        // Big enough that the index runs while the data is still arriving, with an unterminated last line.
        var text = BigLog(60_000) + "2024-05-02 00:00:00,000 [main] INFO  x - no newline";
        var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, true)) gz.Write(Encoding.UTF8.GetBytes(text));
        using var plain = await Open(TestFiles.Write("ref.log", text));
        var progress = new List<double>();
        var g = new FileSource(0, TestFiles.Write("stream.log.gz", ms.ToArray()), new SourceOptions { ChunkSize = chunk });
        using (g)
        {
            await g.OpenAsync(new SyncProgress(f => { lock (progress) progress.Add(f); }));
            Assert.Equal("gzip", g.Compression);
            SameIndex(plain, g);
            Assert.Equal("2024-05-02 00:00:00,000 [main] INFO  x - no newline", g.ReadEntries(g.EntryCount - 1, g.EntryCount)[0]);
            Assert.True(progress.Count > 1);
        }
        // The window and the gzip paths combine: the window is found once decompression is done.
        var t0 = TestFiles.Local(2024, 5, 1, 0, 0, 0);
        using var w = await Open(TestFiles.Write("w.log.gz", ms.ToArray()), new SourceOptions { Window = new OpenWindow(t0 + 1000_000, t0 + 1999_000) });
        Assert.Equal(1000, w.EntryCount);
    }

    [Fact]
    public async Task CorruptGzipFailsToOpen()
    {
        var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, true)) gz.Write(Encoding.UTF8.GetBytes(BigLog(20_000)));
        var bytes = ms.ToArray();
        for (var i = 0; i < 64; i++) bytes[bytes.Length / 3 + i] = 0xff;
        using var s = new FileSource(0, TestFiles.Write("bad.log.gz", bytes));
        await Assert.ThrowsAsync<InvalidDataException>(() => s.OpenAsync());
    }

    [Fact]
    public async Task DecompressionStopsBeforeFillingTheDisk()
    {
        // A decompression bomb (or any compressed file too big for the temp drive) fails to open instead of filling it.
        var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, true)) gz.Write(Encoding.UTF8.GetBytes(BigLog(2000)));
        var path = TestFiles.Write("bomb.log.gz", ms.ToArray());
        var temps = Directory.GetFiles(Path.GetTempPath(), "ultralogviewer-*bomb.log*").Length;
        using var doc = new LogDocument("bomb");
        var failed = await doc.AddSourcesAsync([path], _ => new SourceOptions { MinFreeDiskBytes = long.MaxValue });
        Assert.Contains("Not enough free disk space", Assert.Single(failed).Error.Message);
        Assert.Empty(doc.Sources);
        Assert.Equal(temps, Directory.GetFiles(Path.GetTempPath(), "ultralogviewer-*bomb.log*").Length); // the temp file is gone
        using var ok = await Open(path);
        Assert.Equal(2000, ok.EntryCount);
    }

    [Fact]
    public void APlainFileWithTwoColonsInItsNameIsNotAZipMember()
    {
        if (OperatingSystem.IsWindows()) return; // ':' cannot be in a file name there
        var dir = Directory.CreateTempSubdirectory("ulv-colons-").FullName;
        var prefix = Path.Combine(dir, "a");
        File.WriteAllText(prefix, "not a zip");
        var file = prefix + "::b.log";
        File.WriteAllText(file, "2024-05-01 10:00:00,000 [main] INFO  x - hi\n");
        Assert.Null(Archives.SplitMember(file));
        Assert.Equal("a::b.log", Archives.DisplayName(file));
        Assert.Null(Archives.OpenDecompressed(file, out _, out _, out _));
    }

    // ------------------------------------------------------------ merges

    [Fact]
    public async Task IndexesTheFilesOfAMergeInParallel()
    {
        var a = TestFiles.Write("a.log", BigLog(20_000));
        var b = TestFiles.Write("b.log", BigLog(20_000, 5));
        var missing = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.log");
        using var doc = new LogDocument("merged");
        var seen = new List<double>();
        var failed = await doc.AddSourcesAsync([a, missing, b], _ => new SourceOptions { ChunkSize = 64 * 1024 }, new SyncProgress(f => { lock (seen) seen.Add(f); }));
        Assert.Equal(missing, Assert.Single(failed).Path);
        Assert.Equal([0, 1], doc.Sources.Select(s => s.Id));
        Assert.Equal([a, b], doc.Sources.Select(s => s.Path));
        Assert.Equal(40_000, doc.BaseCount);
        Assert.All(doc.Info().Sources, s => Assert.Equal((false, 1.0), (s.Indexing, s.Progress)));
        // Merged by time: the two files interleave.
        var rows = doc.GetRows(0, 4);
        Assert.Equal([0, 0, 0, 0], rows.Select(r => r.Source).Take(4));
        rows = doc.GetRows(10, 2);
        Assert.Equal(2, rows.Select(r => r.Source).Distinct().Count());
        Assert.True(seen.Count > 2);
    }

    // ------------------------------------------------------------ stdin, CLI

    [Fact]
    public async Task CapturesStdinIntoAGrowingFile()
    {
        var pipe = new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.Out);
        var reader = new System.IO.Pipes.AnonymousPipeClientStream(System.IO.Pipes.PipeDirection.In, pipe.ClientSafePipeHandle);
        var dir = Directory.CreateTempSubdirectory("ulv-stdin-").FullName;
        pipe.Write(Encoding.UTF8.GetBytes(TestFiles.Log4j));
        pipe.Flush();
        using var cap = await StdinCapture.StartAsync(reader, dir, TimeSpan.FromSeconds(5));
        Assert.StartsWith(dir, cap.Path);
        using var s = await Open(cap.Path);
        Assert.Equal("timestamped", s.Format.Id);
        Assert.Equal(6, s.EntryCount);
        Assert.True(s.Live);
        pipe.Write("2024-05-01 10:00:06,007 [main] INFO  com.acme.App - Later\n"u8);
        pipe.Flush();
        pipe.Dispose();
        await cap.Completion;
        Assert.True((await s.RefreshAsync()).Changed);
        Assert.Equal(7, s.EntryCount);
        s.Dispose();
        cap.Dispose();
        Assert.False(File.Exists(cap.Path));
    }

    [Fact]
    public async Task StdinCaptureNeverWritesThroughAPlantedLink()
    {
        // The temp folder can be shared: the capture file gets a name nobody can guess and is created new, so a link
        // planted under the old predictable name (stdin-<time>-<pid>.log) can't redirect the input into another file.
        if (OperatingSystem.IsWindows()) return;
        var dir = Directory.CreateTempSubdirectory("ulv-stdin-").FullName;
        var victim = Path.Combine(dir, "victim.txt");
        File.WriteAllText(victim, "keep me");
        var now = DateTime.Now;
        for (var s = 0; s < 5; s++)
            File.CreateSymbolicLink(Path.Combine(dir, $"stdin-{now.AddSeconds(s):yyyyMMdd-HHmmss}-{Environment.ProcessId}.log"), victim);
        using var cap = await StdinCapture.StartAsync(new MemoryStream("2024-05-01 10:00:00,000 [main] INFO  x - piped\n"u8.ToArray()), dir, TimeSpan.FromSeconds(2));
        await cap.Completion;
        Assert.Equal("keep me", File.ReadAllText(victim));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(cap.Path));
        Assert.Contains("piped", File.ReadAllText(cap.Path));
    }

    [Fact]
    public void ParsesTheCommandLine()
    {
        var now = TestFiles.Local(2024, 5, 1, 12, 0, 0);
        var f = TestFiles.Write("cli.log", "x");
        var c = CommandLine.Parse([f, "--since", "-1h", "-", "--until=2024-05-01T11:30", "--unknown"], now);
        Assert.Equal([f], c.Files);
        Assert.True(c.Stdin);
        Assert.Equal(new OpenWindow(now - 3_600_000, TestFiles.Local(2024, 5, 1, 11, 30, 0)), c.Window);
        Assert.Empty(c.Errors);
        c = CommandLine.Parse(["--last", "15m"], now);
        Assert.Equal(new OpenWindow(null, null, 900_000), c.Window);
        c = CommandLine.Parse(["--since", "yesterday-ish", "--last"], now);
        Assert.Equal(2, c.Errors.Count);
        Assert.Null(CommandLine.Parse([f], now).Window);
    }

    [Theory]
    [InlineData("90s", 90_000L)]
    [InlineData("15m", 900_000L)]
    [InlineData("1h30m", 5_400_000L)]
    [InlineData("2d", 172_800_000L)]
    [InlineData("1.5h", 5_400_000L)]
    [InlineData("1w", 604_800_000L)]
    [InlineData("h", null)]
    [InlineData("10", null)]
    [InlineData("-1h", null)]
    public void ParsesDurations(string s, long? ms) => Assert.Equal(ms, TimeWindow.ParseDuration(s));

    [Fact]
    public void ParsesPointsInTime()
    {
        var now = TestFiles.Local(2024, 5, 1, 12, 0, 0);
        Assert.Equal(now, TimeWindow.ParsePoint("now", now));
        Assert.Equal(now - 1_800_000, TimeWindow.ParsePoint("-30m", now));
        Assert.Equal(TestFiles.Local(2024, 4, 30, 0, 0, 0), TimeWindow.ParsePoint("2024-04-30", now));
        Assert.Equal(TestFiles.Local(2024, 4, 30, 10, 5, 0), TimeWindow.ParsePoint("2024-04-30 10:05", now));
        Assert.Equal(TestFiles.Local(2024, 4, 30, 10, 5, 7), TimeWindow.ParsePoint("2024-04-30T10:05:07", now));
        Assert.Equal(TestFiles.Local(2024, 5, 1, 9, 15, 0), TimeWindow.ParsePoint("9:15", now));
        Assert.Equal(Timestamps.Build(2024, 4, 30, 10, 0, 0, 0, 0), TimeWindow.ParsePoint("2024-04-30T10:00:00Z", now));
        Assert.Null(TimeWindow.ParsePoint("soon", now));
        Assert.Null(TimeWindow.ParsePoint("25:00", now));
        Assert.Equal("2024-05-01 10:00–11:00", TimeWindow.Describe(TestFiles.Local(2024, 5, 1, 10, 0, 0), TestFiles.Local(2024, 5, 1, 11, 0, 0)));
        Assert.Equal("since 2024-05-01 10:00", TimeWindow.Describe(TestFiles.Local(2024, 5, 1, 10, 0, 0), null));
    }
}
