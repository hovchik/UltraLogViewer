using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using UltraLogViewer.Core;

namespace UltraLogViewer.Desktop;

/// <summary>The local HTTP API settings (off by default). The token is made the first time the API is turned on.</summary>
public sealed class LocalApiSettings
{
    public bool Enabled { get; set; }
    public int Port { get; set; } = LocalApiServer.DefaultPort;
    public string Token { get; set; } = "";
}

/// <summary>
/// Open requests from outside: the command line (and a second launch handing its command line over), deep links,
/// and the local HTTP API; plus "Copy link to this view" and "Copy API example". Everything goes through
/// <see cref="HandleRequestAsync"/>, and a request from a link is confirmed by the user before any file opens.
/// </summary>
public sealed partial class MainWindow : ILocalApiHost
{
    private IDisposable? _handoff;
    private LocalApiServer? _api;

    /// <summary>The API's port while it runs (it may differ from the setting when that port was taken).</summary>
    internal int? ApiPort => _api?.Port;

    /// <summary>Handles the startup command line once the window is shown, and takes over later launches' command lines.</summary>
    public void StartWith(OpenRequest request, string cwd, string? pipeName = null)
    {
        Opened += (_, _) =>
        {
            if (request.Paths.Count > 0 || !request.IsValid) Run(() => HandleRequestAsync(request, cwd));
            if (_settings.LocalApi.Enabled) StartApi(quiet: true);
        };
        ListenForLaunches(pipeName ?? SingleInstance.DefaultName);
    }

    /// <summary>Later launches of the app hand their command line (or clicked link) to this window.</summary>
    internal void ListenForLaunches(string pipeName)
    {
        _handoff = SingleInstance.Listen(pipeName, msg =>
        {
            if (SingleInstance.Handoff.FromJson(msg) is not { } h) return;
            var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            var req = OpenRequest.FromCommandLine(CommandLine.Parse(h.Args, now), now);
            Dispatcher.UIThread.Post(() =>
            {
                BringToFront();
                if (req.Paths.Count > 0 || !req.IsValid) Run(() => HandleRequestAsync(req, h.Cwd));
            });
        });
        StopOnClose();
    }

    private bool _stopOnClose;

    private void StopOnClose()
    {
        if (_stopOnClose) return;
        _stopOnClose = true;
        Closed += (_, _) =>
        {
            _handoff?.Dispose();
            _api?.Dispose();
            _api = null;
        };
    }

    /// <summary>A clicked ultralogviewer:// link (macOS delivers it as an event).</summary>
    public void OpenLink(string url)
    {
        BringToFront();
        Run(() => HandleRequestAsync(OpenRequest.FromUrl(url, DateTimeOffset.Now.ToUnixTimeMilliseconds()), Environment.CurrentDirectory));
    }

    private void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        base.Activate();
        // Some window managers only raise a window that was briefly topmost.
        Topmost = true;
        Topmost = false;
    }

    // ================================================================ requests

    /// <summary>
    /// Opens (or reuses) the tabs of a request and applies its filter, levels, search and "go to". Problems are shown
    /// as a toast and left in <see cref="OpenRequest.Errors"/>. Returns the last tab it showed.
    /// </summary>
    public async Task<DocumentTab?> HandleRequestAsync(OpenRequest req, string cwd)
    {
        if (!req.IsValid)
        {
            Toast(string.Join("\n", req.Errors), true);
            return null;
        }
        // remote files as ssh:// URLs, the form kept in recent files (only the command line can name them)
        var files = req.ExpandPaths(cwd).Select(f => RemoteTarget.LooksRemote(f) ? RemoteTarget.Parse(f)?.Url ?? f : f).ToList();
        if (!req.IsValid) Toast(string.Join("\n", req.Errors.Take(5)), true);
        var stdin = files.Remove("-");
        if (files.Count == 0 && !stdin) return null;
        if (req.Source == RequestSource.Url && !await ConfirmLink(files)) return null;
        var format = req.Format;
        if (format is not null && format != "w3c" && !FormatDetector.BuiltIns.Any(b => b.Id == format) && !_settings.CustomFormats.Any(c => "custom:" + c.Name == format))
        {
            Toast($"Unknown format \"{format}\": detecting it instead", true);
            req.Errors.Add($"Unknown format \"{format}\"");
            format = null;
        }
        var window = req.Window is { IsEmpty: false } w ? w : null;
        DocumentTab? last = null;
        var groups = req.Merge && files.Count > 1 ? [files.ToArray()] : files.Select(f => new[] { f }).ToList();
        if (files.Count > 0)
        {
            _settings.AddRecent(files);
            _settings.Save();
        }
        foreach (var g in groups)
        {
            // a remote file's tab has its local copy
            var keys = g.Select(p => RemoteTarget.Parse(p) is { } remote ? RemoteFile.LocalPathFor(remote, RemoteOptions.CacheDir) : p).ToArray();
            var tab = req.NewTab ? null : _tabs.FirstOrDefault(t => t.Paths.SequenceEqual(keys, StringComparer.Ordinal) && Equals(t.Window, window) && (format is null || t.FormatId == format));
            if (tab is not null) Activate(tab);
            else tab = await OpenTab(g, "auto", null, t =>
            {
                t.Window = window;
                t.FormatId = format ?? "";
            });
            if (tab is null) continue;
            await ApplyRequestAsync(tab, req);
            last = tab;
        }
        if (stdin && req.Source == RequestSource.CommandLine && _stdin is null)
        {
            await OpenStdinAsync(window);
            if (_active is { } st)
            {
                await ApplyRequestAsync(st, req);
                last = st;
            }
        }
        return last;
    }

    /// <summary>A link can come from any web page: it opens files only after the user saw which ones.</summary>
    private async Task<bool> ConfirmLink(List<string> files)
    {
        if (ConfirmLinkForTest is { } answer) return answer(files);
        var odd = files.Where(f => !OpenRequest.LooksLikeLog(f)).ToList();
        var text = "A link asks to open:\n\n" + string.Join("\n", files.Take(12)) + (files.Count > 12 ? $"\n… and {files.Count - 12} more" : "") +
                   (odd.Count > 0 ? "\n\nThese do not look like log files:\n" + string.Join("\n", odd.Take(6)) : "") +
                   "\n\nOpen them only if you trust where the link came from.";
        return await Dialogs.Confirm(this, "Open files from a link?", text, "Open");
    }

    /// <summary>UI tests answer the link confirmation (and see which files it named).</summary>
    internal Func<List<string>, bool>? ConfirmLinkForTest { get; set; }

    /// <summary>Applies the filter, levels, search and "go to" of a request to an open tab (waiting until it is indexed).</summary>
    private async Task ApplyRequestAsync(DocumentTab tab, OpenRequest req)
    {
        while (!tab.Ready && IsOpen(tab)) await Task.Delay(30);
        if (!IsOpen(tab)) return;
        if (OwnerOf(_active) != OwnerOf(tab)) Activate(tab);
        var refilter = false;
        if (req.Levels is { } lv)
        {
            tab.Filter.Levels = (bool[])lv.Clone();
            refilter = true;
        }
        if (req.Filter is { } q)
        {
            if (Query.ValidateQuery(q, tab.Filter.QueryCaseSensitive) is { } err)
            {
                Toast($"Quick filter: {err}", true);
                req.Errors.Add($"filter: {err}");
            }
            else
            {
                tab.Filter.Query = q;
                refilter = true;
            }
        }
        if (refilter)
        {
            if (_active == tab) SetQuickFilterText(tab.Filter.Query);
            await ApplyFilterAsync(tab);
            if (_active == tab) _sidebar.Render(tab);
        }
        if (req.Search is { } s && _active == tab)
        {
            _search.Text = s;
            _sRegex.IsChecked = _sWord.IsChecked = _sCase.IsChecked = false;
            await RunSearch(jump: req.GoTo is null);
        }
        if (req.GoTo is { } g)
        {
            int row;
            if (g.Time is long t) row = tab.Doc.RowForTime(t);
            else if (SourceOf(tab, g.File) is int src) row = tab.Doc.RowForLine(src, g.Line);
            else
            {
                Toast($"No file \"{g.File}\" in this tab", true);
                req.Errors.Add($"goto: no file \"{g.File}\"");
                return;
            }
            if (_active == tab && tab.Info.ViewCount > 0) _grid.Select(Math.Clamp(row, 0, tab.Info.ViewCount - 1), center: true);
        }
    }

    /// <summary>A "go to" file: null is the first, "2" the second, or a path or file name.</summary>
    private static int? SourceOf(DocumentTab tab, string? file)
    {
        var src = tab.Info.Sources;
        if (file is null) return 0;
        if (int.TryParse(file, out var n)) return n >= 1 && n <= src.Count ? n - 1 : null;
        for (var i = 0; i < src.Count; i++)
            if (string.Equals(src[i].Path, file, StringComparison.Ordinal) || string.Equals(src[i].DisplayName, file, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Archives.DisplayName(src[i].Path), file, StringComparison.OrdinalIgnoreCase))
                return i;
        return null;
    }

    // ======================================================== copy link to view

    /// <summary>The request that reopens a tab as it is: its files, window, filter, levels, search and the selected entry.</summary>
    internal static OpenRequest ViewRequest(DocumentTab tab, RowData? row)
    {
        var req = new OpenRequest
        {
            Paths = [.. tab.Paths],
            Merge = tab.Paths.Length > 1,
            Format = tab.FormatId.Length > 0 ? tab.FormatId : null,
            Window = tab.Window,
            Filter = string.IsNullOrWhiteSpace(tab.Filter.Query) ? null : tab.Filter.Query,
            Levels = tab.Filter.Levels.All(l => l) ? null : (bool[])tab.Filter.Levels.Clone(),
            Search = tab.SearchPattern is { Text.Length: > 0, Regex: false } p ? p.Text : null,
        };
        if (row is not null)
            req.GoTo = row.Time is long t ? new GoToTarget(Time: t)
                : tab.Info.Sources.Count > 1 ? new GoToTarget(Line: row.Line, File: (row.Source + 1).ToString(System.Globalization.CultureInfo.InvariantCulture))
                : new GoToTarget(Line: row.Line);
        return req;
    }

    private async Task CopyViewLink() => await CopyViewLink(null);

    private async Task CopyViewLink(RowData? row)
    {
        if (_active is not { } tab)
        {
            Toast("Open a file first", true);
            return;
        }
        if (_stdin is not null && tab.Paths.Contains(_stdin.Path))
        {
            Toast("Standard input cannot be linked", true);
            return;
        }
        var req = ViewRequest(tab, row ?? await RowAsync(tab, tab.Focus));
        await CopyText(req.ToUrl(), tab.Filter.Rules.Any(r => r.Enabled) ? "Link (without the sidebar's filter rules) copied" : "Link to this view copied");
    }

    internal Task CopyViewLinkForTest() => CopyViewLink();

    // ================================================================= HTTP API

    private MenuItem LocalApiMenu() => new()
    {
        Header = "Local _API",
        Items =
        {
            Check("Enable local HTTP API", () => _settings.LocalApi.Enabled, ToggleApi),
            Item("Copy API example", () => Run(CopyApiExample)),
            Item("New API token", NewApiToken),
        },
    };

    private void ToggleApi()
    {
        _settings.LocalApi.Enabled = !_settings.LocalApi.Enabled;
        _settings.Save();
        if (_settings.LocalApi.Enabled) StartApi(quiet: false);
        else
        {
            _api?.Dispose();
            _api = null;
            Toast("Local API off");
        }
        RefreshChecks();
    }

    internal void StartApi(bool quiet)
    {
        var s = _settings.LocalApi;
        if (s.Token.Length < 32)
        {
            s.Token = LocalApiServer.NewToken();
            _settings.Save();
        }
        _api?.Dispose();
        StopOnClose();
        try
        {
            _api = new LocalApiServer(this, s.Token);
            var port = _api.Start(s.Port is >= 0 and < 65536 ? s.Port : LocalApiServer.DefaultPort);
            if (!quiet || port != s.Port && s.Port != 0) Toast($"Local API on http://127.0.0.1:{port}{(port != s.Port ? $" (port {s.Port} was taken)" : "")}. File › Local API › Copy API example");
        }
        catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException)
        {
            _api = null;
            Toast($"Local API: {e.Message}", true);
        }
    }

    private void NewApiToken()
    {
        _settings.LocalApi.Token = LocalApiServer.NewToken();
        _settings.Save();
        if (_api is not null) StartApi(quiet: true);
        Toast("New API token: scripts using the old one stop working");
    }

    private async Task CopyApiExample()
    {
        if (_api is null)
        {
            Toast("Turn on File › Local API › Enable local HTTP API first", true);
            return;
        }
        await CopyText(LocalApiServer.CurlExample(_api.Port, _settings.LocalApi.Token, _active?.Paths.FirstOrDefault()), "API example (it contains your token) copied");
    }

    private static Task<T> OnUi<T>(Func<Task<T>> fn) => Dispatcher.UIThread.InvokeAsync(fn);

    private DocumentTab? TabById(int id) => AllTabs().FirstOrDefault(t => t.Id == id);

    Task<ApiResult> ILocalApiHost.OpenAsync(OpenRequest request) =>
        OnUi(async () =>
        {
            BringToFront();
            var tab = await HandleRequestAsync(request, Path.GetTempPath());
            if (tab is null) return new ApiResult(404, request.Errors.Count > 0 ? string.Join("; ", request.Errors) : "Nothing was opened");
            return new ApiResult(200, null, tab.Id);
        });

    Task<IReadOnlyList<ApiTab>> ILocalApiHost.TabsAsync() => OnUi(() => Task.FromResult<IReadOnlyList<ApiTab>>(AllTabs()
        .Select(t => new ApiTab(t.Id, t.SplitOf is null ? t.Doc.Title : t.Doc.Title + " (split)", t.Paths, t.Info.Total, t.Info.ViewCount, t.Filter.Query, t.Ready, t == _active))
        .ToList()));

    Task<ApiResult> ILocalApiHost.UpdateAsync(int id, OpenRequest request) => OnUi(async () =>
    {
        if (TabById(id) is not { } tab) return new ApiResult(404, $"No tab {id}");
        BringToFront();
        await ApplyRequestAsync(tab, request);
        return request.Errors.Count > 0 ? new ApiResult(422, string.Join("; ", request.Errors), id) : ApiResult.Ok(id);
    });

    async Task<IReadOnlyList<RowData>?> ILocalApiHost.RowsAsync(int id, int from, int count)
    {
        var tab = await OnUi(() => Task.FromResult(TabById(id)));
        if (tab is null) return null;
        while (!tab.Ready && await OnUi(() => Task.FromResult(IsOpen(tab)))) await Task.Delay(30);
        return await Task.Run(() =>
        {
            var n = tab.Doc.ViewCount;
            return from >= n ? [] : (IReadOnlyList<RowData>)tab.Doc.GetRows(from, Math.Min(count, n - from));
        });
    }
}
