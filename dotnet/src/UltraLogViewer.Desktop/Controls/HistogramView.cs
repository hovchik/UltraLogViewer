using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using UltraLogViewer.Core;

namespace UltraLogViewer.Desktop.Controls;

/// <summary>
/// Timeline of entries per time bucket, stacked by severity, or by any field (<see cref="Groups"/>) with a legend whose
/// items filter to their value. Click jumps to that time, dragging selects a time range to filter on. The time range on
/// screen in the grid is a subtle band.
/// </summary>
public sealed class HistogramView : Control
{
    private Histogram? _data;
    private (long From, long To)? _viewport;
    private double? _dragStart;
    private double _dragEnd;
    private int _hover = -1;
    private GroupedHistogram? _groups;
    private string _groupLabel = "Level";
    private Rect _groupRect;
    private readonly List<(Rect Rect, int Index)> _legend = [];

    /// <summary>Series colours of a grouped timeline: eight distinct hues, then grey for "other".</summary>
    public static readonly Color[] GroupColors =
    [
        Color.Parse("#4C6EF5"), Color.Parse("#F59F00"), Color.Parse("#12B886"), Color.Parse("#E64980"),
        Color.Parse("#7950F2"), Color.Parse("#1098AD"), Color.Parse("#FA5252"), Color.Parse("#82C91E"),
    ];
    public static readonly Color OtherColor = Color.Parse("#868E96");

    public static Color ColorOf(GroupedHistogram g, int index) => index == g.Keys.Count - 1 && g.HasOther ? OtherColor : GroupColors[index % GroupColors.Length];

    /// <summary>Raised when the "Group by" label is clicked (the window shows the choices).</summary>
    public event Action<Control>? GroupMenuRequested;

    /// <summary>Raised with the index of a clicked legend item.</summary>
    public event Action<int>? LegendClicked;

    /// <summary>Legend items filter when clicked (not for the files of a merged view).</summary>
    public bool LegendClickable { get; set; } = true;

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

    /// <summary>The bars stacked by a field instead of by level (null: by level).</summary>
    public GroupedHistogram? Groups
    {
        get => _groups;
        set
        {
            _groups = value;
            InvalidateVisual();
        }
    }

    public string GroupLabel
    {
        get => _groupLabel;
        set
        {
            _groupLabel = value;
            InvalidateVisual();
        }
    }

    /// <summary>Grouped bars that fit the level data's buckets (the grouping of an older view is not drawn).</summary>
    private GroupedHistogram? Shown => _groups is { } g && _data is { } d && g.Start == d.Start && g.BucketMs == d.BucketMs && g.Counts.Length == d.Counts.Length ? g : null;

    public void SetViewport(long? from, long? to)
    {
        _viewport = from is long f && to is long t ? (f, t) : null;
        InvalidateVisual();
    }

    private const double AxisHeight = 14;
    /// <summary>The "Group by" label and the legend.</summary>
    private const double BarHeight = 16;

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
        var h = Bounds.Height - AxisHeight - BarHeight - 2;
        var max = Math.Max(1, d.Counts.Max(c => c.Other + c.Warn + c.Error));
        var bw = BarWidth;
        var gap = bw > 3 ? 1 : 0;
        var g = Shown;
        var brushes = g?.Keys.Select((_, k) => (IBrush)new ImmutableSolidColorBrush(ColorOf(g, k))).ToArray();
        for (var i = 0; i < d.Counts.Length; i++)
        {
            var (o, w, e) = d.Counts[i];
            var x = i * bw;
            var y = BarHeight + h;
            void Bar(int n, IBrush b)
            {
                if (n == 0) return;
                var bh = Math.Max(1, n / (double)max * h);
                y -= bh;
                ctx.FillRectangle(b, new Rect(x, y, Math.Max(1, bw - gap), bh));
            }
            if (g is not null)
            {
                // the most common value at the bottom, "other" on top
                for (var k = 0; k < g.Keys.Count; k++) Bar(g.Counts[i][k], brushes![k]);
                continue;
            }
            Bar(e, p.Error);
            Bar(w, p.Warn);
            Bar(o, p.Accent);
        }
        RenderHeader(ctx, p, g);

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
            ctx.FillRectangle(new ImmutableSolidColorBrush(((ISolidColorBrush)p.Fg).Color, 0.12), new Rect(_hover * bw, BarHeight, Math.Max(1, bw), Bounds.Height - AxisHeight - BarHeight));
        if (_viewport is { } vp)
        {
            var x1 = Math.Max(0, XOf(vp.From));
            var x2 = Math.Min(Bounds.Width, Math.Max(x1 + 2, XOf(vp.To)));
            // the rows on screen: a subtle band
            ctx.FillRectangle(new ImmutableSolidColorBrush(((ISolidColorBrush)p.Accent).Color, 0.12), new Rect(x1, BarHeight, x2 - x1, Bounds.Height - AxisHeight - BarHeight));
            if (x2 - x1 < Bounds.Width * 0.9)
            {
                // dim what is outside the rows on screen
                var dim = new ImmutableSolidColorBrush(((ISolidColorBrush)p.Bg).Color, 0.35);
                ctx.FillRectangle(dim, new Rect(0, BarHeight, x1, Bounds.Height - AxisHeight - BarHeight));
                ctx.FillRectangle(dim, new Rect(x2, BarHeight, Math.Max(0, Bounds.Width - x2), Bounds.Height - AxisHeight - BarHeight));
            }
            ctx.DrawRectangle(null, new Pen(p.Fg2, 1), new Rect(x1 + 0.5, BarHeight + 0.5, Math.Max(1, x2 - x1 - 1), Bounds.Height - AxisHeight - BarHeight - 1));
        }
        if (_dragStart is double ds)
        {
            var a = Math.Min(ds, _dragEnd);
            var b = Math.Max(ds, _dragEnd);
            ctx.FillRectangle(new ImmutableSolidColorBrush(((ISolidColorBrush)p.Accent).Color, 0.25), new Rect(a, 0, Math.Max(1, b - a), Bounds.Height));
        }
    }

    /// <summary>"Group by: Level ▾" at the left, then the legend of a grouped timeline as far as it fits.</summary>
    private void RenderHeader(DrawingContext ctx, Palette p, GroupedHistogram? g)
    {
        _legend.Clear();
        var label = new FormattedText($"Group by: {_groupLabel} ▾", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 10.5, p.Fg2);
        _groupRect = new Rect(2, 1, label.Width + 8, BarHeight - 2);
        ctx.FillRectangle(p.Bg, _groupRect, 3);
        ctx.DrawText(label, new Point(6, (BarHeight - label.Height) / 2));
        if (g is null) return;
        var x = _groupRect.Right + 10;
        for (var k = 0; k < g.Keys.Count; k++)
        {
            var name = g.Keys[k].Length > 24 ? g.Keys[k][..23] + "…" : g.Keys[k];
            var t = new FormattedText(name, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 10.5, p.Fg2);
            var w = 12 + t.Width;
            if (x + w > Bounds.Width - 4) break;
            ctx.FillRectangle(new ImmutableSolidColorBrush(ColorOf(g, k)), new Rect(x, (BarHeight - 8) / 2, 8, 8), 2);
            ctx.DrawText(t, new Point(x + 11, (BarHeight - t.Height) / 2));
            _legend.Add((new Rect(x - 2, 0, w + 4, BarHeight), k));
            x += w + 10;
        }
    }

    /// <summary>Test hook: the legend items drawn by the last paint.</summary>
    internal IReadOnlyList<int> LegendForTest => _legend.Select(l => l.Index).ToList();

    internal void ClickLegendForTest(int index) => LegendClicked?.Invoke(index);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var pt = e.GetPosition(this);
        if (pt.Y < BarHeight && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            if (_groupRect.Contains(pt)) GroupMenuRequested?.Invoke(this);
            else if (LegendClickable && _legend.FirstOrDefault(l => l.Rect.Contains(pt)) is { Rect.Width: > 0 } hit) LegendClicked?.Invoke(hit.Index);
            e.Handled = true;
            return;
        }
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
        var y = e.GetPosition(this).Y;
        if (y < BarHeight && _dragStart is null)
        {
            var leg = _legend.FirstOrDefault(l => l.Rect.Contains(new Point(x, y)));
            var onGroup = _groupRect.Contains(new Point(x, y));
            Cursor = new Cursor(onGroup || (LegendClickable && leg.Rect.Width > 0) ? StandardCursorType.Hand : StandardCursorType.Arrow);
            ToolTip.SetTip(this, onGroup ? "Stack the bars by level, by file or by any field"
                : leg.Rect.Width > 0 && Shown is { } sg ? $"{sg.Keys[leg.Index]}: {sg.Totals[leg.Index]:N0} entries{(LegendClickable ? "\nClick to filter to it" : "")}" : null);
            if (_hover != -1)
            {
                _hover = -1;
                InvalidateVisual();
            }
            return;
        }
        Cursor = new Cursor(StandardCursorType.Cross);
        if (_data is { Counts.Length: > 0 } d && Bounds.Width > 0)
        {
            var i = Math.Clamp((int)(x / BarWidth), 0, d.Counts.Length - 1);
            if (i != _hover)
            {
                _hover = i;
                ToolTip.SetTip(this, Shown is { } g ? GroupTip(d, g, i) : Tip(d, i));
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

    /// <summary>Bucket time range, entry count, and the most common values in it.</summary>
    public static string GroupTip(Histogram d, GroupedHistogram g, int i)
    {
        var lines = Tip(d, i).Split('\n').Take(2).ToList();
        foreach (var (n, k) in g.Counts[i].Select((n, k) => (n, k)).Where(x => x.n > 0).OrderByDescending(x => x.n).Take(5))
            lines.Add($"{g.Keys[k]}: {n:N0}");
        return string.Join("\n", lines);
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
