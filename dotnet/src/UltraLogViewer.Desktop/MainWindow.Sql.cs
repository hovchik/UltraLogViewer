using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using UltraLogViewer.Core;
using UltraLogViewer.Desktop.Controls;

namespace UltraLogViewer.Desktop;

/// <summary>
/// The SQL panel (View › SQL, Ctrl+Shift+Q): a query editor over the tab's entries (<see cref="LogSql"/>), the results
/// as a grid or a chart, and a click on a result row that has a line (or row) jumps to that entry. It docks below the
/// grid so both stay in sight; each tab keeps its own table, query and result.
/// </summary>
public sealed partial class MainWindow
{
    private sealed class SqlState(LogDocument doc)
    {
        public readonly LogDocument Doc = doc;
        public readonly LogSql Sql = new(doc);
        public string Query = SqlSchema.DefaultQuery;
        public SqlResult? Result;
        public IReadOnlyList<SqlColumn>? Schema;
    }

    private readonly Dictionary<DocumentTab, SqlState> _sql = [];
    private bool _sqlShown;
    private Grid _sqlHost = null!;
    private Control _sqlPane = null!;
    private GridSplitter _sqlSplitter = null!;
    private readonly TextBox _sqlEditor = new()
    {
        AcceptsReturn = true,
        AcceptsTab = false,
        TextWrapping = TextWrapping.NoWrap,
        FontFamily = Fonts.Mono,
        FontSize = 12.5,
        Watermark = "SELECT … FROM logs   (Ctrl+Enter runs)",
    };
    private readonly ComboBox _sqlExamples = new() { PlaceholderText = "Examples", FontSize = 12, MinWidth = 120, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _sqlRun = new() { Content = "Run", Classes = { "accent" }, Padding = new Thickness(12, 2), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _sqlCancel = new() { Content = "Cancel", Classes = { "mini" }, IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _sqlLoadAll = new() { Content = "Load all", Classes = { "mini" }, IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
    private readonly ToggleButton _sqlChartBtn = new() { Content = "Chart", Classes = { "tb" }, IsEnabled = false, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _sqlStatus = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _sqlError = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false, Margin = new Thickness(8, 4), FontFamily = Fonts.Mono };
    private readonly TextBlock _sqlColumns = new() { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Opacity = 0.65, Margin = new Thickness(2, 4, 2, 0) };
    private readonly ProgressBar _sqlProgress = new() { Width = 120, Minimum = 0, Maximum = 1, IsVisible = false, VerticalAlignment = VerticalAlignment.Center, MinHeight = 4 };
    private readonly SqlResultsView _sqlResults = new();
    private readonly SqlChart _sqlChart = new() { IsVisible = false };
    private ScrollViewer _sqlScroll = null!;
    private CancellationTokenSource? _sqlCts;
    private List<SqlExample> _sqlExampleList = [];

    /// <summary>Puts the SQL panel (hidden at first) below <paramref name="center"/> with a splitter between.</summary>
    private Control BuildSqlDock(Control center)
    {
        _sqlEditor.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                e.Handled = true;
                Run(RunSqlAsync);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                ShowSql(false);
            }
        }, RoutingStrategies.Tunnel);
        ToolTip.SetTip(_sqlRun, "Run the query (Ctrl+Enter)");
        _sqlRun.Click += (_, _) => Run(RunSqlAsync);
        _sqlCancel.Click += (_, _) => _sqlCts?.Cancel();
        ToolTip.SetTip(_sqlLoadAll, "Load every entry instead of the first 2,000,000 (uses more memory)");
        _sqlLoadAll.Click += (_, _) =>
        {
            if (SqlFor(OwnerOf(_active)) is not { } st) return;
            st.Sql.Cap = 0;
            Run(RunSqlAsync);
        };
        ToolTip.SetTip(_sqlChartBtn, "Show a (label, number) result as a bar chart");
        _sqlChartBtn.IsCheckedChanged += (_, _) => ShowSqlResult(SqlFor(OwnerOf(_active))?.Result);
        _sqlExamples.SelectionChanged += (_, _) =>
        {
            if (_sqlExamples.SelectedIndex is var i and >= 0 && i < _sqlExampleList.Count)
            {
                _sqlEditor.Text = _sqlExampleList[i].Sql;
                _sqlExamples.SelectedIndex = -1;
                Run(RunSqlAsync);
            }
        };
        _sqlResults.RowSelected += JumpToSqlRow;
        var close = new Button { Content = "×", Classes = { "mini" }, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(close, "Close the SQL panel (Ctrl+Shift+Q)");
        close.Click += (_, _) => ShowSql(false);

        var title = new TextBlock { Text = "SQL", FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { title, _sqlRun, _sqlCancel, _sqlExamples, _sqlChartBtn, _sqlProgress } };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _sqlLoadAll, close } };
        var head = new DockPanel { Margin = new Thickness(8, 4), Children = { left, right, _sqlStatus } };
        DockPanel.SetDock(left, Dock.Left);
        DockPanel.SetDock(right, Dock.Right);
        _sqlStatus.Margin = new Thickness(12, 0);

        var editor = new DockPanel { Margin = new Thickness(8, 0, 4, 6), Children = { _sqlColumns, _sqlEditor } };
        DockPanel.SetDock(_sqlColumns, Dock.Bottom);
        _sqlScroll = new ScrollViewer { Content = _sqlResults, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var results = new DockPanel { Children = { _sqlError, new Panel { Children = { _sqlScroll, _sqlChart } } } };
        DockPanel.SetDock(_sqlError, Dock.Top);
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("380,4,*") };
        body.ColumnDefinitions[0].MinWidth = 160;
        var bodySplit = new GridSplitter { ResizeDirection = GridResizeDirection.Columns, Background = Brushes.Transparent };
        Grid.SetColumn(bodySplit, 1);
        Grid.SetColumn(results, 2);
        body.Children.Add(editor);
        body.Children.Add(bodySplit);
        body.Children.Add(results);
        var pane = new DockPanel { Children = { head, body } };
        DockPanel.SetDock(head, Dock.Top);
        _sqlPane = new Border { Child = pane, IsVisible = false };

        _sqlSplitter = new GridSplitter { ResizeDirection = GridResizeDirection.Rows, Height = 5, IsVisible = false, Background = new SolidColorBrush(Color.Parse("#808080"), 0.25) };
        _sqlHost = new Grid { RowDefinitions = new RowDefinitions("*,Auto,0") };
        _sqlHost.RowDefinitions[2].MinHeight = 0;
        Grid.SetRow(_sqlSplitter, 1);
        Grid.SetRow(_sqlPane, 2);
        _sqlHost.Children.Add(center);
        _sqlHost.Children.Add(_sqlSplitter);
        _sqlHost.Children.Add(_sqlPane);
        _sqlHost.SizeChanged += (_, e) => _sqlHost.RowDefinitions[2].MaxHeight = Math.Max(120, e.NewSize.Height * 0.8);
        return _sqlHost;
    }

    private void ToggleSql() => ShowSql(!_sqlShown);

    private void ShowSql(bool on)
    {
        if (OwnerOf(_active) is null) on = false;
        if (on == _sqlShown) return;
        if (!on) SaveSqlQuery();
        _sqlShown = on;
        _sqlPane.IsVisible = on;
        _sqlSplitter.IsVisible = on;
        _sqlHost.RowDefinitions[2].Height = on ? new GridLength(300) : new GridLength(0);
        RefreshChecks();
        if (on)
        {
            SqlTabChanged();
            _sqlEditor.Focus();
        }
        else
        {
            _sqlCts?.Cancel();
            _grid.Focus();
        }
    }

    /// <summary>The SQL state of a tab (its main pane's document); a new one when the tab got a new document.</summary>
    private SqlState? SqlFor(DocumentTab? tab)
    {
        if (tab is null) return null;
        if (_sql.TryGetValue(tab, out var st) && st.Doc == tab.Doc) return st;
        st?.Sql.Dispose();
        return _sql[tab] = new SqlState(tab.Doc) { Query = st?.Query ?? SqlSchema.DefaultQuery };
    }

    private void SaveSqlQuery()
    {
        if (_sqlEditor.Tag is DocumentTab t && _sql.TryGetValue(t, out var st)) st.Query = _sqlEditor.Text ?? "";
    }

    /// <summary>Shows the active tab's query and result (called when the panel opens and when the tab changes).</summary>
    private void SqlTabChanged()
    {
        if (!_sqlShown) return;
        SaveSqlQuery();
        var tab = OwnerOf(_active);
        if (SqlFor(tab) is not { } st)
        {
            ShowSql(false);
            return;
        }
        _sqlCts?.Cancel();
        _sqlEditor.Tag = tab;
        _sqlEditor.Text = st.Query;
        ShowSqlResult(st.Result);
        if (st.Schema is null) Run(() => LoadSqlSchemaAsync(tab!, st));
        else ShowSqlSchema(st);
    }

    private async Task LoadSqlSchemaAsync(DocumentTab tab, SqlState st)
    {
        if (!tab.Ready) return;
        st.Schema = await st.Sql.SchemaAsync();
        if (OwnerOf(_active) == tab) ShowSqlSchema(st);
    }

    private void ShowSqlSchema(SqlState st)
    {
        var cols = st.Schema ?? [];
        _sqlColumns.Text = "Tables: logs, view (filtered).  Columns: " + string.Join(", ", cols.Select(c => c.Name));
        ToolTip.SetTip(_sqlColumns, string.Join("\n", cols.Select(c => c.BuiltIn ? c.Name : $"{c.Name}  ← field \"{c.Field}\"")));
        _sqlExampleList = SqlSchema.Examples(cols);
        _sqlExamples.ItemsSource = _sqlExampleList.Select(x => x.Title).ToList();
    }

    private void DropSql(DocumentTab tab)
    {
        if (!_sql.Remove(tab, out var st)) return;
        if (_sqlEditor.Tag == tab) _sqlEditor.Tag = null;
        st.Sql.Dispose();
    }

    private async Task RunSqlAsync()
    {
        var tab = OwnerOf(_active);
        if (SqlFor(tab) is not { } st || tab is not { Ready: true }) return;
        _sqlCts?.Cancel();
        var cts = _sqlCts = new CancellationTokenSource();
        st.Query = _sqlEditor.Text ?? "";
        _sqlRun.IsEnabled = false;
        _sqlCancel.IsVisible = true;
        _sqlStatus.Text = "Running…";
        var shownProgress = false;
        var progress = new Progress<double>(f =>
        {
            if (cts.IsCancellationRequested) return;
            if (!shownProgress) _sqlStatus.Text = st.Sql.Cap > 0 && tab.Info.Total > st.Sql.Cap ? $"Loading the first {st.Sql.Cap:N0} entries…" : $"Loading {tab.Info.Total:N0} entries…";
            shownProgress = true;
            _sqlProgress.IsVisible = true;
            _sqlProgress.Value = f;
        });
        SqlResult r;
        try
        {
            st.Schema ??= await st.Sql.SchemaAsync(cts.Token);
            if (OwnerOf(_active) == tab) ShowSqlSchema(st);
            r = await st.Sql.QueryAsync(st.Query, progress, cts.Token);
        }
        catch (OperationCanceledException)
        {
            r = new SqlResult([], [], false, null, 0, 0, st.Sql.Loaded, st.Sql.Total, st.Sql.Capped, Cancelled: true);
        }
        finally
        {
            if (_sqlCts == cts)
            {
                _sqlRun.IsEnabled = true;
                _sqlCancel.IsVisible = false;
                _sqlProgress.IsVisible = false;
            }
        }
        if (_sqlCts != cts) return; // a newer run took over
        if (!r.Cancelled) st.Result = r;
        if (OwnerOf(_active) != tab) return;
        if (r.Cancelled)
        {
            _sqlStatus.Text = "Cancelled";
            return;
        }
        ShowSqlResult(r);
    }

    private void ShowSqlResult(SqlResult? r)
    {
        var p = Palette.Current;
        _sqlError.Foreground = p.Error;
        _sqlError.IsVisible = r?.Error is not null;
        _sqlError.Text = r?.Error ?? "";
        var chartable = r is not null && SqlSchema.Chartable(r);
        _sqlChartBtn.IsEnabled = chartable;
        var chart = chartable && _sqlChartBtn.IsChecked == true;
        _sqlResults.Data = r?.Error is null ? r : null;
        _sqlChart.Data = chart ? r : null;
        _sqlChart.IsVisible = chart;
        _sqlScroll.IsVisible = !chart;
        _sqlLoadAll.IsVisible = r is { Capped: true };
        _sqlStatus.Text = r is null ? "" : SqlStatusText(r);
    }

    /// <summary>"12 rows · 4 ms · 2,000,000 of 8,695,680 entries loaded (the first ones) in 1.2 s".</summary>
    internal static string SqlStatusText(SqlResult r)
    {
        static string Ms(double ms) => ms >= 1000 ? (ms / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " s" : ms.ToString("0", CultureInfo.InvariantCulture) + " ms";
        var parts = new List<string>();
        if (r.Error is null) parts.Add($"{r.Rows.Count:N0}{(r.Truncated ? "+" : "")} {(r.Rows.Count == 1 && !r.Truncated ? "row" : "rows")} in {Ms(r.QueryMs)}");
        if (r.LoadMs >= 1) parts.Add($"table loaded in {Ms(r.LoadMs)}");
        parts.Add(r.Capped ? $"first {r.Loaded:N0} of {r.Total:N0} entries" : $"{r.Loaded:N0} entries");
        return string.Join("  ·  ", parts);
    }

    /// <summary>Selects the entry of a result row: by its line (and source), else by its row in the view.</summary>
    private void JumpToSqlRow(int index)
    {
        var tab = OwnerOf(_active);
        if (tab is null || SqlFor(tab)?.Result is not { } r || index < 0 || index >= r.Rows.Count) return;
        int Col(string name) => r.Columns.ToList().FindIndex(c => c.Equals(name, StringComparison.OrdinalIgnoreCase));
        var row = r.Rows[index];
        int? target = null;
        if (Col("line") is >= 0 and var li && row[li] is long line)
        {
            var src = 0;
            if (Col("source") is >= 0 and var si && row[si] is string name)
                src = Math.Max(0, tab.Info.Sources.ToList().FindIndex(s => s.DisplayName == name));
            target = tab.Doc.RowForLine(src, (int)line);
        }
        else if (Col("row") is >= 0 and var ri && row[ri] is long vr && vr < tab.Doc.ViewCount) target = (int)vr;
        if (target is not { } t) return;
        if (_grid != _gridMain) Activate(tab, focusGrid: false);
        _grid.Select(t, center: true);
    }

    internal bool SqlShownForTest => _sqlShown;
    internal void ToggleSqlForTest() => ToggleSql();
    internal void SetSqlForTest(string sql) => _sqlEditor.Text = sql;
    internal Task RunSqlForTest() => RunSqlAsync();
    internal SqlResult? SqlResultForTest => _sqlResults.Data;
    internal SqlResultsView SqlResultsViewForTest => _sqlResults;
    internal string SqlStatusForTest => _sqlStatus.Text ?? "";
    internal string SqlErrorForTest => _sqlError.IsVisible ? _sqlError.Text ?? "" : "";
    internal ToggleButton SqlChartButtonForTest => _sqlChartBtn;
    internal SqlChart SqlChartForTest => _sqlChart;
    internal IReadOnlyList<string> SqlExamplesForTest => _sqlExampleList.Select(x => x.Title).ToList();
    internal void PickSqlExampleForTest(int i) => _sqlExamples.SelectedIndex = i;
}
