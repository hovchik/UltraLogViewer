using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using UltraLogViewer.Core;

namespace UltraLogViewer.Desktop.Controls;

/// <summary>
/// The patterns list: one row per message template with its count, share, level mix and a sparkline of where it
/// occurs. Lives in a ScrollViewer; draws every row (the list is capped at a few hundred patterns).
/// </summary>
public sealed class PatternsView : Control
{
    public const double RowH = 24;
    public static readonly double[] ColX = [0, 88, 150, 222, 334];
    private static readonly Regex Placeholder = new("<(num|guid|ip|time|str|url|hex)>", RegexOptions.Compiled);
    private static readonly Level[] LevelOrder = [Level.Fatal, Level.Error, Level.Warn, Level.Info, Level.Debug, Level.Trace, Level.None];
    private PatternStats? _data;
    private int _hover = -1;

    public PatternsView()
    {
        Focusable = true;
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    /// <summary>Raised with a pattern and true for "only this pattern", false for "exclude".</summary>
    public event Action<PatternInfo, bool>? Pick;
    public event Action<PatternInfo?>? SelectionChanged;
    public event Action<PatternInfo>? MenuRequested;
    public event Action? CloseRequested;

    public int Selected { get; private set; } = -1;
    public PatternInfo? Current => _data is { } d && Selected >= 0 && Selected < d.Patterns.Count ? d.Patterns[Selected] : null;

    public PatternStats? Data
    {
        get => _data;
        set
        {
            var keep = Current?.Template;
            _data = value;
            Selected = value is null || keep is null ? (value is { Patterns.Count: > 0 } ? 0 : -1) : Math.Max(0, IndexOf(keep));
            Height = Math.Max(RowH, (value?.Patterns.Count ?? 0) * RowH);
            InvalidateVisual();
            SelectionChanged?.Invoke(Current);
        }
    }

    private int IndexOf(string template)
    {
        var list = _data!.Patterns;
        for (var i = 0; i < list.Count; i++)
            if (list[i].Template == template) return i;
        return 0;
    }

    public void Select(int i)
    {
        if (_data is not { Patterns.Count: > 0 } d) return;
        Selected = Math.Clamp(i, 0, d.Patterns.Count - 1);
        this.BringIntoView(new Rect(0, Selected * RowH, Bounds.Width, RowH));
        InvalidateVisual();
        SelectionChanged?.Invoke(Current);
    }

    private static FormattedText T(string s, IBrush b, double size = 12, Typeface? face = null) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face ?? Fonts.MonoFace, size, b);

    public override void Render(DrawingContext ctx)
    {
        var p = Palette.Current;
        ctx.FillRectangle(p.Bg, new Rect(Bounds.Size));
        if (_data is not { } d) return;
        var w = Bounds.Width;
        for (var i = 0; i < d.Patterns.Count; i++)
        {
            var pi = d.Patterns[i];
            var y = i * RowH;
            var rect = new Rect(0, y, w, RowH);
            if (i == Selected) ctx.FillRectangle(p.Sel, rect);
            else if (i == _hover) ctx.FillRectangle(p.Bg3, rect);
            else if (i % 2 == 1) ctx.FillRectangle(p.RowAlt, rect);
            var fg = i == Selected ? p.SelFg : p.Fg;
            var count = T(pi.Count.ToString("N0", CultureInfo.InvariantCulture), fg);
            ctx.DrawText(count, new Point(ColX[1] - 10 - count.Width, y + (RowH - count.Height) / 2));
            var pct = T((d.Scanned > 0 ? 100.0 * pi.Count / d.Scanned : 0).ToString("0.0", CultureInfo.InvariantCulture) + "%", p.Fg2, 11.5);
            ctx.DrawText(pct, new Point(ColX[2] - 10 - pct.Width, y + (RowH - pct.Height) / 2));

            // level mix as one stacked bar
            var bx = ColX[2];
            const double BarW = 60;
            foreach (var l in LevelOrder)
            {
                var n = pi.Levels[(int)l];
                if (n == 0) continue;
                var lw = BarW * n / pi.Count;
                ctx.FillRectangle(p.LevelSwatch(l), new Rect(bx, y + 8, Math.Max(1, lw), RowH - 16));
                bx += lw;
            }

            // sparkline of where the pattern occurs
            var max = Math.Max(1, pi.Spark.Max());
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                const double SparkW = 100;
                for (var k = 0; k < pi.Spark.Length; k++)
                {
                    var px = ColX[3] + SparkW * k / (pi.Spark.Length - 1);
                    var py = y + RowH - 5 - (RowH - 10) * pi.Spark[k] / max;
                    if (k == 0) g.BeginFigure(new Point(px, py), false);
                    else g.LineTo(new Point(px, py));
                }
                g.EndFigure(false);
            }
            ctx.DrawGeometry(null, new Pen(p.Accent, 1.4), geo);

            var tt = T(pi.Template, fg);
            tt.MaxTextWidth = Math.Max(1, w - ColX[4] - 8);
            tt.MaxLineCount = 1;
            tt.Trimming = TextTrimming.CharacterEllipsis;
            if (i != Selected)
                foreach (Match m in Placeholder.Matches(pi.Template))
                    tt.SetForegroundBrush(p.TkDate, m.Index, m.Length);
            ctx.DrawText(tt, new Point(ColX[4], y + (RowH - tt.Height) / 2));
        }
    }

    private int RowAt(Point pt) => _data is { } d && pt.Y >= 0 && (int)(pt.Y / RowH) < d.Patterns.Count ? (int)(pt.Y / RowH) : -1;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var i = RowAt(e.GetPosition(this));
        if (i < 0) return;
        Select(i);
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsRightButtonPressed && Current is { } c) MenuRequested?.Invoke(c);
        else if (e.ClickCount == 2 && Current is { } d) Pick?.Invoke(d, true);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var i = RowAt(e.GetPosition(this));
        if (i == _hover) return;
        _hover = i;
        ToolTip.SetTip(this, i >= 0 ? $"{_data!.Patterns[i].Template}\n\nExample: {_data.Patterns[i].Sample}" : null);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = -1;
        InvalidateVisual();
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
            case Key.Enter when Current is { } c: Pick?.Invoke(c, true); break;
            case Key.Escape: CloseRequested?.Invoke(); break;
            default: return;
        }
        e.Handled = true;
    }
}
