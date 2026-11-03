using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Controls;
using Avalonia.Threading;
using UltraLogViewer.Core;

namespace UltraLogViewer.Desktop.Tests;

/// <summary>Remote files and multi-host tail in the window, over test/fixtures/fake-ssh (see RemoteTests in the core tests).</summary>
public sealed class RemoteUiTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ulv-ui-ssh-").FullName;
    private readonly string _root;

    public RemoteUiTests()
    {
        _root = Directory.CreateDirectory(Path.Combine(_dir, "hosts")).FullName;
        Environment.SetEnvironmentVariable("FAKE_SSH_ROOT", _root);
    }

    public void Dispose()
    {
        RemoteFile.CloseAll();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static string? FakeSsh()
    {
        if (OperatingSystem.IsWindows()) return null;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "test", "fixtures", "fake-ssh", "ssh"))) dir = dir.Parent;
        return dir is null ? null : Path.Combine(dir.FullName, "test", "fixtures", "fake-ssh", "ssh");
    }

    private string HostFile(string host, string remote, string text)
    {
        var p = Path.Combine(_root, host) + remote;
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
        return p;
    }

    private (MainWindow Window, AppSettings Settings) NewWindow()
    {
        var settings = new AppSettings { FilePath = Path.Combine(_dir, "settings.json"), SshProgram = FakeSsh()! };
        var w = new MainWindow(settings, []) { Width = 1400, Height = 860 };
        w.Show();
        return (w, settings);
    }

    private static async Task Until(Func<bool> cond, int timeoutMs = 10_000)
    {
        var start = Environment.TickCount64;
        while (!cond())
        {
            if (Environment.TickCount64 - start > timeoutMs) throw new TimeoutException("Condition not met in time");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(15);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static string Line(int s, string host, string msg) => $"2024-05-01 10:00:{s:00}.000 [main] INFO  com.acme.{host} - {msg}\n";

    [AvaloniaFact]
    public async Task Opens_a_remote_file_follows_it_and_kills_ssh_when_the_tab_closes()
    {
        if (FakeSsh() is null) return;
        var file = HostFile("web1", "/var/log/app.log", Line(0, "Web", "one") + Line(1, "Web", "two"));
        var (w, settings) = NewWindow();
        Assert.Contains(w.PaletteActions(), a => a.Label == "File › Open remote file…");
        Assert.Contains(w.PaletteActions(), a => a.Label == "File › Multi-host tail…");

        await w.OpenPaths(["deploy@web1:/var/log/app.log"], false);
        var tab = w.ActiveTab!;
        await Until(() => tab.Ready);
        Assert.Equal("web1:/var/log/app.log", tab.Title);
        Assert.Equal(2, tab.Info.Total);
        Assert.Equal("ssh://deploy@web1/var/log/app.log", settings.Recent[0]);
        await Until(() => Sidebar.SourcesText(tab).Contains("remote · live"));
        Assert.StartsWith("web1:/var/log/app.log\nremote · live\n", Sidebar.SourcesText(tab));

        File.AppendAllText(file, Line(2, "Web", "three"));
        await Until(() => tab.Info.Total == 3);

        // reopening the same remote file shows its tab instead of a second copy
        await w.OpenPaths(["ssh://deploy@web1/var/log/app.log"], false);
        Assert.Same(tab, w.ActiveTab);

        // a workspace keeps the URL and connects again
        var ws = w.BuildWorkspace(Path.Combine(_dir, "w.ulvw"));
        Assert.Equal(["ssh://deploy@web1/var/log/app.log"], ws.Tabs[0].Paths);

        Assert.Equal(1, RemoteFile.RunningCount);
        w.CloseTab(tab);
        Assert.Equal(0, RemoteFile.RunningCount);
        Assert.Empty(RemoteFile.All);

        File.WriteAllText(Path.Combine(_dir, "w.ulvw"), Workspace.Serialize(ws));
        await w.OpenWorkspaceAsync(Path.Combine(_dir, "w.ulvw"));
        await Until(() => w.ActiveTab is { Ready: true, Info.Total: 3 });
        Assert.Equal("web1:/var/log/app.log", w.ActiveTab!.Title);
        w.Close();
        Assert.Equal(0, RemoteFile.RunningCount); // quitting kills every ssh
    }

    [AvaloniaFact]
    public async Task Multi_host_tail_merges_hosts_by_time_and_reports_failures()
    {
        if (FakeSsh() is null) return;
        HostFile("a1", "/var/log/app/x.log", Line(0, "A", "a first") + Line(4, "A", "a third"));
        HostFile("b2", "/var/log/app/x.log", Line(2, "B", "b second"));
        HostFile("b2", "/var/log/app/y.log", Line(6, "B", "b fourth"));
        var (w, _) = NewWindow();
        await w.OpenMultiHost("a1, b2 nohost", "/var/log/app/*.log");
        var tab = w.ActiveTab!;
        await Until(() => tab.Ready);
        Assert.Contains("nohost", w.ToastForTest);
        Assert.Equal("a1:/var/log/app/x.log + 2", tab.Title);
        Assert.Equal(["a1:/var/log/app/x.log", "b2:/var/log/app/x.log", "b2:/var/log/app/y.log"], tab.Info.Sources.Select(s => s.DisplayName));
        Assert.Equal(["a first", "b second", "a third", "b fourth"], tab.Doc.GetRows(0, 10).Select(r => r.Field("message")));
        // the source column names the host
        Assert.Equal("b2:/var/log/app/x.log", tab.Info.Sources[tab.Doc.GetRows(1, 1)[0].Source].DisplayName);
        await Until(() => Sidebar.SourcesText(tab).Split("remote · live").Length == 4);
        Assert.Equal(3, RemoteFile.RunningCount);

        // a host drops: its source says so and it comes back
        File.WriteAllText(Path.Combine(_root, "b2.down"), "");
        File.WriteAllText(Path.Combine(_root, "b2.drop"), "");
        await Until(() => Sidebar.SourcesText(tab).Contains("remote · reconnecting"));
        File.Delete(Path.Combine(_root, "b2.down"));
        await Until(() => Sidebar.SourcesText(tab).Split("remote · live").Length == 4, 15_000);

        w.CloseAllTabsForTest();
        Assert.Equal(0, RemoteFile.RunningCount);
        w.Close();
    }
}
