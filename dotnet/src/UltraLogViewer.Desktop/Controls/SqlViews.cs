using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using UltraLogViewer.Core;

namespace UltraLogViewer.Desktop.Controls;

/// <summary>
/// The SQL results grid: a header row and the result rows, drawn (only the visible part) inside a ScrollViewer. Click
/// or arrow keys select a row (<see cref="RowSelected"/>), which the window turns into a jump to that entry.
/// </summary>
public sealed class SqlResultsView : Control
{
    public const double RowH = 22;
    private const double CharW = 7.3;
    private SqlResult? _data;
    private double[] _x = [0];
    private ScrollViewer? _scroll;

    public SqlResultsView()
    {
        Focusable = true;
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public event Action<int>? RowSelected;
    public int Selected { get; private set; } = -1;

    public SqlResult? Data
    {
        get => _data;
        set
        {
            _data = value;
            Selected = -1;
            var cols = value?.Columns ?? [];
            _x = new double[cols.Count + 1];
            for (var c = 0; c < cols.Count; c++)
            {
                var chars = cols[c].Length;
                if (value is not null)
                    for (var r = 0; r < Math.Min(300, value.Rows.Count); r++) chars = Math.Max(chars, Cell(value.Rows[r][c]).Length);
                _x[c + 1] = _x[c] + Math.Clamp(chars * CharW + 18, 52, 440);
            }
            Width = Math.Max(1, _x[^1]);
            Height = RowH * (1 + (value?.Rows.Count ?? 0));
            if (_scroll is not null) _scroll.Offset = new Vector(_scroll.Offset.X, 0);
            InvalidateVisual();
        }
    }

    /// <summary>How a value shows in a cell: NULL, numbers in invariant culture, the first line of a text, a short hex for blobs.</summary>
    public static string Cell(object? v)
    {
        switch (v)
        {
            case null: return "NULL";
            case double d: return d.ToString("0.######", CultureInfo.InvariantCulture);
            case long l: return l.ToString(CultureInfo.InvariantCulture);
            case byte[] b: return "x'" + Convert.ToHexString(b, 0, Math.Min(b.Length, 32)) + (b.Length > 32 ? "…" : "") + "'";
            default:
                var s = Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
                var nl = s.IndexOf('\n');
                if (nl >= 0) s = s[..nl].TrimEnd('\r') + " …";
                return s.Length > 300 ? s[..300] + "…" : s;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _scroll = this.FindAncestorOfType<ScrollViewer>();
        if (_scroll is not null) _scroll.ScrollChanged += (_, _) => InvalidateVisual();
    }

    private static FormattedText T(string s, IBrush b, Typeface? face = null) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face ?? Fonts.MonoFace, 12, b);

    public override void Render(DrawingContext ctx)
    {
        var p = Palette.Current;
        ctx.FillRectangle(p.Bg, new Rect(Bounds.Size));
        if (_data is not { } d) return;
        // Only the rows in the viewport are drawn; the header stays at the top of it.
        var top = _scroll?.Offset.Y ?? 0;
        var viewH = _scroll?.Viewport.Height is > 0 and var vh ? vh : Bounds.Height;
        var first = Math.Max(0, (int)(top / RowH));
        var last = Math.Min(d.Rows.Count - 1, (int)((top + viewH) / RowH));
        for (var r = first; r <= last; r++)
        {
            var y = (r + 1) * RowH;
            var rect = new Rect(0, y, Bounds.Width, RowH);
            if (r == Selected) ctx.FillRectangle(p.Sel, rect);
            else if (r % 2 == 1) ctx.FillRectangle(p.RowAlt, rect);
            var row = d.Rows[r];
            for (var c = 0; c < d.Columns.Count; c++)
            {
                var v = row[c];
                var t = T(Cell(v), v is null ? p.Fg3 : r == Selected ? p.SelFg : v is long or double ? p.TkNumber : p.Fg);
                t.MaxTextWidth = Math.Max(1, _x[c + 1] - _x[c] - 12);
                t.MaxLineCount = 1;
                t.Trimming = TextTrimming.CharacterEllipsis;
                var x = v is long or double ? _x[c + 1] - 8 - Math.Min(t.Width, t.MaxTextWidth) : _x[c] + 6;
                ctx.DrawText(t, new Point(x, y + (RowH - t.Height) / 2));
            }
        }
        var head = new Rect(0, top, Bounds.Width, RowH);
        ctx.FillRectangle(p.Bg2, head);
        ctx.FillRectangle(p.Border, new Rect(0, top + RowH - 1, Bounds.Width, 1));
        for (var c = 0; c < d.Columns.Count; c++)
        {
            var t = T(d.Columns[c], p.Fg2, Fonts.MonoBold);
            t.MaxTextWidth = Math.Max(1, _x[c + 1] - _x[c] - 12);
            t.MaxLineCount = 1;
            t.Trimming = TextTrimming.CharacterEllipsis;
            ctx.DrawText(t, new Point(_x[c] + 6, top + (RowH - t.Height) / 2));
            if (c > 0) ctx.FillRectangle(p.Border, new Rect(_x[c], top + 4, 1, RowH - 8));
        }
    }

    public void Select(int r)
    {
        if (_data is not { Rows.Count: > 0 } d) return;
        Selected = Math.Clamp(r, 0, d.Rows.Count - 1);
        this.BringIntoView(new Rect(0, Selected * RowH, 1, RowH * 2));
        InvalidateVisual();
        RowSelected?.Invoke(Selected);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var y = e.GetPosition(this).Y;
        var r = (int)(y / RowH) - 1;
        if (_data is null || r < 0 || r >= _data.Rows.Count || y - (_scroll?.Offset.Y ?? 0) < RowH) return;
        Select(r);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Down: Select(Selected + 1); break;
            case Key.Up: Select(Selected - 1); break;
            case Key.PageDown: Select(Selected + 10); break;
            case Key.PageUp: Select(Selected - 10); break;
            default: return;
        }
        e.Handled = true;
    }
}

/// <summary>A bar chart of a (label, number) result: one bar per row, in result order.</summary>
public sealed class SqlChart : Control
{
    public const int MaxBars = 1000;
    private SqlResult? _data;
    private int _hover = -1;

    public SqlChart()
    {
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public SqlResult? Data
    {
        get => _data;
        set
        {
            _data = value;
            InvalidateVisual();
        }
    }

    private int Bars => Math.Min(MaxBars, _data?.Rows.Count ?? 0);

    private static double Num(object? v) => v switch { long l => l, double d => d, _ => 0 };

    private static FormattedText T(string s, IBrush b) => new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Fonts.MonoFace, 11, b);

    private const double Left = 56, Bottom = 22, TopPad = 10;

    public override void Render(DrawingContext ctx)
    {
        var p = Palette.Current;
        ctx.FillRectangle(p.Bg, new Rect(Bounds.Size));
        if (_data is not { } d || Bars == 0) return;
        var n = Bars;
        var max = 0.0;
        var min = 0.0;
        for (var i = 0; i < n; i++)
        {
            max = Math.Max(max, Num(d.Rows[i][1]));
            min = Math.Min(min, Num(d.Rows[i][1]));
        }
        if (max <= min) max = min + 1;
        var plotW = Math.Max(1, Bounds.Width - Left - 8);
        var plotH = Math.Max(1, Bounds.Height - Bottom - TopPad);
        double Y(double v) => TopPad + plotH * (max - v) / (max - min);
        var bw = plotW / n;
        ctx.FillRectangle(p.Border, new Rect(Left, Y(0), plotW, 1));
        var maxT = T(SqlResultsView.Cell(max), p.Fg3);
        ctx.DrawText(maxT, new Point(Left - 6 - maxT.Width, TopPad - maxT.Height / 2));
        var zeroT = T(SqlResultsView.Cell(min < 0 ? min : 0), p.Fg3);
        ctx.DrawText(zeroT, new Point(Left - 6 - zeroT.Width, (min < 0 ? Y(min) : Y(0)) - zeroT.Height / 2));
        for (var i = 0; i < n; i++)
        {
            var v = Num(d.Rows[i][1]);
            var y0 = Y(Math.Max(0, v));
            var y1 = Y(Math.Min(0, v));
            ctx.FillRectangle(i == _hover ? p.TkWord : p.Accent, new Rect(Left + i * bw + (bw > 4 ? 1 : 0), y0, Math.Max(1, bw - (bw > 4 ? 2 : 0)), Math.Max(1, y1 - y0)));
        }
        var a = T(SqlResultsView.Cell(d.Rows[0][0]), p.Fg2);
        ctx.DrawText(a, new Point(Left, Bounds.Height - Bottom + 4));
        if (n > 1)
        {
            var b = T(SqlResultsView.Cell(d.Rows[n - 1][0]), p.Fg2);
            ctx.DrawText(b, new Point(Math.Max(Left + a.Width + 12, Bounds.Width - 8 - b.Width), Bounds.Height - Bottom + 4));
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var x = e.GetPosition(this).X - Left;
        var i = Bars > 0 && x >= 0 ? (int)(x / (Math.Max(1, Bounds.Width - Left - 8) / Bars)) : -1;
        if (i >= Bars) i = -1;
        if (i == _hover) return;
        _hover = i;
        ToolTip.SetTip(this, i >= 0 ? $"{SqlResultsView.Cell(_data!.Rows[i][0])}: {SqlResultsView.Cell(_data.Rows[i][1])}" : null);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = -1;
        InvalidateVisual();
    }
}
