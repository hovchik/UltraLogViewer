using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using UltraLogViewer.Core;

namespace UltraLogViewer.Desktop.Controls;

/// <summary>
/// Timeline of entries per time bucket, stacked by severity. Click jumps to that time,
/// dragging selects a time range to filter on. The current viewport is outlined.
/// </summary>
public sealed class HistogramView : Control
{
    private Histogram? _data;
    private (long From, long To)? _viewport;
    private double? _dragStart;
    private double _dragEnd;
    private int _hover = -1;

    /// <summary>Raised with the time under a click.</summary>
    public event Action<long>? Jump;

    /// <summary>Raised with the [from, to] range of a drag.</summary>
    public event Action<long, long>? RangeSelected;

    public HistogramView()
    {
        Height = 64;
        MinHeight = 40;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Cross);
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public Histogram? Data
    {
        get => _data;
        set
        {
            _data = value;
            InvalidateVisual();
        }
    }

    public void SetViewport(long? from, long? to)
    {
        _viewport = from is long f && to is long t ? (f, t) : null;
        InvalidateVisual();
    }

    private const double AxisHeight = 14;

    private double BarWidth => _data is { Counts.Length: > 0 } d ? Bounds.Width / d.Counts.Length : 0;

    private long TimeAt(double x)
    {
        var d = _data!;
        var f = Math.Clamp(x / Math.Max(1, Bounds.Width), 0, 1);
        return d.Start + (long)(f * d.Counts.Length * d.BucketMs);
    }

    private double XOf(long t)
    {
        var d = _data!;
        return (double)(t - d.Start) / (d.Counts.Length * d.BucketMs) * Bounds.Width;
    }

    public override void Render(DrawingContext ctx)
    {
        var p = Palette.Current;
        ctx.FillRectangle(p.Bg2, new Rect(Bounds.Size));
        ctx.DrawLine(new Pen(p.Border), new Point(0, Bounds.Height - 0.5), new Point(Bounds.Width, Bounds.Height - 0.5));
        var d = _data;
        if (d is null || d.Counts.Length == 0)
        {
            var t = new FormattedText("No timestamps in this view", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 11, p.Fg3);
            ctx.DrawText(t, new Point((Bounds.Width - t.Width) / 2, (Bounds.Height - t.Height) / 2));
            return;
        }
        var h = Bounds.Height - AxisHeight - 4;
        var max = Math.Max(1, d.Counts.Max(c => c.Other + c.Warn + c.Error));
        var bw = BarWidth;
        var gap = bw > 3 ? 1 : 0;
        for (var i = 0; i < d.Counts.Length; i++)
        {
            var (o, w, e) = d.Counts[i];
            var x = i * bw;
            var y = 4 + h;
            void Bar(int n, IBrush b)
            {
                if (n == 0) return;
                var bh = Math.Max(1, n / (double)max * h);
                y -= bh;
                ctx.FillRectangle(b, new Rect(x, y, Math.Max(1, bw - gap), bh));
            }
            Bar(e, p.Error);
            Bar(w, p.Warn);
            Bar(o, p.Accent);
        }

        // axis labels: start, middle, end
        var span = d.Counts.Length * d.BucketMs;
        var fmt = span > 2 * 86_400_000L ? "MM-dd HH:mm" : span > 120_000 ? "HH:mm" : "HH:mm:ss";
        for (var k = 0; k <= 2; k++)
        {
            var t = d.Start + span * k / 2;
            var s = DateTimeOffset.FromUnixTimeMilliseconds(t).ToLocalTime().ToString(fmt, CultureInfo.InvariantCulture);
            var ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 10, p.Fg3);
            var lx = k == 0 ? 3 : k == 1 ? (Bounds.Width - ft.Width) / 2 : Bounds.Width - ft.Width - 3;
            ctx.DrawText(ft, new Point(lx, Bounds.Height - AxisHeight));
        }
        var bucketLabel = new FormattedText($"{Timestamps.FormatDuration(d.BucketMs)} / bar", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 10, p.Fg3);
        ctx.DrawText(bucketLabel, new Point(Bounds.Width * 0.75 - bucketLabel.Width / 2, Bounds.Height - AxisHeight));

        if (_hover >= 0 && _hover < d.Counts.Length && _dragStart is null)
            ctx.FillRectangle(new ImmutableSolidColorBrush(((ISolidColorBrush)p.Fg).Color, 0.12), new Rect(_hover * bw, 0, Math.Max(1, bw), Bounds.Height - AxisHeight));
        if (_viewport is { } vp)
        {
            var x1 = Math.Max(0, XOf(vp.From));
            var x2 = Math.Min(Bounds.Width, Math.Max(x1 + 2, XOf(vp.To)));
            if (x2 - x1 < Bounds.Width * 0.9)
            {
                // dim what is outside the rows on screen
                var dim = new ImmutableSolidColorBrush(((ISolidColorBrush)p.Bg).Color, 0.35);
                ctx.FillRectangle(dim, new Rect(0, 0, x1, Bounds.Height - AxisHeight));
                ctx.FillRectangle(dim, new Rect(x2, 0, Math.Max(0, Bounds.Width - x2), Bounds.Height - AxisHeight));
            }
            ctx.DrawRectangle(null, new Pen(p.Fg2, 1), new Rect(x1 + 0.5, 0.5, Math.Max(1, x2 - x1 - 1), Bounds.Height - AxisHeight - 1));
        }
        if (_dragStart is double ds)
        {
            var a = Math.Min(ds, _dragEnd);
            var b = Math.Max(ds, _dragEnd);
            ctx.FillRectangle(new ImmutableSolidColorBrush(((ISolidColorBrush)p.Accent).Color, 0.25), new Rect(a, 0, Math.Max(1, b - a), Bounds.Height));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_data is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragStart = _dragEnd = e.GetPosition(this).X;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var x = e.GetPosition(this).X;
        if (_dragStart is not null)
        {
            _dragEnd = Math.Clamp(x, 0, Bounds.Width);
            InvalidateVisual();
        }
        if (_data is { Counts.Length: > 0 } d && Bounds.Width > 0)
        {
            var i = Math.Clamp((int)(x / BarWidth), 0, d.Counts.Length - 1);
            if (i != _hover)
            {
                _hover = i;
                ToolTip.SetTip(this, Tip(d, i));
                InvalidateVisual();
            }
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = -1;
        InvalidateVisual();
    }

    /// <summary>Bucket time range, entry count, and warnings/errors when there are any.</summary>
    public static string Tip(Histogram d, int i)
    {
        var from = d.Start + i * d.BucketMs;
        var to = from + d.BucketMs;
        var a = DateTimeOffset.FromUnixTimeMilliseconds(from).ToLocalTime();
        var b = DateTimeOffset.FromUnixTimeMilliseconds(to).ToLocalTime();
        var multiDay = d.Counts.Length * d.BucketMs > 86_400_000L || a.Date != b.Date;
        var fmt = multiDay ? "yyyy-MM-dd HH:mm:ss" : "HH:mm:ss";
        var (o, w, er) = d.Counts[i];
        var lines = new List<string>
        {
            $"{a.ToString(fmt, CultureInfo.InvariantCulture)} – {b.ToString(fmt, CultureInfo.InvariantCulture)}",
            $"{o + w + er:N0} {(o + w + er == 1 ? "entry" : "entries")}",
        };
        var parts = new List<string>();
        if (w > 0) parts.Add($"{w:N0} {(w == 1 ? "warning" : "warnings")}");
        if (er > 0) parts.Add($"{er:N0} {(er == 1 ? "error" : "errors")}");
        if (parts.Count > 0) lines.Add(string.Join(" · ", parts));
        return string.Join("\n", lines);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragStart is not double ds || _data is null) return;
        _dragStart = null;
        e.Pointer.Capture(null);
        InvalidateVisual();
        var a = Math.Min(ds, _dragEnd);
        var b = Math.Max(ds, _dragEnd);
        if (b - a < 4) Jump?.Invoke(TimeAt(a));
        else RangeSelected?.Invoke(TimeAt(a), TimeAt(b));
    }
}
