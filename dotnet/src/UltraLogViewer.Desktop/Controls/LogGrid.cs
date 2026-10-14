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
    public const double RowHeight = 20;
    private const double GutterWidth = 74;
    private const double OverviewWidth = 14;
    private const double CellPad = 6;
    private const double FontSize = 12.5;

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
    public int VisibleRows => Math.Max(1, (int)((Bounds.Height - HeaderHeight) / RowHeight));
    private int MaxTop => Math.Max(0, Count - VisibleRows);
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

    // -------------------------------------------------------------- scrolling

    public void SyncScroll()
    {
        if (_tab is null)
        {
            _scroll.Maximum = 0;
            return;
        }
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
        for (var r = from; r < to && !missing; r++) missing = !_cache.ContainsKey(r);
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
                InvalidateVisual();
                _host.TopRowChanged();
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
        RenderHeader(ctx, p, cols);
        var vis = VisibleRows + 1;
        var from = tab.TopRow;
        var to = Math.Min(Count, from + vis);
        EnsureRows(from, to);
        var rowsRect = new Rect(0, HeaderHeight, RowsWidth, Math.Max(0, Bounds.Height - HeaderHeight));
        using (ctx.PushClip(rowsRect))
        {
            var selLo = tab.SelectionStart;
            var selHi = tab.SelectionEnd;
            var hits = tab.SearchResult?.Rows;
            var curHit = hits is { Length: > 0 } ? hits[Math.Min(tab.SearchCurrent, hits.Length - 1)] : -1;
            for (var r = from; r < to; r++)
            {
                var y = HeaderHeight + (r - from) * RowHeight;
                _cache.TryGetValue(r, out var row);
                RenderRow(ctx, p, cols, r, y, row, r >= selLo && r <= selHi, r == tab.Focus, hits is not null && Array.BinarySearch(hits, r) >= 0, r == curHit);
            }
        }
        RenderOverview(ctx, p);
    }

    private static FormattedText Text(string s, IBrush brush, Typeface? face = null, double size = FontSize) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face ?? Fonts.MonoFace, size, brush);

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

    private void RenderRow(DrawingContext ctx, Palette p, List<(ColumnDef Col, double Width)> cols, int r, double y, RowData? row, bool selected, bool focus, bool isHit, bool isCurHit)
    {
        var rect = new Rect(0, y, RowsWidth, RowHeight);
        if (r % 2 == 1) ctx.FillRectangle(p.RowAlt, rect);
        if (row is null) return;
        IBrush fg = row.IsContext ? p.Context : p.LevelFg(row.Level);
        IBrush? bg = row.IsContext ? null : p.LevelBg(row.Level);
        foreach (var (rule, re) in _highlights)
        {
            if (!rule.Row || !SafeIsMatch(re, row.Text)) continue;
            bg = new ImmutableSolidColorBrush(Color.Parse(rule.Color), 0.22);
            break;
        }
        if (selected)
        {
            bg = p.Sel;
            fg = p.SelFg;
        }
        if (bg is not null) ctx.FillRectangle(bg, rect);
        if (focus) ctx.FillRectangle(p.Accent, new Rect(0, y, 2, RowHeight));

        // gutter: bookmark marker, "+N" continuation-line badge, line number
        if (_tab!.Bookmarks.ContainsKey(row.Key)) ctx.FillRectangle(p.Bookmark, new Rect(4, y + 5, 9, 9), 2);
        var ln = Text(row.Line.ToString(CultureInfo.InvariantCulture), isHit ? p.HitCur : selected ? fg : p.Fg3, isHit ? Fonts.MonoBold : Fonts.MonoFace);
        var lx = GutterWidth - CellPad - ln.Width;
        ctx.DrawText(ln, new Point(lx, y + (RowHeight - ln.Height) / 2));
        if (row.LineCount > 1)
        {
            var more = Text($"+{row.LineCount - 1}", p.Accent, Fonts.MonoFace, 10);
            ctx.DrawText(more, new Point(lx - more.Width - 3, y + (RowHeight - more.Height) / 2));
        }

        var x = GutterWidth;
        foreach (var (col, w) in cols)
        {
            string v;
            if (col.Key == "__source") v = _tab.Info.Sources.Count > row.Source ? _tab.Info.Sources[row.Source].DisplayName : "";
            else v = row.Field(col.Key);
            var nl = v.IndexOf('\n');
            if (nl >= 0) v = v[..nl];
            if (v.Length > 2000) v = v[..2000] + "…";
            if (v.Length > 0)
            {
                var cell = new Rect(x, y, w, RowHeight);
                using (ctx.PushClip(cell))
                {
                    var t = Text(v, col.Key == "__source" && !selected ? p.Fg2 : fg, col.Key == "level" ? Fonts.MonoBold : Fonts.MonoFace);
                    t.MaxTextWidth = Math.Max(1, w - CellPad * 2);
                    t.MaxLineCount = 1;
                    t.Trimming = TextTrimming.CharacterEllipsis;
                    var origin = new Point(x + CellPad, y + (RowHeight - t.Height) / 2);
                    if (col.Key != "level") DrawMarks(ctx, p, t, v, origin, isCurHit);
                    ctx.DrawText(t, origin);
                }
            }
            x += w;
        }
    }

    private static bool SafeIsMatch(Regex re, string s)
    {
        try { return re.IsMatch(s); } catch (RegexMatchTimeoutException) { return false; }
    }

    /// <summary>Search hits get a background mark; highlight rules color and embolden the text.</summary>
    private void DrawMarks(DrawingContext ctx, Palette p, FormattedText t, string text, Point origin, bool current)
    {
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
        var r = _tab.TopRow + (int)((pt.Y - HeaderHeight) / RowHeight);
        return r < Count ? r : null;
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
            var r = Math.Min(Count - 1, _tab.TopRow + (int)((y - HeaderHeight) / RowHeight));
            if (pt.Y < HeaderHeight) r = Math.Max(0, _tab.TopRow - 1);
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
