using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using UltraLogViewer.Core;

namespace UltraLogViewer.Desktop;

/// <summary>Opening files: time windows, the index cache, standard input and the partial-view banner.</summary>
public sealed partial class MainWindow
{
    private readonly Border _partialBanner;
    private readonly TextBlock _partialText = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private StdinCapture? _stdin;

    private SourceOptions SourceOptionsFor(DocumentTab tab, string encoding) => new()
    {
        Encoding = encoding,
        CustomFormats = _settings.CustomFormats,
        Format = tab.FormatId.Length > 0 ? tab.FormatId : null,
        CacheDir = _settings.IndexCache ? _settings.IndexCacheDir : null,
        CacheMaxBytes = Math.Max(64, _settings.IndexCacheMaxMb) * 1024 * 1024,
        Window = tab.Window,
    };

    // ============================================================ time window

    /// <summary>"Open last…" (a duration back from the newest entry) or "Open time range…" (from / until).</summary>
    private async Task PickAndOpenWindow(bool range)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = range ? "Open a time range of log files" : "Open the last part of log files",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Log files") { Patterns = ["*.log", "*.txt", "*.json", "*.jsonl", "*.gz", "*.bz2", "*.zip", "*.*"] }, FilePickerFileTypes.All],
        });
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray();
        if (paths.Length == 0) return;
        var window = await AskWindow(range);
        if (window is not null) await OpenPaths(paths, paths.Length > 1, window);
    }

    private async Task<OpenWindow?> AskWindow(bool range)
    {
        if (!range)
        {
            var d = await Dialogs.Prompt(this, "Open last", "Show the newest entries of this long (e.g. 15m, 1h, 2d):", "1h", "1h",
                v => TimeWindow.ParseDuration(v) is null ? "Enter a duration such as 15m, 1h or 2d" : null);
            return d is null ? null : new OpenWindow(Last: TimeWindow.ParseDuration(d));
        }
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        string? Check(string v) => v.Trim().Length == 0 || TimeWindow.ParsePoint(v, now) is not null ? null : "Enter a time such as 2024-05-01 10:00, 10:30 or -2h";
        var from = await Dialogs.Prompt(this, "Open time range", "From (2024-05-01 10:00, 10:30, -2h; empty = the start):", "-1h", "2024-05-01 10:00", Check);
        if (from is null) return null;
        var until = await Dialogs.Prompt(this, "Open time range", "Until (empty = the end, and keep following new lines):", "", "2024-05-01 11:00", Check);
        if (until is null) return null;
        var w = new OpenWindow(TimeWindow.ParsePoint(from, now), TimeWindow.ParsePoint(until, now));
        return w.IsEmpty ? null : w;
    }

    private Border BuildPartialBanner()
    {
        var whole = new Button { Content = "Load whole file", Classes = { "mini" }, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        whole.Click += (_, _) => Run(LoadWholeFile);
        var dp = new DockPanel { Margin = new Thickness(10, 4) };
        DockPanel.SetDock(whole, Dock.Right);
        dp.Children.Add(whole);
        dp.Children.Add(_partialText);
        return new Border
        {
            IsVisible = false,
            Background = new SolidColorBrush(Color.Parse("#E8590C"), 0.14),
            BorderBrush = new SolidColorBrush(Color.Parse("#E8590C"), 0.45),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = dp,
        };
    }

    /// <summary>Text of the partial-view banner, e.g. "Showing 2024-05-01 10:00–11:00 · partial file".</summary>
    internal static string PartialText(DocInfo info)
    {
        var src = info.Sources.Where(s => s.Partial).ToList();
        if (src.Count == 0) return "";
        var range = info.TimeMin is long a && info.TimeMax is long b ? TimeWindow.Describe(a, b) : "no entries in " + TimeWindow.Describe(src[0].WindowFrom, src[0].WindowTo);
        var what = info.Sources.Count > 1 ? "partial files" : "partial file";
        return $"Showing {range} · {what}{(src.All(s => s.Live) ? " · following new lines" : "")}";
    }

    private void UpdatePartialBanner()
    {
        var tab = _active;
        var on = tab is { Ready: true, IsPartial: true };
        _partialBanner.IsVisible = on;
        if (on) _partialText.Text = PartialText(tab!.Info);
    }

    internal string PartialBannerForTest => _partialBanner.IsVisible ? _partialText.Text ?? "" : "";

    /// <summary>Reopens a partial tab over the whole file (keeping its filter and format).</summary>
    public async Task LoadWholeFile()
    {
        if (_active is not { Window: not null } tab) return;
        await OpenTab(tab.Paths, tab.Encoding, tab, t => t.Window = null);
    }

    // ============================================================ index cache

    private void ToggleIndexCache()
    {
        _settings.IndexCache = !_settings.IndexCache;
        _settings.Save();
        RefreshChecks();
        Toast(_settings.IndexCache ? "Indexes of big files are saved, so they reopen instantly" : "Index cache turned off");
    }

    private void ClearIndexCache()
    {
        var freed = IndexCache.Clear(_settings.IndexCacheDir);
        Toast($"Index cache cleared ({Sidebar.FormatSize(freed)})");
    }

    // ================================================================ stdin

    /// <summary>"UltraLogViewer -": reads standard input into a temp file that is tailed live.</summary>
    private async Task OpenStdinAsync(OpenWindow? window)
    {
        _stdin = await StdinCapture.StartAsync(Console.OpenStandardInput(), Path.Combine(Path.GetTempPath(), "UltraLogViewer"));
        await OpenTab([_stdin.Path], "auto", null, t =>
        {
            t.Window = window;
            t.Follow = true;
        });
    }
}
