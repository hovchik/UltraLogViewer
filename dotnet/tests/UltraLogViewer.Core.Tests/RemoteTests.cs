using System.Diagnostics;

namespace UltraLogViewer.Core.Tests;

/// <summary>
/// Remote files over ssh: parsing, quoting and arguments, and sessions run against test/fixtures/fake-ssh (a stand-in
/// for OpenSSH that serves $FAKE_SSH_ROOT/&lt;host&gt; and can drop connections), which the Electron tests share.
/// </summary>
public sealed class RemoteTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ulv-ssh-").FullName;

    public void Dispose()
    {
        RemoteFile.CloseAll();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    // ------------------------------------------------------------ targets

    [Theory]
    [InlineData("ssh://web1/var/log/app.log", null, "web1", null, "/var/log/app.log")]
    [InlineData("ssh://deploy@web1:2222/var/log/app.log", "deploy", "web1", 2222, "/var/log/app.log")]
    [InlineData("ssh://web1/var/log/my%20app.log", null, "web1", null, "/var/log/my app.log")]
    [InlineData("deploy@web1.example.com:/var/log/app.log", "deploy", "web1.example.com", null, "/var/log/app.log")]
    [InlineData("web1:/var/log/app.log", null, "web1", null, "/var/log/app.log")]
    [InlineData("ssh://[::1]:22/x.log", null, "[::1]", 22, "/x.log")]
    public void ParsesUrlsAndScpTargets(string text, string? user, string host, int? port, string path)
    {
        Assert.True(RemoteTarget.LooksRemote(text));
        var t = RemoteTarget.ParseOrThrow(text);
        Assert.Equal(new RemoteTarget(user, host, port, path), t);
        Assert.Equal(t, RemoteTarget.ParseOrThrow(t.Url)); // the URL saved in recent files reads back the same
    }

    [Fact]
    public void NamesAndDestinations()
    {
        var t = RemoteTarget.ParseOrThrow("ssh://ops@web1:2200/var/log/a b.log");
        Assert.Equal("web1:/var/log/a b.log", t.Display);
        Assert.Equal("ops@web1", t.Destination);
        Assert.Equal("ssh://ops@web1:2200/var/log/a%20b.log", t.Url);
        Assert.Equal("::1", RemoteTarget.ParseOrThrow("ssh://[::1]/x").Destination);
    }

    [Theory]
    [InlineData(@"C:\logs\app.log")]
    [InlineData("C:/logs/app.log")]
    [InlineData("/var/log/app.log")]
    [InlineData("relative/app.log")]
    [InlineData("archive.zip::member.log")]
    public void LocalPathsAreNotRemote(string text) => Assert.Null(RemoteTarget.Parse(text));

    [Theory]
    [InlineData("ssh://-oProxyCommand=evil/x")]
    [InlineData("ssh://-oProxyCommand=evil@host/x")]
    [InlineData("ssh://host;rm/x")]
    [InlineData("ssh://a$(id)@host/x")] // a user name with shell characters (CVE-2023-51385 in older ssh)
    [InlineData("ssh://a%60id%60@host/x")]
    [InlineData("a'b@host:/x")]
    [InlineData("ssh://host/var/log/a\nrm -rf ~")]
    [InlineData("ssh://host/var/log/a%0Arm")]
    [InlineData("ssh://host/a%00b")]
    [InlineData("ssh://host:0/x")]
    [InlineData("ssh://host:99999/x")]
    [InlineData("ssh://host:abc/x")]
    [InlineData("ssh://host")]
    [InlineData("host:relative/path")]
    public void RejectsHostileOrBrokenTargets(string text) => Assert.Throws<FormatException>(() => RemoteTarget.ParseOrThrow(text));

    // ------------------------------------------------------------ quoting

    [Theory]
    [InlineData("/var/log/app.log")]
    [InlineData("/var/log/it's here.log")]
    [InlineData("/tmp/a b/$(reboot)/`id`/x;y|z&w>v<u")]
    [InlineData("/tmp/'''")]
    [InlineData("/tmp/\"q\" \\ *?[x]")]
    [InlineData("/tmp/$HOME ${x} ~")]
    public void QuotedWordsReachTheShellUnchanged(string word)
    {
        var q = RemoteShell.Quote(word);
        Assert.StartsWith("'", q);
        if (OperatingSystem.IsWindows()) return;
        // The real POSIX shell decides: printf prints the one word it receives.
        Assert.Equal(word, Sh("printf %s " + q));
    }

    [Theory]
    [InlineData("/var/log/a\nb")]
    [InlineData("/var/log/a\rb")]
    [InlineData("/var/log/a\0b")]
    [InlineData("/var/log/a\u001bb")]
    [InlineData("/var/log/a\u007fb")]
    public void ControlCharactersAreRefused(string word) => Assert.Throws<FormatException>(() => RemoteShell.Quote(word));

    [Fact]
    public void GlobsKeepOnlyStarAndQuestionMarkUnquoted()
    {
        Assert.Equal("'/var/log/app/'*'.log'", RemoteShell.QuoteGlob("/var/log/app/*.log"));
        Assert.Equal("'/l/a'?'b '\\''c'\\'''*", RemoteShell.QuoteGlob("/l/a?b 'c'*"));
        Assert.True(RemoteShell.HasGlob("/x/*.log"));
        Assert.False(RemoteShell.HasGlob("/x/[a].log"));
        if (OperatingSystem.IsWindows()) return;
        var dir = Directory.CreateDirectory(Path.Combine(_root, "g $x")).FullName;
        File.WriteAllText(Path.Combine(dir, "a.log"), "");
        File.WriteAllText(Path.Combine(dir, "b.log"), "");
        File.WriteAllText(Path.Combine(dir, "c.txt"), "");
        Assert.Equal($"{dir}/a.log\n{dir}/b.log\n", Sh(RemoteShell.GlobCommand(dir + "/*.log")));
    }

    [Fact]
    public void BuildsSshArgumentsWithoutAShell()
    {
        var t = RemoteTarget.ParseOrThrow("ssh://ops@web1:2200/var/log/it's.log");
        var args = Ssh.Args(t, RemoteShell.TailCommand(t.Path, 100));
        Assert.Equal(["-n", "-T", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10", "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=3", "-p", "2200", "--", "ops@web1",
            "tail -c +101 -F -- '/var/log/it'\\''s.log'"], args);
        Assert.Equal("stat -L -c %s -- '/x' 2>/dev/null || stat -L -f %z -- '/x' 2>/dev/null || wc -c < '/x'", RemoteShell.SizeCommand("/x"));
        Assert.Equal("ls -1Ap -- '/var/log'", RemoteShell.ListCommand("/var/log"));
        Assert.DoesNotContain("-p", Ssh.Args(RemoteTarget.ParseOrThrow("web1:/x"), "true"));
        // a target changed after parsing is checked again
        Assert.Throws<FormatException>(() => Ssh.Args(t with { Host = "-oProxyCommand=x" }, "true"));
    }

    [Fact]
    public void SshProgramComesFromTheSettingThenTheEnvironment()
    {
        var old = Environment.GetEnvironmentVariable(Ssh.EnvVar);
        try
        {
            Environment.SetEnvironmentVariable(Ssh.EnvVar, null);
            Assert.Equal("ssh", Ssh.Program(null));
            Environment.SetEnvironmentVariable(Ssh.EnvVar, "/opt/ssh");
            Assert.Equal("/opt/ssh", Ssh.Program(""));
            Assert.Equal("/usr/bin/ssh", Ssh.Program("/usr/bin/ssh"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(Ssh.EnvVar, old);
        }
    }

    [Fact]
    public void ExplainsLoginsThatNeedAPerson()
    {
        var t = RemoteTarget.ParseOrThrow("ops@web1:/x");
        Assert.Contains("ssh-agent", Ssh.Explain(t, 255, "ops@web1: Permission denied (publickey,password).\n"));
        Assert.Contains("host key", Ssh.Explain(t, 255, "Host key verification failed.\n"));
        Assert.Equal("web1: stat: cannot statx '/x': No such file or directory", Ssh.Explain(t, 1, "stat: cannot statx '/x': No such file or directory\n"));
        Assert.True(RemoteFile.IsRotation("tail: '/x' has been replaced;  following new file"));
        Assert.True(RemoteFile.IsRotation("tail: /x: file truncated"));
        Assert.False(RemoteFile.IsRotation("tail: '/x' has become inaccessible: No such file or directory"));
    }

    // --------------------------------------------------- with the fake ssh

    private static string? FakeSsh()
    {
        if (OperatingSystem.IsWindows()) return null;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "test", "fixtures", "fake-ssh", "ssh"))) dir = dir.Parent;
        return dir is null ? null : Path.Combine(dir.FullName, "test", "fixtures", "fake-ssh", "ssh");
    }

    private RemoteOptions Options() => new() { SshProgram = FakeSsh(), CacheDir = Path.Combine(_root, "cache"), RetryMs = 100, MaxRetryMs = 400 };

    /// <summary>A file on fake host <paramref name="host"/>, returned as its local path there.</summary>
    private string HostFile(string host, string remote, string text)
    {
        var p = Path.Combine(_root, host) + remote;
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
        return p;
    }

    private static async Task Until(Func<bool> cond, int timeoutMs = 10_000)
    {
        var sw = Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("Condition not met in time");
            await Task.Delay(20);
        }
    }

    private static async Task UntilAsync(Func<Task<bool>> cond, int timeoutMs = 10_000)
    {
        var sw = Stopwatch.StartNew();
        while (!await cond())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("Condition not met in time");
            await Task.Delay(30);
        }
    }

    private static string[] Messages(LogDocument d) => d.GetRows(0, 1000).Select(r => r.Field("message")).ToArray();

    private void UseFakeEnv() => Environment.SetEnvironmentVariable("FAKE_SSH_ROOT", _root);

    [Fact]
    public async Task CopiesARemoteFileAndFollowsItAcrossADropAndARotation()
    {
        if (FakeSsh() is null) return;
        UseFakeEnv();
        var file = HostFile("web1", "/var/log/app.log", "2024-05-01 10:00:00,000 [main] INFO  a.B - one\n2024-05-01 10:00:01,000 [main] INFO  a.B - two\n");
        var t = RemoteTarget.ParseOrThrow("ops@web1:/var/log/app.log");
        var r = await RemoteFile.OpenAsync(t, Options());
        Assert.Same(r, RemoteFile.Find(r.LocalPath));
        Assert.Equal("web1:/var/log/app.log", Archives.DisplayName(r.LocalPath));

        using var doc = new LogDocument("remote");
        await doc.AddSourceAsync(r.LocalPath, new SourceOptions());
        Assert.Equal(["one", "two"], Messages(doc));
        var info = doc.Info().Sources[0];
        Assert.Equal("web1:/var/log/app.log", info.DisplayName);
        Assert.NotNull(info.Remote);
        Assert.Equal("remote · live", info.Remote!.Text);

        // live growth
        File.AppendAllText(file, "2024-05-01 10:00:02,000 [main] WARN  a.B - three\n");
        await UntilAsync(async () => await doc.RefreshAsync() && doc.ViewCount == 3 || doc.ViewCount == 3);
        Assert.Equal("three", Messages(doc)[2]);

        // the connection drops: it reconnects and goes on from the bytes it has
        File.WriteAllText(Path.Combine(_root, "web1.drop"), "");
        await Until(() => r.Starts >= 2 && r.State == RemoteState.Live);
        File.AppendAllText(file, "2024-05-01 10:00:03,000 [main] INFO  a.B - four\n");
        await UntilAsync(async () => { await doc.RefreshAsync(); return doc.ViewCount == 4; });
        Assert.Equal(["one", "two", "three", "four"], Messages(doc));

        // rotation: a new, shorter file under the same name; the copy starts over
        File.Move(file, file + ".1");
        File.WriteAllText(file, "2024-05-01 11:00:00,000 [main] ERROR a.B - fresh\n");
        await UntilAsync(async () => { await doc.RefreshAsync(); return doc.ViewCount == 1; });
        Assert.Equal(["fresh"], Messages(doc));
        Assert.Equal(new FileInfo(file).Length, new FileInfo(r.LocalPath).Length);
    }

    [Fact]
    public async Task ReconnectsWithBackoffWhileTheHostIsDownAndSaysWhy()
    {
        if (FakeSsh() is null) return;
        UseFakeEnv();
        var file = HostFile("web2", "/l/app.log", "2024-05-01 10:00:00 INFO first\n");
        var r = await RemoteFile.OpenAsync(RemoteTarget.ParseOrThrow("ssh://web2/l/app.log"), Options());
        await Until(() => r.Received == new FileInfo(file).Length);
        File.WriteAllText(Path.Combine(_root, "web2.down"), "");
        File.WriteAllText(Path.Combine(_root, "web2.drop"), "");
        await Until(() => r.State == RemoteState.Reconnecting && r.Attempt >= 2);
        Assert.Contains("Connection refused", r.Status.Text);
        Assert.StartsWith("remote · reconnecting (", r.Status.Text);
        File.Delete(Path.Combine(_root, "web2.down"));
        File.AppendAllText(file, "2024-05-01 10:00:01 INFO second\n");
        await Until(() => r.State == RemoteState.Live && r.Received == new FileInfo(file).Length);
        Assert.Equal(File.ReadAllText(file), File.ReadAllText(r.LocalPath));
    }

    [Fact]
    public async Task FailsClearlyWhenTheFileIsMissingOrTheLoginNeedsAPassword()
    {
        if (FakeSsh() is null) return;
        UseFakeEnv();
        HostFile("web3", "/l/app.log", "x\n");
        var e = await Assert.ThrowsAsync<IOException>(() => RemoteFile.OpenAsync(RemoteTarget.ParseOrThrow("web3:/l/nope.log"), Options()));
        Assert.Contains("No such file", e.Message);
        File.WriteAllText(Path.Combine(_root, "web3.auth"), "");
        e = await Assert.ThrowsAsync<IOException>(() => RemoteFile.OpenAsync(RemoteTarget.ParseOrThrow("web3:/l/app.log"), Options()));
        Assert.Contains("ssh-agent", e.Message);
        e = await Assert.ThrowsAsync<IOException>(() => RemoteFile.OpenAsync(RemoteTarget.ParseOrThrow("nohost:/l/app.log"), Options()));
        Assert.Contains("Could not resolve hostname", e.Message);
        Assert.Equal(0, RemoteFile.RunningCount);
    }

    [Fact]
    public async Task MultiHostTailMergesHostsByTimeAndExpandsGlobs()
    {
        if (FakeSsh() is null) return;
        UseFakeEnv();
        HostFile("a1", "/var/log/app/x.log", "2024-05-01 10:00:00 INFO a1 x first\n2024-05-01 10:00:04 INFO a1 x third\n");
        HostFile("a1", "/var/log/app/y.log", "2024-05-01 10:00:05 INFO a1 y fourth\n");
        HostFile("a1", "/var/log/app/skip.txt", "2024-05-01 10:00:06 INFO not a log\n");
        HostFile("b2", "/var/log/app/x.log", "2024-05-01 10:00:02 INFO b2 x second\n");
        var opts = Options();
        var targets = new List<RemoteTarget>();
        foreach (var host in new[] { "a1", "b2" })
            targets.AddRange(await RemoteFiles.ExpandAsync(new RemoteTarget(null, host, null, "/var/log/app/*.log"), opts));
        Assert.Equal(["a1:/var/log/app/x.log", "a1:/var/log/app/y.log", "b2:/var/log/app/x.log"], targets.Select(t => t.Display));

        var sessions = await Task.WhenAll(targets.Select(t => RemoteFile.OpenAsync(t, opts)));
        using var doc = new LogDocument("Merged");
        var failed = await doc.AddSourcesAsync(sessions.Select(s => s.LocalPath).ToList(), _ => new SourceOptions());
        Assert.Empty(failed);
        Assert.Equal(["a1 x first", "b2 x second", "a1 x third", "a1 y fourth"], Messages(doc));
        Assert.Equal(["a1:/var/log/app/x.log", "b2:/var/log/app/x.log", "a1:/var/log/app/x.log", "a1:/var/log/app/y.log"],
            doc.GetRows(0, 10).Select(row => doc.Info().Sources[row.Source].DisplayName));

        // a pattern that matches nothing
        var e = await Assert.ThrowsAsync<IOException>(() => RemoteFiles.ExpandAsync(new RemoteTarget(null, "b2", null, "/var/log/app/*.gz"), opts));
        Assert.Contains("No such file", e.Message);
    }

    [Fact]
    public async Task ClosingKillsTheSshProcessesAndDeletesTheCopy()
    {
        if (FakeSsh() is null) return;
        UseFakeEnv();
        HostFile("c1", "/l/a.log", "2024-05-01 10:00:00 INFO a\n");
        HostFile("c2", "/l/a.log", "2024-05-01 10:00:00 INFO b\n");
        var opts = Options();
        var a = await RemoteFile.OpenAsync(RemoteTarget.ParseOrThrow("c1:/l/a.log"), opts);
        var b = await RemoteFile.OpenAsync(RemoteTarget.ParseOrThrow("c2:/l/a.log"), opts);
        Assert.Same(a, await RemoteFile.OpenAsync(RemoteTarget.ParseOrThrow("c1:/l/a.log"), opts)); // joins the running session
        await Until(() => RemoteFile.RunningCount == 2);
        var pids = Directory.GetFiles(Path.Combine(_root, "pids")).Select(f => int.Parse(Path.GetFileName(f))).ToList();
        Assert.Equal(2, pids.Count);

        RemoteFile.Prune([b.LocalPath]); // only b is still shown in a tab
        Assert.Equal(RemoteState.Closed, a.State);
        Assert.False(File.Exists(a.LocalPath));
        Assert.Null(RemoteFile.Find(a.LocalPath));
        Assert.Equal(1, RemoteFile.RunningCount);

        RemoteFile.CloseAll(); // the app quits
        Assert.Equal(0, RemoteFile.RunningCount);
        await Until(() => pids.All(Dead));
    }

    /// <summary>Gone, or a zombie waiting to be reaped.</summary>
    private static bool Dead(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            return stat[(stat.LastIndexOf(')') + 2)..].StartsWith('Z');
        }
        catch (IOException)
        {
            return true;
        }
    }

    private static string Sh(string command)
    {
        var psi = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, UseShellExecute = false };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(command);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return o;
    }
}
