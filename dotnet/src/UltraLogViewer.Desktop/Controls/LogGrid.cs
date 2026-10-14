using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using UltraLogViewer.Core;

namespace UltraLogViewer.Desktop.Controls;

public interface ILogGridHost
{
    AppSettings Settings { get; }
    IReadOnlyList<HighlightRule> Highlights { get; }
    /// <summary>The tab's columns in display order (format columns plus user-added fields).</summary>
    IReadOnlyList<ColumnDef> Columns(DocumentTab tab);
    double ColumnWidth(ColumnDef col);
    void SetColumnWidth(string key, double width);
    bool IsColumnHidden(string key);
    void ShowColumnMenu(Control anchor, ColumnDef? col);
    void ShowRowMenu(RowData row, ColumnDef? col);
    void SelectionChanged();
    void TopRowChanged();
    /// <summary>Return true if the key was handled.</summary>
    bool HandleKey(KeyEventArgs e);
    void ToggleBookmark(RowData row);
    void CopyRows(int from, int to);
}

/// <summary>
/// Virtualised, custom-drawn log table. Only rows on screen are fetched from the engine and drawn,
/// so tens of millions of rows scroll smoothly. Includes the header, a line-number gutter and an
/// overview ruler (warnings | errors | search hits, plus bookmarks) beside the scrollbar.
/// </summary>
public sealed class LogGrid : Control
{
    public const double HeaderHeight = 24;
    private const double GutterWidth = 74;
    private const double OverviewWidth = 14;
    private const double CellPad = 6;
    private const int WrapLines = 8;
    private const int ExpandLines = 40;

    private readonly ScrollBar _scroll;
    private readonly ILogGridHost _host;
    private readonly Dictionary<int, RowData> _cache = [];
    private DocumentTab? _tab;
    private int _gen;
    private string? _fetching;
    private byte[] _overview = [];
    private Regex? _searchRe;
    private List<(HighlightRule Rule, Regex Re)> _highlights = [];
    private bool _syncingScroll;
    private (ColumnDef Col, double StartX, double StartWidth)? _resizing;
    private bool _dragSelecting;
    private bool _overviewDragging;
    private string? _fetchingFocus;
    /// <summary>Rows laid out by the last paint, for hit-testing.</summary>
    private List<RowBox> _layout = [];
    /// <summary>Heights of wrapped or expanded rows, valid while <see cref="_heightSig"/> holds.</summary>
    private readonly Dictionary<string, double> _heights = [];
    private string _heightSig = "";
    private (ColumnDef? Col, double X, double Width) _msg;
    private double _lineHeight;

    private readonly record struct RowBox(int Row, double Y, double H, Rect Badge);

    public LogGrid(ILogGridHost host)
    {
        _host = host;
        Focusable = true;
        ClipToBounds = true;
        _scroll = new ScrollBar { Orientation = Orientation.Vertical, Minimum = 0, SmallChange = 1, AllowAutoHide = false };
        _scroll.ValueChanged += (_, e) =>
        {
            if (_syncingScroll || _tab is null) return;
            var top = (int)Math.Round(e.NewValue);
            if (top == _tab.TopRow) return;
            _tab.TopRow = top;
            _tab.Follow = top >= MaxTop;
            _host.TopRowChanged();
            InvalidateVisual();
        };
        VisualChildren.Add(_scroll);
        LogicalChildren.Add(_scroll);
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public DocumentTab? Tab => _tab;
    public int Count => _tab?.Info.ViewCount ?? 0;
    private AppSettings Settings => _host.Settings;
    public double RowHeight => Settings.Comfortable ? 26 : 20;
    private double FontSize => Settings.Comfortable ? 13.5 : 12.5;
    private double ViewHeight => Math.Max(0, Bounds.Height - HeaderHeight);
    /// <summary>Rows can differ in height (soft wrap on, or some entries expanded).</summary>
    private bool Variable => _tab is not null && (Settings.Wrap || _tab.ExpandedRows.Count > 0);

    /// <summary>Rows fully visible from the top row.</summary>
    public int VisibleRows => Variable ? FitCount(_tab!.TopRow) : Math.Max(1, (int)(ViewHeight / RowHeight));

    private int MaxTop
    {
        get
        {
            if (!Variable) return Math.Max(0, Count - VisibleRows);
            if (Count == 0) return 0;
            return Math.Min(Count - 1, TopForBottom(Count - 1));
        }
    }
    private double ScrollWidth => _scroll.DesiredSize.Width > 0 ? _scroll.DesiredSize.Width : 14;
    private double RowsWidth => Math.Max(0, Bounds.Width - ScrollWidth - OverviewWidth);

    // ------------------------------------------------------------------ state

    public void SetTab(DocumentTab? tab)
    {
        _tab = tab;
        _cache.Clear();
        _gen++;
        _overview = [];
        RefreshPatterns();
        SyncScroll();
        InvalidateVisual();
    }

    /// <summary>Drops cached rows (filter change, new data) and redraws.</summary>
    public void Invalidate()
    {
        _cache.Clear();
        _gen++;
        SyncScroll();
        InvalidateVisual();
    }

    /// <summary>Rows were appended (indexing in progress): keep the cache, just resize the scroll range.</summary>
    public void CountChanged()
    {
        SyncScroll();
        InvalidateVisual();
    }

    public void RefreshPatterns()
    {
        _searchRe = null;
        if (_tab is { SearchResult: not null } t && t.SearchPattern.Text.Length > 0)
        {
            try { _searchRe = Query.Compile(t.SearchPattern); } catch (ArgumentException) { }
        }
        _highlights = [];
        foreach (var h in _host.Highlights)
        {
            if (!h.Enabled || h.Text.Length == 0) continue;
            try { _highlights.Add((h, Query.Compile(h.Pattern))); } catch (ArgumentException) { }
        }
        InvalidateVisual();
    }

    public void SetOverview(byte[] overview)
    {
        _overview = overview;
        InvalidateVisual();
    }

    public RowData? GetCached(int row) => _cache.GetValueOrDefault(row);

    internal IReadOnlyList<(int Row, double Y, double H)> LayoutForTest => _layout.Select(b => (b.Row, b.Y, b.H)).ToList();
    internal string TimeTextForTest(int row) => _cache.TryGetValue(row, out var r) ? TimeText(r, row) : "";

    // -------------------------------------------------------------- scrolling

    public void SyncScroll()
    {
        if (_tab is null)
        {
            _scroll.Maximum = 0;
            return;
        }
        RefreshLayoutInfo();
        _tab.TopRow = Math.Clamp(_tab.TopRow, 0, MaxTop);
        _syncingScroll = true;
        _scroll.Maximum = MaxTop;
        _scroll.ViewportSize = VisibleRows;
        _scroll.LargeChange = Math.Max(1, VisibleRows - 1);
        _scroll.Value = _tab.TopRow;
        _syncingScroll = false;
    }

    public void ScrollTo(int top)
    {
        if (_tab is null) return;
        _tab.TopRow = Math.Clamp(top, 0, MaxTop);
        _tab.Follow = _tab.TopRow >= MaxTop;
        SyncScroll();
        _host.TopRowChanged();
        InvalidateVisual();
    }

    public void Reveal(int row, bool center = false)
    {
        if (_tab is null) return;
        if (Variable)
        {
            RefreshLayoutInfo();
            if (center) ScrollTo(TopForBottom(row, (ViewHeight + RowHeightOf(row)) / 2));
            else if (row < _tab.TopRow) ScrollTo(row);
            else if (row >= _tab.TopRow + VisibleRows) ScrollTo(TopForBottom(row));
            else InvalidateVisual();
            return;
        }
        var vis = VisibleRows;
        if (center) ScrollTo(row - vis / 2);
        else if (row < _tab.TopRow) ScrollTo(row);
        else if (row >= _tab.TopRow + vis) ScrollTo(row - vis + 1);
        else InvalidateVisual();
    }

    public void Select(int row, bool extend = false, bool center = false)
    {
        if (_tab is null || Count == 0) return;
        row = Math.Clamp(row, 0, Count - 1);
        _tab.Focus = row;
        if (!extend) _tab.Anchor = row;
        Reveal(row, center);
        _host.SelectionChanged();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (_tab is null) return;
        var lines = -e.Delta.Y * 3;
        var step = Math.Sign(lines) * Math.Max(1, (int)Math.Round(Math.Abs(lines)));
        ScrollTo(_tab.TopRow + step);
        e.Handled = true;
    }

    // ----------------------------------------------------------------- layout

    protected override Size MeasureOverride(Size availableSize)
    {
        _scroll.Measure(new Size(double.PositiveInfinity, Math.Max(0, availableSize.Height - HeaderHeight)));
        return new Size(double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 300 : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var w = _scroll.DesiredSize.Width;
        _scroll.Arrange(new Rect(finalSize.Width - w, HeaderHeight, w, Math.Max(0, finalSize.Height - HeaderHeight)));
        Dispatcher.UIThread.Post(SyncScroll);
        return finalSize;
    }

    // ---------------------------------------------------------- row heights

    /// <summary>Re-reads the message column and drops cached heights when width, density or wrap changed.</summary>
    private void RefreshLayoutInfo()
    {
        var cols = Columns();
        var x = GutterWidth;
        _msg = (null, GutterWidth, 0);
        foreach (var (col, w) in cols)
        {
            if (col.Key == "message" || (_msg.Col is null && w > 0 && _host.ColumnWidth(col) == 0)) _msg = (col, x, w);
            x += w;
        }
        if (_msg.Col is null && cols.Count > 0) _msg = (cols[^1].Col, x - cols[^1].Width, cols[^1].Width);
        var sig = $"{_msg.Col?.Key}|{_msg.Width:0}|{RowsWidth:0}|{Settings.Wrap}|{Settings.Density}";
        if (sig == _heightSig) return;
        _heightSig = sig;
        _heights.Clear();
        _lineHeight = Cell("Xg", Palette.Current.Fg).Height;
    }

    /// <summary>Call after a display setting that changes row heights.</summary>
    public void LayoutChanged()
    {
        _heightSig = "";
        _heights.Clear();
        SyncScroll();
        InvalidateVisual();
    }

    private double RowHeightOf(int r) => !Variable ? RowHeight : _cache.TryGetValue(r, out var row) ? RowHeightOf(row) : RowHeight;

    private double RowHeightOf(RowData row)
    {
        var wrap = Settings.Wrap;
        var exp = IsExpanded(row);
        if (!wrap && !exp) return RowHeight;
        if (_heightSig.Length == 0) RefreshLayoutInfo();
        if (_heights.TryGetValue(row.Key, out var h)) return h;
        if (_heights.Count > 20_000) _heights.Clear();
        h = RowHeight;
        if (wrap && _msg.Col is { } mc) h += Math.Max(0, MessageText(row, mc, Palette.Current.Fg).Height - _lineHeight);
        if (exp) h += Cell(Expansion(row).Text, Palette.Current.Fg).Height + 4;
        _heights[row.Key] = h;
        return h;
    }

    private int FitCount(int top)
    {
        var view = ViewHeight;
        double acc = 0;
        var n = 0;
        for (var r = top; r < Count; r++)
        {
            var h = RowHeightOf(r);
            if (acc + h > view && n > 0) break;
            acc += h;
            n++;
            if (acc >= view) break;
        }
        return Math.Max(1, n);
    }

    /// <summary>The top row that puts <paramref name="row"/> at the bottom of the viewport.</summary>
    private int TopForBottom(int row, double? space = null)
    {
        var view = space ?? ViewHeight;
        var acc = RowHeightOf(row);
        var r = row;
        while (r > 0)
        {
            var h = RowHeightOf(r - 1);
            if (acc + h > view) break;
            acc += h;
            r--;
        }
        return r;
    }

    public bool IsExpanded(RowData row) => row.LineCount > 1 && _tab is not null && _tab.ExpandedRows.Contains(row.Key);

    public void SetExpanded(RowData row, bool on)
    {
        if (_tab is null || row.LineCount <= 1) return;
        if (on ? !_tab.ExpandedRows.Add(row.Key) : !_tab.ExpandedRows.Remove(row.Key)) return;
        _heights.Remove(row.Key);
        SyncScroll();
        if (on && row.Row >= _tab.TopRow && row.Row < Count)
        {
            // keep as much of the expansion on screen as fits
            var top = TopForBottom(row.Row);
            if (top > _tab.TopRow) ScrollTo(Math.Min(top, row.Row));
        }
        InvalidateVisual();
    }

    /// <summary>Lines 2.. of a multi-line entry, at most <see cref="ExpandLines"/>, plus a "more" line.</summary>
    private static (string Text, int MoreAt) Expansion(RowData row)
    {
        var lines = row.Text.Replace("\r\n", "\n").Split('\n');
        var rest = lines.Skip(1).Take(ExpandLines).Select(l => l.Length > 2000 ? l[..2000] + "…" : l);
        var text = string.Join('\n', rest);
        var more = lines.Length - 1 - ExpandLines;
        if (more <= 0) return (text, -1);
        return (text + $"\n… {more:N0} more lines", text.Length + 1);
    }

    // ---------------------------------------------------------------- columns

    private List<(ColumnDef Col, double Width)> Columns()
    {
        if (_tab is null) return [];
        var all = _host.Columns(_tab);
        var cols = all.Where(c => !_host.IsColumnHidden(c.Key)).Select(c => (c, _host.ColumnWidth(c))).ToList();
        if (cols.Count == 0) cols = all.Select(c => (c, _host.ColumnWidth(c))).ToList();
        if (cols.Count > 0 && !cols.Any(c => c.Item2 == 0)) cols[^1] = (cols[^1].c, 0);
        // Resolve the stretching column's width.
        var fixedWidth = GutterWidth + cols.Sum(c => c.Item2);
        var flex = Math.Max(80, RowsWidth - fixedWidth);
        return cols.Select(c => (c.c, c.Item2 == 0 ? flex : c.Item2)).ToList();
    }

    // -------------------------------------------------------------- fetching

    private void EnsureRows(int from, int to)
    {
        var tab = _tab;
        if (tab is null) return;
        var missing = false;
        for (var r = Math.Max(0, from - 1); r < to && !missing; r++) missing = !_cache.ContainsKey(r);
        if (!missing) return;
        var start = Math.Max(0, from - 80);
        var end = Math.Min(Count, to + 80);
        var key = $"{start}:{end}:{_gen}";
        if (_fetching == key) return;
        _fetching = key;
        var gen = _gen;
        var doc = tab.Doc;
        Task.Run(() => doc.GetRows(start, end - start)).ContinueWith(t =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_fetching == key) _fetching = null;
                if (t.IsFaulted || gen != _gen || _tab != tab) return;
                if (_cache.Count > 5000) _cache.Clear();
                foreach (var r in t.Result) _cache[r.Row] = r;
                if (Variable)
                {
                    // heights are known now: keep the tail pinned while following
                    if (tab.Follow) tab.TopRow = MaxTop;
                    SyncScroll();
                }
                InvalidateVisual();
                _host.TopRowChanged();
            });
        }, TaskScheduler.Default);
    }

    /// <summary>Fetches the focused row on its own (relative time mode needs its time while it is off screen).</summary>
    private void EnsureFocusRow()
    {
        var tab = _tab;
        if (tab is null || tab.Focus >= Count || _cache.ContainsKey(tab.Focus)) return;
        var key = $"{tab.Focus}:{_gen}";
        if (_fetchingFocus == key) return;
        _fetchingFocus = key;
        var gen = _gen;
        var focus = tab.Focus;
        Task.Run(() => tab.Doc.GetRows(focus, 1)).ContinueWith(t =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (t.IsFaulted || gen != _gen || _tab != tab) return;
                foreach (var r in t.Result) _cache[r.Row] = r;
                InvalidateVisual();
            });
        }, TaskScheduler.Default);
    }

    // ----------------------------------------------------------------- render

    public override void Render(DrawingContext ctx)
    {
        var p = Palette.Current;
        var bounds = new Rect(Bounds.Size);
        ctx.FillRectangle(p.Bg, bounds);
        var tab = _tab;
        if (tab is null) return;
        var cols = Columns();
        RefreshLayoutInfo();
        RenderHeader(ctx, p, cols);
        var from = tab.TopRow;
        var layout = new List<RowBox>();
        var y0 = HeaderHeight;
        for (var r = from; r < Count && y0 < Bounds.Height; r++)
        {
            var h = RowHeightOf(r);
            layout.Add(new RowBox(r, y0, h, default));
            y0 += h;
        }
        var to = from + layout.Count;
        EnsureRows(from, to);
        if (Settings.TimeMode == "relative") EnsureFocusRow();
        var rowsRect = new Rect(0, HeaderHeight, RowsWidth, ViewHeight);
        using (ctx.PushClip(rowsRect))
        {
            var selLo = tab.SelectionStart;
            var selHi = tab.SelectionEnd;
            var hits = tab.SearchResult?.Rows;
            var curHit = hits is { Length: > 0 } ? hits[Math.Min(tab.SearchCurrent, hits.Length - 1)] : -1;
            for (var i = 0; i < layout.Count; i++)
            {
                var (r, y, h, _) = layout[i];
                _cache.TryGetValue(r, out var row);
                var badge = RenderRow(ctx, p, cols, r, y, h, row, r >= selLo && r <= selHi, r == tab.Focus, hits is not null && Array.BinarySearch(hits, r) >= 0, r == curHit);
                layout[i] = layout[i] with { Badge = badge };
            }
        }
        _layout = layout;
        RenderOverview(ctx, p);
    }

    private static FormattedText Text(string s, IBrush brush, Typeface? face, double size) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face ?? Fonts.MonoFace, size, brush);

    private FormattedText Cell(string s, IBrush brush, Typeface? face = null) => Text(s, brush, face, FontSize);

    private string CellText(RowData row, ColumnDef col, int r)
    {
        string v;
        if (col.Key == "__source") v = _tab!.Info.Sources.Count > row.Source ? _tab.Info.Sources[row.Source].DisplayName : "";
        else if (col.Key == "time") v = TimeText(row, r);
        else v = row.Field(col.Key);
        var nl = v.IndexOf('\n');
        if (nl >= 0) v = v[..nl];
        if (v.Length > 2000) v = v[..2000] + "…";
        return v;
    }

    /// <summary>The Time cell in the chosen mode; rows without a parsed time show the text as written.</summary>
    private string TimeText(RowData row, int r)
    {
        var raw = row.Field("time");
        if (row.Time is not long t) return raw;
        var s = Settings;
        switch (s.TimeMode)
        {
            case "full": return Timestamps.Format(t, s.TimeUtc);
            case "time": return Timestamps.FormatTimeOfDay(t, s.TimeUtc);
            case "relative":
                return _cache.GetValueOrDefault(_tab!.Focus)?.Time is long f ? Timestamps.FormatSigned(t - f) : "";
            case "delta":
                return r > 0 && _cache.GetValueOrDefault(r - 1)?.Time is long pt ? Timestamps.FormatSigned(t - pt) : "";
            default: return raw;
        }
    }

    private FormattedText MessageText(RowData row, ColumnDef col, IBrush fg)
    {
        var t = Cell(CellText(row, col, row.Row), fg);
        t.MaxTextWidth = Math.Max(1, _msg.Width - CellPad * 2);
        t.MaxLineCount = Settings.Wrap ? WrapLines : 1;
        t.Trimming = TextTrimming.CharacterEllipsis;
        return t;
    }

    private void RenderHeader(DrawingContext ctx, Palette p, List<(ColumnDef Col, double Width)> cols)
    {
        ctx.FillRectangle(p.Bg2, new Rect(0, 0, Bounds.Width, HeaderHeight));
        ctx.DrawLine(new Pen(p.Border), new Point(0, HeaderHeight - 0.5), new Point(Bounds.Width, HeaderHeight - 0.5));
        var face = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
        var line = Text("Line", p.Fg2, face, 11.5);
        ctx.DrawText(line, new Point(GutterWidth - CellPad - line.Width, (HeaderHeight - line.Height) / 2));
        var x = GutterWidth;
        foreach (var (col, w) in cols)
        {
            var t = Text(col.Title, p.Fg2, face, 11.5);
            t.MaxTextWidth = Math.Max(1, w - CellPad * 2);
            t.MaxLineCount = 1;
            t.Trimming = TextTrimming.CharacterEllipsis;
            ctx.DrawText(t, new Point(x + CellPad, (HeaderHeight - t.Height) / 2));
            x += w;
            ctx.DrawLine(new Pen(p.Border), new Point(x - 0.5, 4), new Point(x - 0.5, HeaderHeight - 4));
        }
    }

    /// <summary>Draws one row; returns the rect of its "+N" badge (empty when it has none).</summary>
    private Rect RenderRow(DrawingContext ctx, Palette p, List<(ColumnDef Col, double Width)> cols, int r, double y, double h, RowData? row, bool selected, bool focus, bool isHit, bool isCurHit)
    {
        var rect = new Rect(0, y, RowsWidth, h);
        if (r % 2 == 1) ctx.FillRectangle(p.RowAlt, rect);
        if (row is null) return default;
        var s = Settings;
        var rowH = RowHeight;
        IBrush fg;
        IBrush? bg = null;
        IBrush levelFg;
        if (row.IsContext) fg = levelFg = p.Context;
        else if (s.TintRows)
        {
            fg = levelFg = p.LevelFg(row.Level);
            bg = p.LevelBg(row.Level);
        }
        else
        {
            fg = row.Level is Level.Trace or Level.Debug ? p.LevelFg(row.Level) : p.Fg;
            levelFg = row.Level == Level.Fatal ? p.FatalBg : p.LevelFg(row.Level);
        }
        foreach (var (rule, re) in _highlights)
        {
            if (!rule.Row || !SafeIsMatch(re, row.Text)) continue;
            bg = new ImmutableSolidColorBrush(Color.Parse(rule.Color), 0.22);
            break;
        }
        if (selected)
        {
            bg = p.Sel;
            fg = levelFg = p.SelFg;
        }
        if (bg is not null) ctx.FillRectangle(bg, rect);
        if (focus) ctx.FillRectangle(p.Accent, new Rect(0, y, 2, h));
        if (!s.TintRows && !row.IsContext && p.LevelStripe(row.Level) is { } stripe)
            ctx.FillRectangle(stripe, new Rect(GutterWidth - 3, y, 3, h));

        // gutter: bookmark marker, "+N" continuation-line badge, line number
        var bookmarked = _tab!.Bookmarks.ContainsKey(row.Key);
        if (bookmarked) ctx.FillRectangle(p.Bookmark, new Rect(4, y + (rowH - 9) / 2, 9, 9), 2);
        var ln = Cell(row.Line.ToString(CultureInfo.InvariantCulture), isHit ? p.HitCur : selected ? fg : p.Fg3, isHit ? Fonts.MonoBold : Fonts.MonoFace);
        var lx = GutterWidth - CellPad - ln.Width;
        ctx.DrawText(ln, new Point(lx, y + (rowH - ln.Height) / 2));
        var badge = default(Rect);
        var expanded = IsExpanded(row);
        if (row.LineCount > 1)
        {
            var more = Text($"{(expanded ? "−" : "+")}{row.LineCount - 1}", p.Accent, expanded ? Fonts.MonoBold : Fonts.MonoFace, 10);
            var bx = lx - more.Width - 3;
            ctx.DrawText(more, new Point(bx, y + (rowH - more.Height) / 2));
            badge = new Rect(bx - 2, y, more.Width + 4, rowH);
        }

        // gap marker: a dashed line on top of a row that comes long after the previous one
        if (s.GapMs > 0 && r > 0 && row.Time is long t && _cache.GetValueOrDefault(r - 1)?.Time is long pt && t - pt >= s.GapMs)
        {
            ctx.DrawLine(new Pen(p.Warn, 1, DashStyle.Dash), new Point(0, y + 0.5), new Point(RowsWidth, y + 0.5));
            var gap = Text(Timestamps.FormatGap(t - pt), p.Warn, Fonts.MonoFace, 9.5);
            var left = badge == default ? lx : badge.X;
            if (!bookmarked && 2 + gap.Width + 4 < left) ctx.DrawText(gap, new Point(2, y + (rowH - gap.Height) / 2));
        }

        var tokens = s.TokenColors && !selected && !row.IsContext;
        var x = GutterWidth;
        foreach (var (col, w) in cols)
        {
            var isMsg = col == _msg.Col;
            var v = CellText(row, col, r);
            if (v.Length > 0)
            {
                var cell = new Rect(x, y, w, h);
                using (ctx.PushClip(cell))
                {
                    FormattedText ft;
                    if (isMsg) ft = MessageText(row, col, fg);
                    else
                    {
                        var brush = col.Key == "level" ? levelFg : col.Key == "__source" && !selected ? p.Fg2 : fg;
                        ft = Cell(v, brush, col.Key == "level" ? Fonts.MonoBold : Fonts.MonoFace);
                        ft.MaxTextWidth = Math.Max(1, w - CellPad * 2);
                        ft.MaxLineCount = 1;
                        ft.Trimming = TextTrimming.CharacterEllipsis;
                    }
                    var origin = new Point(x + CellPad, y + (rowH - _lineHeight) / 2);
                    if (col.Key != "level") DrawMarks(ctx, p, ft, v, origin, isCurHit, tokens && isMsg);
                    ctx.DrawText(ft, origin);
                }
            }
            x += w;
        }

        if (expanded)
        {
            var (text, moreAt) = Expansion(row);
            var et = Cell(text, fg);
            var ex = (_msg.Col is null ? GutterWidth : _msg.X) + CellPad;
            var ey = y + h - 4 - et.Height;
            if (moreAt >= 0) et.SetForegroundBrush(p.Fg3, moreAt, text.Length - moreAt);
            using (ctx.PushClip(new Rect(ex - CellPad, ey, Math.Max(0, RowsWidth - ex + CellPad), et.Height)))
            {
                ctx.DrawLine(new Pen(p.Border, 2), new Point(ex - 4, ey), new Point(ex - 4, ey + et.Height));
                DrawMarks(ctx, p, et, moreAt >= 0 ? text[..moreAt] : text, new Point(ex, ey), isCurHit, tokens);
                ctx.DrawText(et, new Point(ex, ey));
            }
        }
        return badge;
    }

    private static bool SafeIsMatch(Regex re, string s)
    {
        try { return re.IsMatch(s); } catch (RegexMatchTimeoutException) { return false; }
    }

    /// <summary>Token colours first, then highlight rules color and embolden the text; search hits get a background mark.</summary>
    private void DrawMarks(DrawingContext ctx, Palette p, FormattedText t, string text, Point origin, bool current, bool tokens = false)
    {
        if (tokens)
        {
            foreach (var tk in Tokenizer.Tokenize(text))
            {
                t.SetForegroundBrush(p.Token(tk.Kind), tk.Start, tk.Length);
                if (tk.Kind == TokenKind.Exception) t.SetFontWeight(FontWeight.SemiBold, tk.Start, tk.Length);
            }
        }
        foreach (var (rule, re) in _highlights)
        {
            if (rule.Row) continue;
            var brush = new ImmutableSolidColorBrush(Color.Parse(rule.Color));
            foreach (var (s, len) in Query.Ranges(re, text, 100))
            {
                t.SetForegroundBrush(brush, s, len);
                t.SetFontWeight(FontWeight.SemiBold, s, len);
            }
        }
        if (_searchRe is null) return;
        foreach (var (s, len) in Query.Ranges(_searchRe, text, 200))
        {
            var geo = t.BuildHighlightGeometry(origin, s, len);
            if (geo is not null) ctx.DrawGeometry(current ? p.HitCur : p.Hit, null, geo);
        }
    }

    private void RenderOverview(DrawingContext ctx, Palette p)
    {
        var x0 = Bounds.Width - ScrollWidth - OverviewWidth;
        var top = HeaderHeight;
        var h = Bounds.Height - HeaderHeight;
        if (h <= 0) return;
        ctx.FillRectangle(p.Bg2, new Rect(x0, top, OverviewWidth, h));
        ctx.DrawLine(new Pen(p.Border), new Point(x0 + 0.5, top), new Point(x0 + 0.5, top + h));
        var n = Count;
        if (_tab is null || n == 0) return;
        var lane = OverviewWidth / 3;
        if (_overview.Length > 0)
        {
            var bh = h / _overview.Length;
            for (var i = 0; i < _overview.Length; i++)
            {
                var f = _overview[i];
                if ((f & 1) != 0) ctx.FillRectangle(p.Warn, new Rect(x0, top + i * bh, lane, Math.Max(1, bh)));
                if ((f & 2) != 0) ctx.FillRectangle(p.Error, new Rect(x0 + lane, top + i * bh, lane, Math.Max(1, bh)));
            }
        }
        if (_tab.SearchResult is { Rows.Length: > 0 } sr)
        {
            var lastY = -2.0;
            foreach (var r in sr.Rows)
            {
                var y = Math.Floor((double)r / n * h);
                if (y == lastY) continue;
                lastY = y;
                ctx.FillRectangle(p.HitCur, new Rect(x0 + lane * 2, top + y, lane, 2));
            }
        }
        foreach (var b in _tab.Bookmarks.Values)
            if (b.Row is int br) ctx.FillRectangle(p.Bookmark, new Rect(x0, top + Math.Floor((double)br / n * h), OverviewWidth, 2));
        var vy = (double)_tab.TopRow / n * h;
        var vh = Math.Max(4, (double)VisibleRows / n * h);
        ctx.DrawRectangle(new Pen(p.Fg3), new Rect(x0 + 0.5, top + vy + 0.5, OverviewWidth - 1, Math.Min(h - vy - 1, vh)));
    }

    // ------------------------------------------------------------------ input

    private int? RowAt(Point pt)
    {
        if (_tab is null || pt.Y < HeaderHeight || pt.X > RowsWidth) return null;
        foreach (var b in _layout)
            if (pt.Y >= b.Y && pt.Y < b.Y + b.H) return b.Row < Count ? b.Row : null;
        return null;
    }

    private ColumnDef? ColumnAt(double x)
    {
        var cx = GutterWidth;
        foreach (var (col, w) in Columns())
        {
            if (x >= cx && x < cx + w) return col;
            cx += w;
        }
        return null;
    }

    private (ColumnDef Col, double Right)? ResizeHandleAt(double x)
    {
        var cx = GutterWidth;
        foreach (var (col, w) in Columns())
        {
            cx += w;
            if (Math.Abs(x - cx) <= 4 && _host.ColumnWidth(col) > 0) return (col, cx);
        }
        return null;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var pt = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;
        if (pt.Y < HeaderHeight)
        {
            if (props.IsRightButtonPressed)
            {
                _host.ShowColumnMenu(this, ColumnAt(pt.X));
            }
            else if (ResizeHandleAt(pt.X) is { } h)
            {
                var cols = Columns();
                var w = cols.First(c => c.Col == h.Col).Width;
                _resizing = (h.Col, pt.X, w);
                e.Pointer.Capture(this);
            }
            e.Handled = true;
            return;
        }
        if (pt.X >= Bounds.Width - ScrollWidth - OverviewWidth && pt.X < Bounds.Width - ScrollWidth)
        {
            _overviewDragging = true;
            JumpOverview(pt.Y);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }
        var row = RowAt(pt);
        if (row is not null && _tab is not null && props.IsRightButtonPressed)
        {
            if (row < _tab.SelectionStart || row > _tab.SelectionEnd) Select(row.Value);
            if (_cache.TryGetValue(row.Value, out var clicked)) _host.ShowRowMenu(clicked, ColumnAt(pt.X));
            e.Handled = true;
            return;
        }
        if (row is null || _tab is null || !props.IsLeftButtonPressed) return;
        if (e.ClickCount == 1 && _layout.FirstOrDefault(b => b.Row == row).Badge.Contains(pt) && _cache.TryGetValue(row.Value, out var multi))
        {
            SetExpanded(multi, !IsExpanded(multi));
            e.Handled = true;
            return;
        }
        if (e.ClickCount == 2)
        {
            if (_cache.TryGetValue(row.Value, out var rd)) _host.ToggleBookmark(rd);
            e.Handled = true;
            return;
        }
        if (pt.X < 16 && _cache.TryGetValue(row.Value, out var marked) && _tab.Bookmarks.ContainsKey(marked.Key)) _host.ToggleBookmark(marked);
        var extend = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        _tab.Focus = row.Value;
        if (!extend) _tab.Anchor = row.Value;
        _dragSelecting = true;
        e.Pointer.Capture(this);
        InvalidateVisual();
        _host.SelectionChanged();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pt = e.GetPosition(this);
        if (_resizing is { } rs)
        {
            _host.SetColumnWidth(rs.Col.Key, Math.Max(30, rs.StartWidth + pt.X - rs.StartX));
            InvalidateVisual();
            return;
        }
        if (_overviewDragging)
        {
            JumpOverview(pt.Y);
            return;
        }
        if (_dragSelecting && _tab is not null)
        {
            var y = Math.Clamp(pt.Y, HeaderHeight, Bounds.Height - 1);
            var r = RowAt(new Point(0, y)) ?? (_layout.Count > 0 ? _layout[^1].Row : _tab.TopRow);
            if (pt.Y < HeaderHeight) r = Math.Max(0, _tab.TopRow - 1);
            else if (pt.Y >= Bounds.Height - 1) r = Math.Min(Count - 1, r + 1);
            if (r != _tab.Focus && r >= 0)
            {
                _tab.Focus = r;
                Reveal(r);
                _host.SelectionChanged();
            }
            return;
        }
        Cursor = pt.Y < HeaderHeight && ResizeHandleAt(pt.X) is not null ? new Cursor(StandardCursorType.SizeWestEast) : Cursor.Default;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _resizing = null;
        _dragSelecting = false;
        _overviewDragging = false;
        e.Pointer.Capture(null);
    }

    private void JumpOverview(double y)
    {
        var h = Bounds.Height - HeaderHeight;
        var f = Math.Clamp((y - HeaderHeight) / h, 0, 1);
        ScrollTo((int)(f * Count - VisibleRows / 2.0));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var tab = _tab;
        if (tab is null || e.Handled) return;
        if (_host.HandleKey(e))
        {
            e.Handled = true;
            return;
        }
        var page = VisibleRows - 1;
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var mod = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        switch (e.Key)
        {
            case Key.Down: Select(tab.Focus + 1, shift); break;
            case Key.Up: Select(tab.Focus - 1, shift); break;
            case Key.PageDown: Select(tab.Focus + page, shift); break;
            case Key.PageUp: Select(tab.Focus - page, shift); break;
            case Key.Home: Select(0, shift); break;
            case Key.Right when !shift && !mod:
            case Key.Left when !shift && !mod:
                if (_cache.TryGetValue(tab.Focus, out var fr)) SetExpanded(fr, e.Key == Key.Right);
                break;
            case Key.End:
                Select(Count - 1, shift);
                tab.Follow = true;
                _host.TopRowChanged();
                break;
            case Key.C when mod: _host.CopyRows(tab.SelectionStart, tab.SelectionEnd); break;
            case Key.A when mod:
                tab.Anchor = 0;
                tab.Focus = Math.Max(0, Count - 1);
                InvalidateVisual();
                _host.SelectionChanged();
                break;
            default: return;
        }
        e.Handled = true;
    }
}
