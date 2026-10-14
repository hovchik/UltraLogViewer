using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using UltraLogViewer.Core;

namespace UltraLogViewer.Desktop;

/// <summary>Colors for the custom-drawn controls, matching the light/dark theme variant.</summary>
public sealed class Palette
{
    public required IBrush Bg, Bg2, Bg3, Fg, Fg2, Fg3, Border, Accent, AccentBg, Sel, SelFg, RowAlt;
    public required IBrush Trace, Debug, Info, Warn, WarnBg, Error, ErrorBg, Fatal, FatalBg, Hit, HitCur, Context, Bookmark;

    private static IBrush B(string hex) => new ImmutableSolidColorBrush(Color.Parse(hex));

    public static readonly Palette Light = new()
    {
        Bg = B("#FFFFFF"), Bg2 = B("#F5F6F8"), Bg3 = B("#ECEEF2"), Fg = B("#1D2129"), Fg2 = B("#5A6270"), Fg3 = B("#8A919E"), Border = B("#DDE0E6"),
        Accent = B("#2F6FDE"), AccentBg = B("#E3ECFC"), Sel = B("#CFE0FB"), SelFg = B("#0D1B33"), RowAlt = B("#FAFBFC"),
        Trace = B("#9AA1AD"), Debug = B("#6B7480"), Info = B("#1D2129"), Warn = B("#A35C00"), WarnBg = B("#FFF6E5"), Error = B("#C4251C"),
        ErrorBg = B("#FDECEA"), Fatal = B("#FFFFFF"), FatalBg = B("#B3261E"), Hit = B("#FFE066"), HitCur = B("#FF9F1C"), Context = B("#A0A6B0"),
        Bookmark = B("#2F6FDE"),
    };

    public static readonly Palette Dark = new()
    {
        Bg = B("#14161A"), Bg2 = B("#1B1E23"), Bg3 = B("#252930"), Fg = B("#D9DDE4"), Fg2 = B("#A2A9B4"), Fg3 = B("#6F7784"), Border = B("#2E333B"),
        Accent = B("#5B93F0"), AccentBg = B("#1F2D45"), Sel = B("#26406B"), SelFg = B("#FFFFFF"), RowAlt = B("#171A1E"),
        Trace = B("#636B77"), Debug = B("#8A929E"), Info = B("#D9DDE4"), Warn = B("#F0B04A"), WarnBg = B("#2F2615"), Error = B("#FF6B61"),
        ErrorBg = B("#3A1C1B"), Fatal = B("#FFFFFF"), FatalBg = B("#9C2A22"), Hit = B("#7A6514"), HitCur = B("#C46D00"), Context = B("#5C6470"),
        Bookmark = B("#5B93F0"),
    };

    public static Palette For(ThemeVariant? v) => v == ThemeVariant.Dark ? Dark : Light;

    public static Palette Current => For(Application.Current?.ActualThemeVariant);

    public IBrush LevelFg(Level l) => l switch
    {
        Level.Trace => Trace,
        Level.Debug => Debug,
        Level.Warn => Warn,
        Level.Error => Error,
        Level.Fatal => Fatal,
        _ => Info,
    };

    public IBrush? LevelBg(Level l) => l switch
    {
        Level.Warn => WarnBg,
        Level.Error => ErrorBg,
        Level.Fatal => FatalBg,
        _ => null,
    };

    public IBrush LevelSwatch(Level l) => l switch
    {
        Level.Trace => Trace,
        Level.Debug => Debug,
        Level.Info => Accent,
        Level.Warn => Warn,
        Level.Error => Error,
        Level.Fatal => FatalBg,
        _ => Fg3,
    };
}

public static class Fonts
{
    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Menlo, SF Mono, DejaVu Sans Mono, Liberation Mono, monospace");
    public static readonly Typeface MonoFace = new(Mono);
    public static readonly Typeface MonoBold = new(Mono, FontStyle.Normal, FontWeight.SemiBold);
}
