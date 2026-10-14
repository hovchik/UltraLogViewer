using System.Text.RegularExpressions;

namespace UltraLogViewer.Core;

public static partial class Levels
{
    private static readonly Dictionary<string, Level> Words = new(StringComparer.Ordinal)
    {
        ["TRACE"] = Level.Trace, ["VERBOSE"] = Level.Trace, ["FINEST"] = Level.Trace, ["FINER"] = Level.Trace, ["TRC"] = Level.Trace, ["VRB"] = Level.Trace,
        ["DEBUG"] = Level.Debug, ["FINE"] = Level.Debug, ["DBG"] = Level.Debug, ["DBUG"] = Level.Debug,
        ["INFO"] = Level.Info, ["INFORMATION"] = Level.Info, ["NOTICE"] = Level.Info, ["INF"] = Level.Info, ["CONFIG"] = Level.Info,
        ["WARN"] = Level.Warn, ["WARNING"] = Level.Warn, ["WRN"] = Level.Warn,
        ["ERROR"] = Level.Error, ["ERR"] = Level.Error, ["SEVERE"] = Level.Error, ["ERRO"] = Level.Error, ["EROR"] = Level.Error,
        ["FATAL"] = Level.Fatal, ["CRITICAL"] = Level.Fatal, ["CRIT"] = Level.Fatal, ["PANIC"] = Level.Fatal, ["EMERG"] = Level.Fatal,
        ["EMERGENCY"] = Level.Fatal, ["ALERT"] = Level.Fatal, ["FTL"] = Level.Fatal,
    };

    private static readonly Dictionary<char, Level> Single = new()
    {
        ['T'] = Level.Trace, ['V'] = Level.Trace, ['D'] = Level.Debug, ['I'] = Level.Info, ['W'] = Level.Warn, ['E'] = Level.Error, ['F'] = Level.Fatal,
    };

    /// <summary>Parses a level token such as "WARN", "warning", "E" or a numeric pino/bunyan/syslog level.</summary>
    public static Level Parse(string? value)
    {
        if (string.IsNullOrEmpty(value)) return Level.None;
        if (Words.TryGetValue(value, out var direct)) return direct;
        var s = value.Trim();
        if (s.Length == 0) return Level.None;
        if (long.TryParse(s, out var n)) return FromNumber(n);
        var up = s.ToUpperInvariant();
        if (Words.TryGetValue(up, out var l)) return l;
        return up.Length == 1 && Single.TryGetValue(up[0], out var sl) ? sl : Level.None;
    }

    /// <summary>pino/bunyan (10..60) or syslog severity (0..7).</summary>
    public static Level FromNumber(long n)
    {
        if (n >= 10)
        {
            if (n >= 60) return Level.Fatal;
            if (n >= 50) return Level.Error;
            if (n >= 40) return Level.Warn;
            if (n >= 30) return Level.Info;
            if (n >= 20) return Level.Debug;
            return Level.Trace;
        }
        if (n <= 2) return Level.Fatal;
        if (n == 3) return Level.Error;
        if (n == 4) return Level.Warn;
        if (n <= 6) return Level.Info;
        return Level.Debug;
    }

    [GeneratedRegex(@"\b(TRACE|VERBOSE|FINEST|FINER|DEBUG|FINE|INFO|INFORMATION|NOTICE|WARN|WARNING|ERROR|ERR|SEVERE|CRITICAL|CRIT|FATAL|PANIC|EMERG)\b", RegexOptions.CultureInvariant)]
    private static partial Regex BareRe();

    [GeneratedRegex(@"[\[<(|:\s](trace|debug|info|notice|warn(?:ing)?|error|err|fatal|crit(?:ical)?|alert|emerg)[\]>)|:]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DelimitedRe();

    /// <summary>Heuristic level detection for free-form text: looks at the beginning of the line only.</summary>
    public static Level Detect(ReadOnlySpan<char> text)
    {
        var head = text.Length > 240 ? text[..240] : text;
        foreach (var m in BareRe().EnumerateMatches(head)) return Parse(head.Slice(m.Index, m.Length).ToString());
        foreach (var m in DelimitedRe().EnumerateMatches(head)) return Parse(head.Slice(m.Index + 1, m.Length - 2).ToString());
        return Level.None;
    }

    public static string Label(Level l) => LevelNames.Labels[(int)l];
}
