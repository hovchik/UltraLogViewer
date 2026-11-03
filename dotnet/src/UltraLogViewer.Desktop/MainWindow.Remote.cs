using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using UltraLogViewer.Core;

namespace UltraLogViewer.Desktop;

/// <summary>
/// Remote files over ssh: "Open remote file…" (with a simple remote folder browser) and "Multi-host tail…", which opens
/// one path or pattern on several hosts merged by time. A remote file is copied into a local file that the normal
/// indexing and live tail read; see <see cref="RemoteFile"/>. Tabs keep the local copies' paths, while recent files and
/// workspaces keep ssh:// URLs, which open the remote file again.
/// </summary>
public sealed partial class MainWindow
{
    private RemoteOptions RemoteOptions => new()
    {
        SshProgram = _settings.SshProgram,
        CacheDir = Path.Combine(Path.GetDirectoryName(_settings.FilePath ?? AppSettings.DefaultPath)!, "remote-cache"),
    };

    /// <summary>The ssh:// URL for a tab's path when it is the local copy of a remote file, else the path itself.</summary>
    internal static string SourceUrl(string path) => RemoteFile.Find(path)?.Target.Url ?? path;

    /// <summary>"host:path" for the local copy of a remote file, else the file name.</summary>
    internal static string ShortName(string path) => RemoteFile.DisplayNameOf(path) ?? Path.GetFileName(path);

    /// <summary>
    /// Replaces remote paths (ssh:// URLs, user@host:/path, patterns with * or ?) by local copies that are being kept
    /// up to date, connecting to all hosts at once. Failures are reported; null when nothing could be opened.
    /// </summary>
    private async Task<string[]?> LocalCopiesAsync(string[] paths)
    {
        if (!paths.Any(RemoteTarget.LooksRemote)) return paths;
        var hosts = paths.Where(RemoteTarget.LooksRemote).Select(p => RemoteTarget.Parse(p)?.Host).OfType<string>().Distinct().ToList();
        if (hosts.Count > 0) Toast(hosts.Count == 1 ? $"Connecting to {hosts[0]}…" : $"Connecting to {hosts.Count} hosts…");
        var opts = RemoteOptions;
        var errors = new List<string>();
        var slots = await Task.WhenAll(paths.Select(async p =>
        {
            if (!RemoteTarget.LooksRemote(p)) return [p];
            try
            {
                var targets = await RemoteFiles.ExpandAsync(RemoteTarget.ParseOrThrow(p), opts);
                var opened = await Task.WhenAll(targets.Select(async t =>
                {
                    try
                    {
                        return (await RemoteFile.OpenAsync(t, opts)).LocalPath;
                    }
                    catch (IOException e)
                    {
                        lock (errors) errors.Add(e.Message);
                        return null;
                    }
                }));
                return opened.OfType<string>().ToArray();
            }
            catch (Exception e) when (e is IOException or FormatException)
            {
                lock (errors) errors.Add(e is FormatException ? $"{p}: {e.Message}" : e.Message);
                return Array.Empty<string>();
            }
        }));
        if (errors.Count > 0) Toast(string.Join("\n", errors.Take(4)) + (errors.Count > 4 ? $"\n… and {errors.Count - 4} more" : ""), true);
        var local = slots.SelectMany(s => s).Distinct().ToArray();
        return local.Length == 0 ? null : local;
    }

    /// <summary>Ends the ssh sessions that no open tab shows any more (after a tab is closed or replaced).</summary>
    private void PruneRemote() => RemoteFile.Prune(AllTabs().SelectMany(t => t.Paths.Concat(t.Info.Sources.Select(s => s.Path))));

    /// <summary>The Sources list shows the connection state, which changes without new data.</summary>
    private void TickRemote()
    {
        if (_tickCount % 4 == 0 && _active is { } tab && tab.Info.Sources.Any(s => s.Remote is not null)) _sidebar.UpdateCounts(tab);
    }

    // ============================================================ dialogs

    /// <summary>"Open remote file…": a URL or user@host:/path, with a Browse button that lists the remote folder.</summary>
    private async Task PickRemoteFile()
    {
        var url = await RemoteDialog(this, RemoteOptions.SshProgram, _settings.Recent.FirstOrDefault(RemoteTarget.LooksRemote) ?? "");
        if (url is not null) await OpenPaths([url], false);
    }

    /// <summary>"Multi-host tail…": the same path or pattern on several hosts, merged into one timeline.</summary>
    private async Task PickMultiHost()
    {
        var hosts = new TextBox { Watermark = "web1, web2, deploy@web3:2222", Text = _settings.MultiHostHosts, MinWidth = 380 };
        var path = new TextBox { Watermark = "/var/log/app/*.log", Text = _settings.MultiHostPath, FontFamily = Fonts.Mono };
        var error = new TextBlock { Foreground = Brushes.IndianRed, FontSize = 12, IsVisible = false, TextWrapping = TextWrapping.Wrap };
        var ok = new Button { Content = "Open", IsDefault = true, Classes = { "accent" } };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var panel = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "Hosts (as in your ssh config; separate with commas or spaces):", TextWrapping = TextWrapping.Wrap },
                hosts,
                new TextBlock { Text = "File on each host (* and ? match several files):", Margin = new Thickness(0, 6, 0, 0) },
                path,
                new TextBlock { Classes = { "hint" }, Text = "Each file is copied and followed live over ssh, merged by time and labelled by host.", TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Opacity = 0.7 },
                error,
                Dialogs.Buttons(ok, cancel),
            },
        };
        var w = Dialogs.Shell("Multi-host tail", panel, 480);
        ok.Click += (_, _) =>
        {
            try
            {
                RemoteFiles.ForHosts(hosts.Text ?? "", path.Text?.Trim() ?? "");
                w.Close(true);
            }
            catch (FormatException e)
            {
                error.Text = e.Message;
                error.IsVisible = true;
            }
        };
        cancel.Click += (_, _) => w.Close(false);
        w.Opened += (_, _) => hosts.Focus();
        if (!await w.ShowDialog<bool>(this)) return;
        _settings.MultiHostHosts = hosts.Text?.Trim() ?? "";
        _settings.MultiHostPath = path.Text?.Trim() ?? "";
        await OpenMultiHost(_settings.MultiHostHosts, _settings.MultiHostPath);
    }

    /// <summary>Opens <paramref name="path"/> on every host into one merged tab.</summary>
    public async Task OpenMultiHost(string hosts, string path)
    {
        List<RemoteTarget> targets;
        try
        {
            targets = RemoteFiles.ForHosts(hosts, path);
        }
        catch (FormatException e)
        {
            Toast(e.Message, true);
            return;
        }
        await OpenPaths(targets.Select(t => t.Url).ToArray(), true);
    }

    internal static async Task<string?> RemoteDialog(Window owner, string? sshProgram, string initial)
    {
        var box = new TextBox { Text = initial, Watermark = "ssh://user@host/var/log/app.log  or  user@host:/var/log/app.log", MinWidth = 440, FontFamily = Fonts.Mono };
        var list = new ListBox { MaxHeight = 260, FontSize = 12.5, IsVisible = false };
        var error = new TextBlock { Foreground = Brushes.IndianRed, FontSize = 12, IsVisible = false, TextWrapping = TextWrapping.Wrap };
        var browse = new Button { Content = "Browse" };
        var ok = new Button { Content = "Open", IsDefault = true, Classes = { "accent" } };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var row = new DockPanel { Children = { browse, box } };
        DockPanel.SetDock(browse, Dock.Right);
        browse.Margin = new Thickness(6, 0, 0, 0);
        var panel = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Remote file (your ssh config, keys and agent are used):", TextWrapping = TextWrapping.Wrap },
                row,
                list,
                error,
                Dialogs.Buttons(ok, cancel),
            },
        };
        var w = Dialogs.Shell("Open remote file", panel, 560);
        void Fail(string msg)
        {
            error.Text = msg;
            error.IsVisible = true;
        }
        RemoteTarget? folder = null;
        async Task Browse(RemoteTarget dir)
        {
            error.IsVisible = false;
            browse.IsEnabled = false;
            try
            {
                var entries = await RemoteFiles.ListAsync(dir, sshProgram);
                folder = dir;
                list.ItemsSource = (dir.Path == "/" ? entries : entries.Prepend("../")).ToList();
                list.IsVisible = true;
                box.Text = dir.Url.TrimEnd('/') + "/";
            }
            catch (IOException e)
            {
                Fail(e.Message);
            }
            finally
            {
                browse.IsEnabled = true;
            }
        }
        browse.Click += async (_, _) =>
        {
            var text = box.Text?.Trim() ?? "";
            // "host" or "user@host" alone browses the root folder
            if (!text.Contains('/') && !text.Contains(':')) text = $"ssh://{text}/";
            RemoteTarget t;
            try
            {
                t = RemoteTarget.ParseOrThrow(text);
            }
            catch (FormatException e)
            {
                Fail(e.Message);
                return;
            }
            // a file browses its folder
            await Browse(t.Path.EndsWith('/') ? t : t.WithPath(RemoteFiles.Join(t.Path, "..")));
        };
        list.DoubleTapped += async (_, _) =>
        {
            if (folder is null || list.SelectedItem is not string name) return;
            var p = RemoteFiles.Join(folder.Path, name.TrimEnd('/'));
            if (name.EndsWith('/')) await Browse(folder.WithPath(p));
            else w.Close(folder.WithPath(p).Url);
        };
        list.SelectionChanged += (_, _) =>
        {
            if (folder is not null && list.SelectedItem is string name && !name.EndsWith('/')) box.Text = folder.WithPath(RemoteFiles.Join(folder.Path, name)).Url;
        };
        ok.Click += (_, _) =>
        {
            try
            {
                var t = RemoteTarget.ParseOrThrow(box.Text ?? "");
                if (t.Path.EndsWith('/')) throw new FormatException("Pick a file: this is a folder");
                w.Close(t.Url);
            }
            catch (FormatException e)
            {
                Fail(e.Message);
            }
        };
        cancel.Click += (_, _) => w.Close(null);
        w.Opened += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        return await w.ShowDialog<string?>(owner);
    }
}
