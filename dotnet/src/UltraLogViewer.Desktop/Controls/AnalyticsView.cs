using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using UltraLogViewer.Core;

namespace UltraLogViewer.Desktop.Controls;

/// <summary>
/// The analytics dashboard (as renderer/analyticsView.ts): cards with insights, totals, levels over time, level, file,
/// hour and weekday bars, pattern lists, exceptions, bursts, silences, durations, field distributions and top IPs,
/// URLs and methods, all drawn here. Every mark has a tooltip and filters the log on click (Shift+click hides);
/// dragging over the timeline filters to a time range. Lives in a ScrollViewer.
/// </summary>
public sealed class AnalyticsView : Control
{
    private const double Pad = 10, CardPad = 12, Gap = 10, BarH = 22, PtH = 24, MinCol = 340;
    private static readonly Regex Placeholder = new("(<(?:num|guid|ip|time|str|url|hex)>)", RegexOptions.Compiled);
    private static readonly Level[] LevelOrder = [Level.Fatal, Level.Error, Level.Warn, Level.Info, Level.Debug, Level.Trace, Level.None];
    private static readonly string[] LevelLabel = ["No level", "Trace", "Debug", "Info", "Warning", "Error", "Fatal"];
    private static readonly string[] Weekdays = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
    private static readonly IBrush[] SourceColors = new[] { "#2a78d6", "#eb6834", "#1baf7a", "#eda100", "#e87ba4", "#008300", "#6250d6", "#e34948" }.Select(c => (IBrush)new SolidColorBrush(Color.Parse(c))).ToArray();
    private const string Hint = "Click: show only these · Shift+click: hide them";

    private sealed record Hit(Rect Rect, string Tip, Action<bool>? Act);

    private AnalyticsResult? _data;
    private readonly List<Hit> _hits = [];
    private Hit? _hover;
    private Point _pointer;
    private Rect _timelineRect;
    private (long Start, long End) _timelineSpan;
    private double? _dragFrom;
    private double _dragTo;

    public AnalyticsView()
    {
        Focusable = true;
        ClipToBounds = true;
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    /// <summary>Level, only (else hide), grouped (Error also takes fatals, Debug also takes trace and no level).</summary>
    public event Action<Level, bool, bool>? FilterLevel;
    public event Action<long, long>? FilterTime;
    public event Action<string, bool>? FilterPattern;
    public event Action<string, string, bool>? FilterField;
    public event Action<string, bool>? FilterText;
    public event Action<long>? JumpTo;

    public AnalyticsResult? Data
    {
        get => _data;
        set
        {
            _data = value;
            if (_dragFrom is null) _hover = null;
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    /// <summary>Tooltips of the marks drawn last (for tests).</summary>
    public IReadOnlyList<string> TipsForTest => _hits.Select(h => h.Tip).ToList();

    /// <summary>Clicks the first mark whose tooltip starts with <paramref name="prefix"/> (for tests).</summary>
    public bool ClickForTest(string prefix, bool shift = false)
    {
        var h = _hits.FirstOrDefault(x => x.Act is not null && x.Tip.StartsWith(prefix, StringComparison.Ordinal));
        h?.Act!(shift);
        return h is not null;
    }

    protected override Size MeasureOverride(Size available)
    {
        var w = double.IsInfinity(available.Width) ? 1000 : available.Width;
        _hits.Clear();
        return new Size(w, Paint(null, w));
    }

    public override void Render(DrawingContext ctx)
    {
        var p = Palette.Current;
        ctx.FillRectangle(p.Bg2, new Rect(Bounds.Size));
        _hits.Clear();
        Paint(ctx, Bounds.Width);
        if (_dragFrom is { } a && _timelineRect.Width > 0)
        {
            var lo = Math.Min(a, _dragTo);
            var hi = Math.Max(a, _dragTo);
            ctx.FillRectangle(new SolidColorBrush(((ISolidColorBrush)p.Accent).Color, 0.18), new Rect(lo, _timelineRect.Y, hi - lo, _timelineRect.Height));
            DrawTip(ctx, $"{Fmt.Time(At(lo))} → {Fmt.Time(At(hi))}\nRelease: show this time range", _pointer);
        }
        else if (_hover is { } h)
        {
            ctx.FillRectangle(new SolidColorBrush(((ISolidColorBrush)p.Fg).Color, 0.07), h.Rect);
            DrawTip(ctx, h.Tip, _pointer);
        }
    }

    private long At(double x) => _timelineSpan.Start + (long)((x - _timelineRect.X) / Math.Max(1, _timelineRect.Width) * (_timelineSpan.End - _timelineSpan.Start));

    // ------------------------------------------------------------------ text

    private static FormattedText T(string s, IBrush b, double size = 12, bool bold = false, bool mono = false, double maxW = double.PositiveInfinity)
    {
        var ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            mono ? Fonts.MonoFace : new Typeface(FontFamily.Default, FontStyle.Normal, bold ? FontWeight.SemiBold : FontWeight.Normal), size, b);
        if (!double.IsInfinity(maxW))
        {
            ft.MaxTextWidth = Math.Max(1, maxW);
            ft.MaxLineCount = 1;
            ft.Trimming = TextTrimming.CharacterEllipsis;
        }
        return ft;
    }

    private static double Text(DrawingContext? ctx, string s, double x, double y, IBrush b, double size = 12, bool bold = false, bool mono = false, double maxW = double.PositiveInfinity, bool right = false)
    {
        var ft = T(s, b, size, bold, mono, maxW);
        if (ctx is not null) ctx.DrawText(ft, new Point(right ? x - ft.Width : x, y));
        return ft.WidthIncludingTrailingWhitespace;
    }

    /// <summary>A template with its placeholders dimmed, cut to <paramref name="maxW"/>.</summary>
    private static void Template(DrawingContext? ctx, string tpl, double x, double y, double maxW, Palette p)
    {
        if (ctx is null) return;
        var end = x + maxW;
        foreach (var (part, i) in Placeholder.Split(tpl).Select((s, i) => (s, i)))
        {
            if (part.Length == 0) continue;
            var room = end - x;
            if (room <= 8) break;
            var ft = T(part, i % 2 == 1 ? p.Fg3 : p.Fg, 11.5, mono: true, maxW: room);
            ctx.DrawText(ft, new Point(x, y));
            x += ft.WidthIncludingTrailingWhitespace;
        }
    }

    private void DrawTip(DrawingContext ctx, string tip, Point at)
    {
        var p = Palette.Current;
        var lines = tip.Split('\n');
        var fts = lines.Select((l, i) => T(l, p.Fg, 11.5, bold: i == 0, maxW: 460)).ToList();
        var w = fts.Max(f => f.Width) + 18;
        var h = fts.Sum(f => f.Height) + 12;
        var x = at.X + 14;
        var y = at.Y + 16;
        if (x + w > Bounds.Width - 4) x = Math.Max(4, at.X - w - 10);
        if (y + h > Bounds.Height - 4) y = Math.Max(4, at.Y - h - 8);
        var box = new Rect(x, y, w, h);
        ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)), null, box.Translate(new Vector(0, 2)).Inflate(1), 5, 5);
        ctx.DrawRectangle(p.Bg, new Pen(p.Border), box, 5, 5);
        var ty = y + 6;
        foreach (var f in fts)
        {
            ctx.DrawText(f, new Point(x + 9, ty));
            ty += f.Height;
        }
    }

    private void AddHit(Rect r, string tip, Action<bool>? act = null) => _hits.Add(new Hit(r, tip, act));

    // ---------------------------------------------------------------- layout

    /// <summary>Draws the dashboard (or only measures it, without <paramref name="ctx"/>); returns its height.</summary>
    private double Paint(DrawingContext? ctx, double width)
    {
        var p = Palette.Current;
        var r = _data;
        if (r is null)
        {
            Text(ctx, "The analysis starts once the file is loaded.", Pad + 20, 40, p.Fg2);
            return 120;
        }
        var y = Pad;
        var full = width - 2 * Pad;
        y += Card(ctx, p, "Insights", r.State == AnalyticsState.Done ? "" : "So far: these grow while the analysis runs", new Rect(Pad, y, full, 0), (c, a) => Insights(c, p, r, a)) is var ih and > 0 ? ih + Gap : 0;
        y += Tiles(ctx, p, r, new Rect(Pad, y, full, 0)) + Gap;
        if (r.Timeline is { Counts.Length: > 0 })
            y += Card(ctx, p, "Levels over time", "Shaded: error bursts. Click a bar for its time range, drag across bars for a longer one.", new Rect(Pad, y, full, 0), (c, a) => Timeline(c, p, r, a)) + Gap;
        y = Grid(ctx, p, y, full, [
            ("Levels", "", (c, a) => LevelBars(c, p, r, a)),
            ("Files", "Entries per file; warnings and errors at the end of each bar", r.Sources.Count > 1 ? (c, a) => SourceBars(c, p, r, a) : null),
            ("Hour of day", "Local time; the red part is errors", r.Hours.Length > 0 && r.Summary.Timed > 0 ? (c, a) => Hours(c, p, r, a) : null),
            ("Day of week", "", r.Weekdays.Count(x => x > 0) >= 2 ? (c, a) => Columns(c, p, a, r.Weekdays, Weekdays, r.Weekdays.Select((n, i) => $"{Weekdays[i]}: {n:N0} entries").ToArray(), null) : null),
        ]);
        y += Card(ctx, p, "Top message patterns", "Most common templates (numbers, ids and times replaced)", new Rect(Pad, y, full, 0), (c, a) => PatternList(c, p, r, a, r.Patterns.Take(25).ToList(), "count")) + Gap;
        y = Grid(ctx, p, y, full, [
            ("Error patterns", "Templates with the most errors", r.ErrorPatterns.Count > 0 ? (c, a) => PatternList(c, p, r, a, r.ErrorPatterns.Take(12).ToList(), "errors") : null),
            ("Exceptions", "Exception and error types anywhere in the entries, stack traces included", r.Exceptions.Count > 0 ? (c, a) => Exceptions(c, p, r, a) : null),
            ("New late in the log", "First seen in the last quarter of the time span", r.NewPatterns.Count > 0 ? (c, a) => PatternList(c, p, r, a, r.NewPatterns, "first") : null),
            ("Gone quiet", "Common earlier, nothing in the second half", r.StoppedPatterns.Count > 0 ? (c, a) => PatternList(c, p, r, a, r.StoppedPatterns, "last") : null),
            ("Error bursts", "Times with far more errors than usual", r.Bursts.Count > 0 ? (c, a) => Bursts(c, p, r, a) : null),
            ("Silences", "Longest stretches without a single entry", r.Gaps.Count > 0 ? (c, a) => Gaps(c, p, r, a) : null),
            ("Durations", r.Durations is { } d ? $"{d.Count:N0} durations found in messages (\"took 120 ms\")" : "", r.Durations is not null ? (c, a) => Histogram(c, p, a, r.Durations!, Fmt.Ms) : null),
            ("Slowest operations", "Templates with the longest average duration in the message", r.SlowPatterns.Count > 0 ? (c, a) => PatternList(c, p, r, a, r.SlowPatterns, "dur") : null),
        ]);
        if (r.Fields.Any(f => f.Top.Count > 0))
            y += Card(ctx, p, "Fields", "Value distributions of every parsed field; numbers get a histogram and percentiles", new Rect(Pad, y, full, 0), (c, a) => Fields(c, p, r, a)) + Gap;
        y = Grid(ctx, p, y, full, [
            ("Top IP addresses", "", r.Ips.Count > 0 ? (c, a) => Values(c, p, a, r.Ips, r.Scanned) : null),
            ("Top URLs", "", r.Urls.Count > 0 ? (c, a) => Values(c, p, a, r.Urls, r.Scanned) : null),
            ("HTTP methods", "", r.Methods.Count > 0 ? (c, a) => Values(c, p, a, r.Methods, r.Scanned) : null),
        ]);
        return y + Pad;
    }

    private delegate double Body(DrawingContext? ctx, Rect area);

    /// <summary>A card: title, subtitle and body; returns its height (the body draws from area.Y and returns its own height).</summary>
    private double Card(DrawingContext? ctx, Palette p, string title, string sub, Rect at, Body body, double? height = null)
    {
        var inner = new Rect(at.X + CardPad, at.Y + CardPad, at.Width - 2 * CardPad, 0);
        var y = inner.Y;
        // measure first: the background goes under the body
        var bodyTop = y + 20 + (sub.Length > 0 ? 18 : 0);
        var bh = body(null, new Rect(inner.X, bodyTop, inner.Width, 0));
        if (bh <= 0) return 0;
        var h = height ?? bodyTop - at.Y + bh + CardPad;
        if (ctx is not null)
        {
            ctx.DrawRectangle(p.Bg, new Pen(p.Border), new Rect(at.X, at.Y, at.Width, h), 6, 6);
            Text(ctx, title, inner.X, y, p.Fg, 12.5, bold: true, maxW: inner.Width);
            if (sub.Length > 0) Text(ctx, sub, inner.X, y + 20, p.Fg2, 11.5, maxW: inner.Width);
            body(ctx, new Rect(inner.X, bodyTop, inner.Width, 0));
        }
        return h;
    }

    /// <summary>Cards in equal columns, rows as tall as their tallest card; returns the y below them.</summary>
    private double Grid(DrawingContext? ctx, Palette p, double y, double full, (string Title, string Sub, Body? Body)[] cards)
    {
        var list = cards.Where(c => c.Body is not null).ToList();
        if (list.Count == 0) return y;
        var cols = Math.Max(1, Math.Min(list.Count, (int)((full + Gap) / (MinCol + Gap))));
        var w = (full - (cols - 1) * Gap) / cols;
        for (var i = 0; i < list.Count; i += cols)
        {
            var row = list.Skip(i).Take(cols).ToList();
            var heights = row.Select((c, k) => Card(null, p, c.Title, c.Sub, new Rect(Pad + k * (w + Gap), y, w, 0), c.Body!)).ToList();
            var rh = heights.Max();
            if (rh <= 0) continue;
            for (var k = 0; k < row.Count; k++)
                if (heights[k] > 0) Card(ctx, p, row[k].Title, row[k].Sub, new Rect(Pad + k * (w + Gap), y, w, 0), row[k].Body!, rh);
            y += rh + Gap;
        }
        return y;
    }

    // ----------------------------------------------------------------- cards

    private double Insights(DrawingContext? ctx, Palette p, AnalyticsResult r, Rect a)
    {
        var items = new List<(string Text, string? Tpl, string Tip, Action<bool>? Act)>();
        var s = r.Summary;
        var errs = r.Levels[(int)Level.Error] + r.Levels[(int)Level.Fatal];
        if (s.Entries > 0)
            items.Add((errs > 0 ? $"{errs:N0} errors ({Fmt.Pct(errs, s.Entries)} of all entries), {r.Levels[(int)Level.Warn]:N0} warnings." : "No errors in the whole log.", null,
                "Click to filter the log to this", errs > 0 ? _ => FilterLevel?.Invoke(Level.Error, true, true) : null));
        if (r.Bursts.FirstOrDefault() is { } b)
            items.Add(($"Error burst at {Fmt.Time(b.From)}: {b.Errors:N0} errors in {Fmt.Span(b.To - b.From)}, {b.Factor:0}× the usual rate.", null, "Click to filter the log to this", _ => FilterTime?.Invoke(b.From, b.To)));
        if (r.ErrorPatterns.FirstOrDefault() is { } ep)
            items.Add(($"Most frequent error ({ep.Errors:N0}×): ", ep.Template, "Click to filter the log to this", sh => FilterPattern?.Invoke(ep.Template, !sh)));
        if (r.Exceptions.FirstOrDefault() is { } ex)
            items.Add(($"Top exception {ex.Name}: {ex.Count:N0}×, last at {Fmt.Time(ex.Last)}.", null, "Click to filter the log to this", sh => FilterText?.Invoke(ex.Name, !sh)));
        if (r.NewPatterns.FirstOrDefault() is { } np)
            items.Add(($"New message since {Fmt.Time(np.First)} ({np.Count:N0}×): ", np.Template, "Click to filter the log to this", sh => FilterPattern?.Invoke(np.Template, !sh)));
        if (r.Gaps.FirstOrDefault() is { } g)
            items.Add(($"Longest silence: {Fmt.Span(g.Ms)} without entries after {Fmt.Time(g.From)}.", null, "Click to jump there", _ => JumpTo?.Invoke(g.From)));
        if (s.PeakAt is long pk && s.RatePerMin > 0 && s.PeakPerMin > s.RatePerMin * 3 && r.Timeline is { } tl)
            items.Add(($"Busiest moment {Fmt.Time(pk)}: {Fmt.Compact(s.PeakPerMin)}/min, {s.PeakPerMin / s.RatePerMin:0}× the average.", null, "Click to filter the log to this", _ => FilterTime?.Invoke(pk, pk + tl.BucketMs)));
        if (r.SlowPatterns.FirstOrDefault() is { } sl)
            items.Add(($"Slowest operation (avg {Fmt.Ms(sl.DurAvg)}, max {Fmt.Ms(sl.DurMax)}): ", sl.Template, "Click to filter the log to this", sh => FilterPattern?.Invoke(sl.Template, !sh)));
        if (items.Count == 0) return 0;
        var y = a.Y;
        foreach (var (text, tpl, tip, act) in items)
        {
            var w = Text(ctx, "•  " + text, a.X, y, p.Fg, 12, maxW: a.Width);
            if (tpl is not null) Template(ctx, tpl, a.X + w, y + 1, a.Width - w, p);
            if (ctx is not null) AddHit(new Rect(a.X - 4, y - 1, a.Width + 8, 20), tip, act);
            y += 20;
        }
        return y - a.Y;
    }

    private double Tiles(DrawingContext? ctx, Palette p, AnalyticsResult r, Rect a)
    {
        var s = r.Summary;
        var span = s.TimeMin is long t0 && s.TimeMax is long t1 ? t1 - t0 : 0;
        var errs = r.Levels[(int)Level.Error] + r.Levels[(int)Level.Fatal];
        var tiles = new (string Label, string Value, string Sub, string Tip, Action<bool>? Act)[]
        {
            ("Entries", Fmt.Compact(s.Entries), $"{Fmt.Bytes(s.Bytes)} · {s.Sources} {(s.Sources == 1 ? "file" : "files")}", $"{s.Entries:N0} entries", null),
            ("Time span", span > 0 ? Fmt.Span(span) : "–", s.TimeMin is not null ? $"{Fmt.Time(s.TimeMin)} → {Fmt.Time(s.TimeMax, false)}" : "no timestamps", "Time span", null),
            ("Rate", s.RatePerMin > 0 ? $"{Fmt.Compact(s.RatePerMin)}/min" : "–", s.PeakPerMin > 0 ? $"peak {Fmt.Compact(s.PeakPerMin)}/min" : "", "Entries per minute", null),
            ("Errors", Fmt.Compact(errs), Fmt.Pct(errs, s.Entries), $"{errs:N0} errors and fatals\n{Hint}", sh => FilterLevel?.Invoke(Level.Error, !sh, true)),
            ("Warnings", Fmt.Compact(r.Levels[(int)Level.Warn]), Fmt.Pct(r.Levels[(int)Level.Warn], s.Entries), $"{r.Levels[(int)Level.Warn]:N0} warnings\n{Hint}", sh => FilterLevel?.Invoke(Level.Warn, !sh, false)),
            ("Patterns", Fmt.Compact(s.Patterns), "distinct templates", "Distinct message templates", null),
            ("Exceptions", Fmt.Compact(s.Exceptions), "distinct types", "Distinct exception types", null),
            ("Multi-line", Fmt.Compact(s.Multiline), r.Scanned > 0 ? $"{Fmt.Pct(s.Multiline, r.Scanned)} · avg {s.AvgLength:0} chars" : "", "Entries with more than one line", null),
        };
        var cols = Math.Max(1, Math.Min(tiles.Length, (int)((a.Width + Gap) / (118 + Gap))));
        var w = (a.Width - (cols - 1) * Gap) / cols;
        const double h = 66;
        for (var i = 0; i < tiles.Length; i++)
        {
            var x = a.X + i % cols * (w + Gap);
            var y = a.Y + i / cols * (h + Gap);
            if (ctx is null) continue;
            var t = tiles[i];
            ctx.DrawRectangle(p.Bg, new Pen(p.Border), new Rect(x, y, w, h), 6, 6);
            Text(ctx, t.Label, x + 12, y + 7, p.Fg2, 11.5, maxW: w - 24);
            Text(ctx, t.Value, x + 12, y + 22, p.Fg, 19, bold: true, maxW: w - 24);
            Text(ctx, t.Sub, x + 12, y + 47, p.Fg3, 11, maxW: w - 24);
            AddHit(new Rect(x, y, w, h), t.Tip, t.Act);
        }
        var rows = (tiles.Length + cols - 1) / cols;
        return rows * h + (rows - 1) * Gap;
    }

    private static readonly (string Name, Level[] Levels, Func<Palette, IBrush> Color, Level Filter)[] Series =
    [
        ("Errors", [Level.Fatal, Level.Error], p => p.Error, Level.Error),
        ("Warnings", [Level.Warn], p => p.Warn, Level.Warn),
        ("Info", [Level.Info], p => p.Accent, Level.Info),
        ("Debug & trace", [Level.Debug, Level.Trace, Level.None], p => p.Fg3, Level.Debug),
    ];

    private double Timeline(DrawingContext? ctx, Palette p, AnalyticsResult r, Rect a)
    {
        var tl = r.Timeline!;
        var n = tl.Counts.Length;
        var sums = tl.Counts.Select(c => Series.Select(s => s.Levels.Sum(l => c[(int)l])).ToArray()).ToArray();
        var max = Math.Max(1, sums.Max(x => x.Sum()));
        const double legendH = 20, H = 150, tickH = 18;
        if (ctx is null) return legendH + H + tickH;
        // legend
        var lx = a.X;
        foreach (var s in Series)
        {
            ctx.DrawRectangle(s.Color(p), null, new Rect(lx, a.Y + 4, 10, 10), 2, 2);
            var w = Text(ctx, s.Name, lx + 15, a.Y, p.Fg2, 12);
            var f = s.Filter;
            AddHit(new Rect(lx - 2, a.Y - 2, w + 20, 20), $"{s.Name}\n{Hint}", sh => FilterLevel?.Invoke(f, !sh, true));
            lx += w + 32;
        }
        Text(ctx, $"{Fmt.Span(tl.BucketMs)} per bar · peak {max:N0}", a.Right, a.Y, p.Fg3, 11.5, right: true);
        var top = a.Y + legendH;
        var plot = new Rect(a.X, top, a.Width, H);
        _timelineRect = plot;
        _timelineSpan = (tl.Start, tl.Start + n * tl.BucketMs);
        var bw = a.Width / n;
        var gap = n > 200 ? 0 : 1;
        foreach (var b in r.Bursts)
        {
            var x0 = a.X + (b.From - tl.Start) / (double)tl.BucketMs * bw;
            var x1 = a.X + (b.To - tl.Start) / (double)tl.BucketMs * bw;
            ctx.FillRectangle(new SolidColorBrush(((ISolidColorBrush)p.Error).Color, 0.1), new Rect(x0, top, Math.Max(2, x1 - x0), H));
        }
        for (var i = 0; i < n; i++)
        {
            var x = a.X + i * bw;
            var y = top + H;
            for (var k = Series.Length - 1; k >= 0; k--)
            {
                var v = sums[i][k];
                if (v == 0) continue;
                var hgt = Math.Max(1, v / (double)max * (H - 4));
                y -= hgt;
                ctx.FillRectangle(Series[k].Color(p), new Rect(x + gap / 2.0, y, Math.Max(0.6, bw - gap), hgt));
            }
            var from = tl.Start + i * tl.BucketMs;
            var lines = new List<string> { $"{Fmt.Time(from)} – {Fmt.Time(from + tl.BucketMs, false)}", $"{sums[i].Sum():N0} entries" };
            for (var k = 0; k < Series.Length; k++)
                if (sums[i][k] > 0) lines.Add($"{Series[k].Name}: {sums[i][k]:N0}");
            var burst = r.Bursts.FirstOrDefault(bb => from >= bb.From && from < bb.To);
            if (burst is not null) lines.Add($"Error burst: {burst.Errors:N0} errors, {burst.Factor:0}× usual");
            lines.Add("Click: show this bucket · drag: a range");
            AddHit(new Rect(x, top, bw, H), string.Join('\n', lines), _ => FilterTime?.Invoke(from, from + tl.BucketMs));
        }
        ctx.DrawLine(new Pen(p.Border), new Point(a.X, top + H + 0.5), new Point(a.Right, top + H + 0.5));
        var end = tl.Start + n * tl.BucketMs;
        foreach (var f in new[] { 0, 0.25, 0.5, 0.75, 1 })
        {
            var t = tl.Start + (long)(f * (end - tl.Start));
            var label = Fmt.Time(t, f == 0 || end - tl.Start > 86_400_000);
            var w = T(label, p.Fg3, 11).Width;
            var x = a.X + f * a.Width - (f == 0 ? 0 : f == 1 ? w : w / 2);
            Text(ctx, label, x, top + H + 2, p.Fg3, 11);
        }
        return legendH + H + tickH;
    }

    private sealed record BarRow(string Label, int Count, string Tip, Action<bool>? Act, IBrush? Color = null, (int N, IBrush Color)[]? Segs = null, string? Value = null);

    private double Bars(DrawingContext? ctx, Palette p, Rect a, IReadOnlyList<BarRow> rows, int of, bool share = true)
    {
        if (rows.Count == 0) return 0;
        if (ctx is null) return rows.Count * (BarH + 2);
        var max = Math.Max(1, rows.Max(r => r.Count));
        var labelW = Math.Max(80, a.Width * 0.38);
        var numW = 52.0;
        var pctW = share ? 44.0 : 0;
        var trackX = a.X + labelW + 8;
        var trackW = Math.Max(10, a.Width - labelW - numW - pctW - 24);
        var y = a.Y;
        foreach (var r in rows)
        {
            Text(ctx, r.Label, a.X + 4, y + 3, p.Fg, 12, maxW: labelW - 4);
            var x = trackX;
            if (r.Segs is { } segs)
                foreach (var (n, color) in segs.Where(s => s.N > 0))
                {
                    var w = Math.Max(2, n / (double)max * trackW);
                    ctx.DrawRectangle(color, null, new Rect(x, y + 6, w, 10), 0, 0);
                    x += w + 2;
                }
            else ctx.DrawRectangle(r.Color ?? p.Accent, null, new Rect(x, y + 6, Math.Max(2, r.Count / (double)max * trackW), 10), 2, 2);
            Text(ctx, r.Value ?? Fmt.Compact(r.Count), trackX + trackW + 8 + numW, y + 3, p.Fg, 12, right: true);
            if (share) Text(ctx, Fmt.Pct(r.Count, of), a.Right - 2, y + 3, p.Fg3, 12, right: true);
            AddHit(new Rect(a.X, y, a.Width, BarH), r.Tip, r.Act);
            y += BarH + 2;
        }
        return y - a.Y;
    }

    private double LevelBars(DrawingContext? ctx, Palette p, AnalyticsResult r, Rect a)
    {
        var total = r.Levels.Sum();
        var rows = LevelOrder.Where(l => r.Levels[(int)l] > 0).Select(l => new BarRow(LevelLabel[(int)l], r.Levels[(int)l],
            $"{LevelNames.Labels[(int)l]}: {r.Levels[(int)l]:N0} ({Fmt.Pct(r.Levels[(int)l], total)})\n{Hint}", sh => FilterLevel?.Invoke(l, !sh, false), LevelBrush(p, l))).ToList();
        return Bars(ctx, p, a, rows, total);
    }

    private static IBrush LevelBrush(Palette p, Level l) => l switch
    {
        Level.Fatal => p.FatalBg,
        Level.Error => p.Error,
        Level.Warn => p.Warn,
        Level.Info => p.Accent,
        Level.Debug => p.Debug,
        Level.Trace => p.Trace,
        _ => p.Fg3,
    };

    private double SourceBars(DrawingContext? ctx, Palette p, AnalyticsResult r, Rect a)
    {
        var total = r.Sources.Sum(s => s.Entries);
        var rows = r.Sources.Take(12).Select((s, i) =>
        {
            var errs = s.Levels[(int)Level.Error] + s.Levels[(int)Level.Fatal];
            var warn = s.Levels[(int)Level.Warn];
            return new BarRow(s.Name, s.Entries, $"{s.Name}\n{s.Entries:N0} entries · {errs:N0} errors · {warn:N0} warnings\n{Fmt.Time(s.TimeMin)} → {Fmt.Time(s.TimeMax)}", null,
                Segs: [(s.Entries - errs - warn, SourceColors[i % SourceColors.Length]), (warn, p.Warn), (errs, p.Error)]);
        }).ToList();
        return Bars(ctx, p, a, rows, total);
    }

    private double Columns(DrawingContext? ctx, Palette p, Rect a, int[] values, string[] labels, string[] tips, int[]? errors)
    {
        const double H = 110;
        if (ctx is null) return H;
        var max = Math.Max(1, values.Max());
        var n = values.Length;
        var w = a.Width / n;
        var plotH = H - 16;
        for (var i = 0; i < n; i++)
        {
            var x = a.X + i * w;
            var h = Math.Max(values[i] > 0 ? 1 : 0, values[i] / (double)max * plotH);
            ctx.DrawRectangle(p.Accent, null, new Rect(x + 1, a.Y + plotH - h, Math.Max(1, w - 2), h), 2, 2);
            if (errors is not null && values[i] > 0)
            {
                var eh = errors[i] / (double)values[i] * h;
                if (eh > 0) ctx.DrawRectangle(p.Error, null, new Rect(x + 1, a.Y + plotH - h, Math.Max(1, w - 2), eh), 2, 2);
            }
            if (labels[i].Length > 0)
            {
                var lw = T(labels[i], p.Fg3, 10.5).Width;
                Text(ctx, labels[i], x + w / 2 - lw / 2, a.Y + plotH + 2, p.Fg3, 10.5);
            }
            AddHit(new Rect(x, a.Y, w, plotH), tips[i]);
        }
        return H;
    }

    private double Hours(DrawingContext? ctx, Palette p, AnalyticsResult r, Rect a) =>
        Columns(ctx, p, a, r.Hours.Select(h => h[0]).ToArray(), Enumerable.Range(0, 24).Select(i => i % 3 == 0 ? i.ToString(CultureInfo.InvariantCulture) : "").ToArray(),
            r.Hours.Select((h, i) => $"{i:00}:00–{i:00}:59\n{h[0]:N0} entries · {h[2]:N0} errors · {h[1]:N0} warnings").ToArray(), r.Hours.Select(h => h[2]).ToArray());

    private double PatternList(DrawingContext? ctx, Palette p, AnalyticsResult r, Rect a, IReadOnlyList<AnalyticsPattern> list, string by)
    {
        if (list.Count == 0) return by == "count" ? 20 : 0;
        if (ctx is null) return list.Count * PtH;
        var of = Math.Max(1, r.Scanned);
        var spark = by == "count" && a.Width > 600;
        var extraW = by is "first" or "last" or "dur" ? Math.Min(170, a.Width * 0.32) : 46;
        var y = a.Y;
        foreach (var pt in list)
        {
            var value = by switch { "errors" => Fmt.Compact(pt.Errors), "dur" => Fmt.Ms(pt.DurAvg), _ => Fmt.Compact(pt.Count) };
            var extra = by switch
            {
                "first" => $"since {Fmt.Time(pt.First)}",
                "last" => $"last {Fmt.Time(pt.Last)}",
                "dur" => $"max {Fmt.Ms(pt.DurMax)} · {pt.DurCount:N0}×",
                _ => Fmt.Pct(pt.Count, of),
            };
            Text(ctx, value, a.X + 50, y + 4, p.Fg, 12, right: true);
            // level mix
            var lx = a.X + 58;
            ctx.FillRectangle(p.Bg3, new Rect(lx, y + 8, 46, 8));
            foreach (var l in LevelOrder)
            {
                var n = pt.Levels[(int)l];
                if (n <= 0) continue;
                var w = n / (double)Math.Max(1, pt.Count) * 46;
                ctx.FillRectangle(LevelBrush(p, l), new Rect(lx, y + 8, w, 8));
                lx += w;
            }
            var tplX = a.X + 114;
            var right = a.Right - extraW - 8 - (spark ? 128 : 0);
            Template(ctx, pt.Template, tplX, y + 4, right - tplX, p);
            if (spark)
            {
                var sx = a.Right - extraW - 128;
                var smax = Math.Max(1, pt.Spark.Max());
                var bw = 120.0 / pt.Spark.Length;
                for (var i = 0; i < pt.Spark.Length; i++)
                {
                    if (pt.Spark[i] == 0) continue;
                    var h = Math.Max(1, pt.Spark[i] / (double)smax * 18);
                    ctx.FillRectangle(p.Accent, new Rect(sx + i * bw, y + 3 + 18 - h, Math.Max(1, bw - 0.6), h));
                }
            }
            Text(ctx, extra, a.Right - 2, y + 5, p.Fg3, 11, right: true, maxW: extraW);
            var tip = string.Join('\n', new[]
            {
                pt.Sample,
                $"{pt.Count:N0} entries ({Fmt.Pct(pt.Count, of)}) · {pt.Errors:N0} errors · {pt.Levels[(int)Level.Warn]:N0} warnings",
                pt.First is not null ? $"{Fmt.Time(pt.First)} → {Fmt.Time(pt.Last)}" : "",
                pt.DurCount > 0 ? $"duration avg {Fmt.Ms(pt.DurAvg)}, max {Fmt.Ms(pt.DurMax)}" : "",
                "Click: show only this pattern · Shift+click: hide it",
            }.Where(s => s.Length > 0));
            var tpl = pt.Template;
            AddHit(new Rect(a.X, y, a.Width, PtH), tip, sh => FilterPattern?.Invoke(tpl, !sh));
            y += PtH;
        }
        return y - a.Y;
    }

    private double Exceptions(DrawingContext? ctx, Palette p, AnalyticsResult r, Rect a)
    {
        var of = r.Exceptions.Sum(e => e.Count);
        var rows = r.Exceptions.Take(12).Select(e => new BarRow(Regex.Replace(e.Name, @"^(?:[a-z_$][\w$]*\.)+", ""), e.Count,
            $"{e.Name}\n{e.Count:N0}× · {Fmt.Time(e.First)} → {Fmt.Time(e.Last)}\n{Hint}", sh => FilterText?.Invoke(e.Name, !sh), p.TkError)).ToList();
        return Bars(ctx, p, a, rows, of);
    }

    private double Bursts(DrawingContext? ctx, Palette p, AnalyticsResult r, Rect a)
    {
        var errs = r.Levels[(int)Level.Error] + r.Levels[(int)Level.Fatal];
        var rows = r.Bursts.Select(b => new BarRow($"{Fmt.Time(b.From)} · {Fmt.Span(b.To - b.From)}", b.Errors,
            $"{b.Errors:N0} errors of {b.Total:N0} entries, {b.Factor:0.0}× the usual rate\nClick: show this time range", _ => FilterTime?.Invoke(b.From, b.To), p.Error)).ToList();
        return Bars(ctx, p, a, rows, errs);
    }

    private double Gaps(DrawingContext? ctx, Palette p, AnalyticsResult r, Rect a)
    {
        var max = r.Gaps.Max(g => g.Ms);
        var rows = r.Gaps.Select(g => new BarRow(Fmt.Time(g.From), (int)Math.Min(int.MaxValue, g.Ms * 1000 / Math.Max(1, max)),
            $"No entries for {Fmt.Span(g.Ms)}\n{Fmt.Time(g.From)} → {Fmt.Time(g.To)}\nClick: jump there", _ => JumpTo?.Invoke(g.From), p.Fg3, Value: Fmt.Span(g.Ms))).ToList();
        return Bars(ctx, p, a, rows, 1, share: false);
    }

    private double Histogram(DrawingContext? ctx, Palette p, Rect a, AnalyticsNumeric num, Func<double, string> unit)
    {
        const double H = 60, statsH = 20;
        if (ctx is null) return H + 6 + statsH;
        var max = Math.Max(1, num.Hist.Max());
        var n = num.Hist.Length;
        var w = a.Width / n;
        for (var i = 0; i < n; i++)
        {
            var h = num.Hist[i] > 0 ? Math.Max(1, num.Hist[i] / (double)max * H) : 0;
            var x = a.X + i * w;
            ctx.DrawRectangle(p.TkNumber, null, new Rect(x + 1, a.Y + H - h, Math.Max(1, w - 2), h), 2, 2);
            var lo = num.HistStart + i * num.HistWidth;
            AddHit(new Rect(x, a.Y, w, H), $"{unit(lo)} – {unit(lo + num.HistWidth)}\n≈ {num.Hist[i]:N0} values");
        }
        var sx = a.X;
        foreach (var (k, v) in new[] { ("min", num.Min), ("p50", num.P50), ("p90", num.P90), ("p99", num.P99), ("max", num.Max), ("avg", num.Avg) })
        {
            sx += Text(ctx, k + " ", sx, a.Y + H + 6, p.Fg3, 11.5);
            sx += Text(ctx, unit(v), sx, a.Y + H + 6, p.Fg, 11.5) + 12;
            if (sx > a.Right - 40) break;
        }
        return H + 6 + statsH;
    }

    private double Fields(DrawingContext? ctx, Palette p, AnalyticsResult r, Rect a)
    {
        var fields = r.Fields.Where(f => f.Top.Count > 0).Take(24).ToList();
        var of = Math.Max(1, r.Scanned);
        var cols = Math.Max(1, (int)((a.Width + 18) / (300 + 18)));
        var w = (a.Width - (cols - 1) * 18) / cols;
        double FieldH(DrawingContext? c, AnalyticsField f, Rect at)
        {
            var y = at.Y;
            Text(c, f.Key, at.X, y, p.Fg, 12, bold: true, mono: true, maxW: at.Width);
            Text(c, $"{f.Distinct:N0}{(f.DistinctCapped ? "+" : "")} distinct · in {Fmt.Pct(f.Present, of)} of entries", at.X, y + 18, p.Fg2, 11.5, maxW: at.Width);
            y += 38;
            if (f.Numeric is { } num) y += Histogram(c, p, new Rect(at.X, y, at.Width, 0), num, Fmt.Value) + 4;
            var key = f.Key;
            y += Bars(c, p, new Rect(at.X, y, at.Width, 0), f.Top.Take(8).Select(t => new BarRow(t.Value, t.Count,
                $"{key} = {t.Value}\n{t.Count:N0} entries ({Fmt.Pct(t.Count, f.Present)})\n{Hint}", sh => FilterField?.Invoke(key, t.Value, !sh))).ToList(), f.Present);
            return y - at.Y;
        }
        var top = a.Y;
        for (var i = 0; i < fields.Count; i += cols)
        {
            var row = fields.Skip(i).Take(cols).ToList();
            var rh = row.Select((f, k) => FieldH(null, f, new Rect(a.X + k * (w + 18), top, w, 0))).Max();
            if (ctx is not null)
                for (var k = 0; k < row.Count; k++) FieldH(ctx, row[k], new Rect(a.X + k * (w + 18), top, w, 0));
            top += rh + 14;
        }
        return top - a.Y - 14;
    }

    private double Values(DrawingContext? ctx, Palette p, Rect a, IReadOnlyList<(string Value, int Count)> values, int of) =>
        Bars(ctx, p, a, values.Take(10).Select(v => new BarRow(v.Value, v.Count, $"{v.Value}\n{v.Count:N0} entries\n{Hint}", sh => FilterText?.Invoke(v.Value, !sh), p.TkId)).ToList(), Math.Max(1, of));

    // ----------------------------------------------------------- interaction

    private Hit? HitAt(Point pt)
    {
        for (var i = _hits.Count - 1; i >= 0; i--)
            if (_hits[i].Rect.Contains(pt)) return _hits[i];
        return null;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _pointer = e.GetPosition(this);
        if (_dragFrom is not null)
        {
            _dragTo = Math.Clamp(_pointer.X, _timelineRect.X, _timelineRect.Right);
            InvalidateVisual();
            return;
        }
        var h = HitAt(_pointer);
        Cursor = h?.Act is not null ? new Cursor(StandardCursorType.Hand) : _timelineRect.Contains(_pointer) ? new Cursor(StandardCursorType.Cross) : Cursor.Default;
        if (h == _hover && h is null) return;
        _hover = h;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_dragFrom is not null) return;
        _hover = null;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var pt = e.GetPosition(this);
        if (_timelineRect.Contains(pt))
        {
            _dragFrom = _dragTo = pt.X;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }
        if (HitAt(pt) is { Act: { } act })
        {
            e.Handled = true;
            act(e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragFrom is not { } from) return;
        _dragFrom = null;
        e.Pointer.Capture(null);
        var pt = e.GetPosition(this);
        var lo = Math.Min(from, _dragTo);
        var hi = Math.Max(from, _dragTo);
        InvalidateVisual();
        if (hi - lo >= 4)
        {
            FilterTime?.Invoke(At(lo), At(hi) + 1);
            return;
        }
        if (HitAt(pt) is { Act: { } act }) act(e.KeyModifiers.HasFlag(KeyModifiers.Shift));
    }
}

/// <summary>Number and time formatting of the analytics view.</summary>
public static class Fmt
{
    public static string Time(long? t, bool withDate = true)
    {
        if (t is not long v) return "–";
        var d = DateTimeOffset.FromUnixTimeMilliseconds(v).ToLocalTime();
        return d.ToString(withDate ? "yyyy-MM-dd HH:mm:ss" : "HH:mm:ss", CultureInfo.InvariantCulture);
    }

    public static string Span(double ms)
    {
        if (!(ms > 0)) return "0 s";
        if (ms < 1000) return $"{ms:0} ms";
        var s = ms / 1000;
        if (s < 60) return s < 10 ? $"{s:0.0} s" : $"{s:0} s";
        var m = s / 60;
        if (m < 60) return $"{Math.Floor(m)} min {Math.Round(s % 60)} s";
        var h = m / 60;
        if (h < 48) return $"{Math.Floor(h)} h {Math.Round(m % 60)} min";
        return $"{Math.Floor(h / 24)} d {Math.Round(h % 24)} h";
    }

    public static string Ms(double ms) => ms < 1 ? $"{ms:0.00} ms" : ms < 1000 ? (ms < 10 ? $"{ms:0.0} ms" : $"{ms:0} ms") : Span(ms);

    public static string Compact(double n) => n >= 1e9 ? $"{n / 1e9:0.0}B" : n >= 1e6 ? $"{n / 1e6:0.0}M" : n >= 1e4 ? $"{n / 1e3:0.0}k" : Math.Round(n).ToString("N0", CultureInfo.InvariantCulture);

    public static string Value(double x) => Math.Abs(x) >= 1e4 ? Compact(x) : x == Math.Floor(x) ? x.ToString("N0", CultureInfo.InvariantCulture) : x.ToString(Math.Abs(x) < 10 ? "0.00" : "0.0", CultureInfo.InvariantCulture);

    public static string Pct(double n, double of)
    {
        var p = of > 0 ? n / of * 100 : 0;
        return p == 0 ? "0%" : p < 0.1 ? "<0.1%" : p < 10 ? $"{p:0.0}%" : $"{p:0}%";
    }

    public static string Bytes(long n)
    {
        string[] u = ["B", "KB", "MB", "GB", "TB"];
        double v = n;
        var i = 0;
        while (v >= 1024 && i < u.Length - 1)
        {
            v /= 1024;
            i++;
        }
        return i == 0 ? $"{v:0} {u[i]}" : $"{v:0.0} {u[i]}";
    }
}
