using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using UltraLogViewer.Core;
using UltraLogViewer.Desktop.Controls;

namespace UltraLogViewer.Desktop;

/// <summary>
/// The Analytics tab: a dashboard over the whole log of the active tab, filled in by its background analysis
/// (<see cref="AnalyticsJob"/>, started once the files are loaded). It takes the place of the grid and the details
/// pane; a click on a chart filters the log and goes back to it.
/// </summary>
public sealed partial class MainWindow
{
    private readonly AnalyticsView _analytics = new();
    private readonly TextBlock _analyticsState = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
    private readonly ProgressBar _analyticsProgress = new() { Width = 160, Minimum = 0, Maximum = 1, MinHeight = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _analyticsPause = new() { Classes = { "mini" }, Content = "Pause" };
    private readonly Button _statusAnalytics = new() { Classes = { "mini" }, Padding = new Thickness(6, 0), MinHeight = 18, FontSize = 12, IsVisible = false };
    private readonly ProgressBar _statusAnalyticsBar = new() { Width = 80, Minimum = 0, Maximum = 1, IsVisible = false, VerticalAlignment = VerticalAlignment.Center, MinHeight = 4 };
    private ScrollViewer _analyticsScroll = null!;
    private Control _analyticsPane = null!;
    private bool _analyticsShown;

    private Control BuildAnalyticsPane()
    {
        var title = new TextBlock { Text = "Analytics", FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var sub = new TextBlock { Text = "whole log, in the background", Opacity = 0.6, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var rerun = new Button { Classes = { "mini" }, Content = "Re-run" };
        ToolTip.SetTip(rerun, "Analyse the whole log again");
        rerun.Click += (_, _) => AnalyticsOwner()?.Analytics?.Restart();
        _analyticsPause.Click += (_, _) =>
        {
            if (AnalyticsOwner() is not { } t) return;
            if (t.Analytics?.State is AnalyticsState.Running or AnalyticsState.Waiting) t.Analytics.Pause();
            else StartAnalytics(t);
        };
        var close = new Button { Classes = { "mini" }, Content = "×" };
        ToolTip.SetTip(close, "Back to the log (Esc)");
        close.Click += (_, _) => ShowAnalytics(false);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _analyticsState, _analyticsProgress, _analyticsPause, rerun, close } };
        var head = new DockPanel { Margin = new Thickness(10, 4), Children = { right, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { title, sub } } } };
        DockPanel.SetDock(right, Dock.Right);
        var headBorder = new Border { Child = head, BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = new SolidColorBrush(Color.Parse("#808080"), 0.25) };
        _analyticsScroll = new ScrollViewer { Content = _analytics, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var pane = new DockPanel { Children = { headBorder, _analyticsScroll } };
        DockPanel.SetDock(headBorder, Dock.Top);
        _analyticsPane = new Border { Child = pane, IsVisible = false };
        _analytics.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            ShowAnalytics(false);
        };
        _analytics.FilterLevel += (l, only, group) => FilterFromAnalytics(t => SetLevels(t, l, only, group));
        _analytics.FilterTime += (from, to) => FilterFromAnalytics(t =>
        {
            t.Filter.TimeFrom = from;
            t.Filter.TimeTo = to;
        });
        _analytics.FilterPattern += (tpl, include) => FilterFromAnalytics(t =>
        {
            if (tpl != "(other)") t.Filter.Rules.Add(new FilterRule { Text = tpl, PatternMatch = true, CaseSensitive = true, Mode = include ? RuleMode.Include : RuleMode.Exclude });
        });
        _analytics.FilterText += (text, include) => FilterFromAnalytics(t =>
            t.Filter.Rules.Add(new FilterRule { Text = text, CaseSensitive = true, Mode = include ? RuleMode.Include : RuleMode.Exclude }));
        _analytics.FilterField += (key, value, include) =>
        {
            ShowAnalytics(false);
            AddValueRule(key, value, include ? RuleMode.Include : RuleMode.Exclude);
        };
        _analytics.JumpTo += t =>
        {
            ShowAnalytics(false);
            if (_active is { } tab) _grid.Select(tab.Doc.RowForTime(t), center: true);
        };
        _statusAnalytics.Click += (_, _) => ShowAnalytics(true);
        ToolTip.SetTip(_statusAnalytics, "Background analysis of the whole log: open Analytics (Ctrl+Shift+Y)");
        return _analyticsPane;
    }

    /// <summary>The tab the dashboard is about: the left pane's tab in a split.</summary>
    private DocumentTab? AnalyticsOwner() => OwnerOf(_active);

    private void ToggleAnalytics() => ShowAnalytics(!_analyticsShown);

    private void ShowAnalytics(bool on)
    {
        if (_active is null) on = false;
        if (on && _patternsShown) ShowPatterns(false);
        _analyticsShown = on;
        _analyticsPane.IsVisible = on;
        _work.IsVisible = !on;
        RefreshChecks();
        if (on && AnalyticsOwner() is { } t)
        {
            if (t.Analytics is null) StartAnalytics(t);
            RenderAnalytics(t);
            _analytics.Focus();
        }
        else if (!on) _grid.Focus();
    }

    /// <summary>Starts (or resumes) the background analysis of a tab's whole log.</summary>
    private void StartAnalytics(DocumentTab tab)
    {
        tab = OwnerOf(tab)!;
        if (!tab.Ready || !_tabs.Contains(tab)) return;
        if (tab.Analytics is null)
        {
            var job = new AnalyticsJob(tab.Doc);
            job.Updated += r => Dispatcher.UIThread.Post(() => OnAnalytics(tab, r));
            tab.Analytics = job;
        }
        tab.Analytics.Start();
        OnAnalytics(tab, tab.Analytics.Snapshot());
    }

    private void OnAnalytics(DocumentTab tab, AnalyticsResult r)
    {
        if (!_tabs.Contains(tab)) return;
        // snapshots can arrive out of order across thread hops: keep the newest run's latest
        if (tab.AnalyticsResult is { } old && old.Run == r.Run && old.State == AnalyticsState.Done && r.State == AnalyticsState.Running && r.Scanned < old.Scanned) return;
        tab.AnalyticsResult = r;
        if (tab != AnalyticsOwner()) return;
        if (_analyticsShown) RenderAnalytics(tab);
        UpdateAnalyticsStatus();
    }

    private void RenderAnalytics(DocumentTab tab)
    {
        var r = tab.AnalyticsResult;
        _analytics.Data = r;
        var frac = r is null ? 0 : r.State == AnalyticsState.Done ? 1 : r.Progress;
        _analyticsProgress.Value = frac;
        _analyticsState.Text = r is null ? "Not started" : AnalyticsStateText(r);
        if (r?.State == AnalyticsState.Error) _analyticsState.Foreground = Palette.Current.Error;
        else _analyticsState.ClearValue(TextBlock.ForegroundProperty);
        var running = r?.State is AnalyticsState.Running or AnalyticsState.Waiting;
        _analyticsPause.Content = running ? "Pause" : r?.State == AnalyticsState.Paused ? "Resume" : "Start";
        _analyticsPause.IsVisible = r?.State != AnalyticsState.Done;
    }

    internal static string AnalyticsStateText(AnalyticsResult r) => r.State switch
    {
        AnalyticsState.Waiting => "Waiting: loading and filtering go first",
        AnalyticsState.Running => $"Analysing {Math.Floor(r.Progress * 100)}% · {r.Scanned:N0} of {r.Total:N0} entries read",
        AnalyticsState.Paused => $"Paused at {Math.Floor(r.Progress * 100)}%",
        AnalyticsState.Done => $"Analysed {r.Total:N0} entries in {Fmt.Span(r.ElapsedMs)}",
        AnalyticsState.Cancelled => "Stopped",
        _ => $"Analysis failed: {r.Error}",
    };

    /// <summary>The status bar shows the analysis while it runs.</summary>
    private void UpdateAnalyticsStatus()
    {
        var r = AnalyticsOwner()?.AnalyticsResult;
        var on = r is { State: AnalyticsState.Running or AnalyticsState.Waiting or AnalyticsState.Paused } && _active?.BusyProgress is null;
        _statusAnalytics.IsVisible = _statusAnalyticsBar.IsVisible = on;
        if (!on) return;
        var label = r!.State switch { AnalyticsState.Paused => "Analysis paused", AnalyticsState.Waiting => "Analysis waiting", _ => "Analysing" };
        _statusAnalytics.Content = $"{label} {Math.Floor(r.Progress * 100)}%";
        _statusAnalyticsBar.Value = r.Progress;
    }

    private void FilterFromAnalytics(Action<DocumentTab> change)
    {
        if (AnalyticsOwner() is not { } tab) return;
        ShowAnalytics(false);
        if (_active != tab) Activate(tab);
        change(tab);
        ApplyFilter();
    }

    /// <summary>Only (or hide) a level from the dashboard. Grouped (the summary and the timeline series): "errors" are errors and fatals, "debug" is debug, trace and no level.</summary>
    internal static void SetLevels(DocumentTab t, Level l, bool only, bool grouped)
    {
        Level[] group = !grouped ? [l] : l switch
        {
            Level.Error => [Level.Error, Level.Fatal],
            Level.Debug => [Level.Debug, Level.Trace, Level.None],
            _ => [l],
        };
        if (only)
            for (var i = 0; i < t.Filter.Levels.Length; i++) t.Filter.Levels[i] = group.Contains((Level)i);
        else
            foreach (var g in group) t.Filter.Levels[(int)g] = false;
    }

    internal AnalyticsView AnalyticsForTest => _analytics;
    internal bool AnalyticsShownForTest => _analyticsShown;
    internal void ToggleAnalyticsForTest() => ToggleAnalytics();
    internal ScrollViewer AnalyticsScrollForTest => _analyticsScroll;
}
