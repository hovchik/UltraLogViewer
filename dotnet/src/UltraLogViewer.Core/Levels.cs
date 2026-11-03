using System.Globalization;
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

    [GeneratedRegex(@"(?:^|[\[<(|:\s])(trace|debug|info|notice|warn(?:ing)?|error|err|fatal|crit(?:ical)?|alert|emerg)[\]>)|:]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DelimitedRe();

    /// <summary>The original regex implementation of <see cref="Detect"/>, kept as the reference the fast one is tested against.</summary>
    internal static Level DetectWithRegex(ReadOnlySpan<char> text)
    {
        var head = text.Length > 240 ? text[..240] : text;
        foreach (var m in BareRe().EnumerateMatches(head)) return Parse(head.Slice(m.Index, m.Length).ToString());
        foreach (var m in DelimitedRe().EnumerateMatches(head))
        {
            // the match starts with its delimiter, except at the start of the text ("error: ...")
            var lead = char.IsLetter(head[m.Index]) ? 0 : 1;
            return Parse(head.Slice(m.Index + lead, m.Length - lead - 1).ToString());
        }
        return Level.None;
    }

    private static readonly Level[] BareLevels = [Level.Trace, Level.Trace, Level.Trace, Level.Trace, Level.Debug, Level.Debug, Level.Info, Level.Info, Level.Info,
        Level.Warn, Level.Warn, Level.Error, Level.Error, Level.Error, Level.Fatal, Level.Fatal, Level.Fatal, Level.Fatal, Level.Fatal];
    private static readonly string[] BareWords = ["TRACE", "VERBOSE", "FINEST", "FINER", "DEBUG", "FINE", "INFO", "INFORMATION", "NOTICE",
        "WARN", "WARNING", "ERROR", "ERR", "SEVERE", "CRITICAL", "CRIT", "FATAL", "PANIC", "EMERG"];
    // In the regex's alternation order: at one position the first alternative that is followed by a delimiter wins.
    private static readonly string[] DelimWords = ["trace", "debug", "info", "notice", "warning", "warn", "error", "err", "fatal", "critical", "crit", "alert", "emerg"];
    private static readonly Level[] DelimLevels = [Level.Trace, Level.Debug, Level.Info, Level.Info, Level.Warn, Level.Warn, Level.Error, Level.Error, Level.Fatal,
        Level.Fatal, Level.Fatal, Level.Fatal, Level.Fatal];

    /// <summary>A regex \w character (letters, non-spacing marks, decimal digits, connector punctuation).</summary>
    private static bool IsWordChar(char c)
    {
        if (c < 128) return (uint)((c | 0x20) - 'a') <= 'z' - 'a' || (uint)(c - '0') <= 9 || c == '_';
        if (c is '\u200D' or '\u200C') return true; // joiners count as word characters for \b
        return char.GetUnicodeCategory(c) is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.NonSpacingMark
            or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.ConnectorPunctuation;
    }

    private static bool IsDelimiterBefore(char c) => c is '[' or '<' or '(' or '|' or ':' || char.IsWhiteSpace(c);
    private static bool IsDelimiterAfter(char c) => c is ']' or '>' or ')' or '|' or ':';

    /// <summary>
    /// Heuristic level detection for free-form text: looks at the beginning of the line only.
    /// First an upper-case level word standing alone (" WARN "), otherwise a level word of any case right before
    /// a delimiter ("[error]", "warning:"). Hand-written because it runs for every line of plain text files and
    /// regexes do not scale across indexing threads; <see cref="DetectWithRegex"/> is the specification.
    /// </summary>
    public static Level Detect(ReadOnlySpan<char> text)
    {
        var head = text.Length > 240 ? text[..240] : text;
        // Bare upper-case word: the first whole \w run that is one of the words.
        var i = 0;
        while (i < head.Length)
        {
            if (!IsWordChar(head[i]))
            {
                i++;
                continue;
            }
            var start = i;
            while (i < head.Length && IsWordChar(head[i])) i++;
            var len = i - start;
            if (len >= 3 && len <= 11 && (uint)(head[start] - 'C') <= 'W' - 'C')
            {
                var run = head.Slice(start, len);
                for (var w = 0; w < BareWords.Length; w++)
                    if (run.SequenceEqual(BareWords[w])) return BareLevels[w];
            }
        }
        // Delimited word of any case: at the start of the text or after a delimiter, and followed by one.
        for (var p = 0; p < head.Length; p++)
        {
            if (p == 0 && DelimitedAt(head, 0) is { } atStart) return atStart;
            if (IsDelimiterBefore(head[p]) && DelimitedAt(head, p + 1) is { } l) return l;
        }
        return Level.None;
    }

    /// <summary>Case-insensitive compare with a lower-case ASCII word.</summary>
    private static bool AsciiEqualsIgnoreCase(ReadOnlySpan<char> s, string lower)
    {
        for (var k = 0; k < lower.Length; k++)
        {
            var c = s[k];
            if (c >= 128 || (c | 0x20) != lower[k]) return false;
        }
        return true;
    }

    private static Level? DelimitedAt(ReadOnlySpan<char> s, int p)
    {
        if (p >= s.Length || ((s[p] | 0x20) - 'a') is < 0 or > 25) return null;
        var rest = s[p..];
        for (var w = 0; w < DelimWords.Length; w++)
        {
            var word = DelimWords[w];
            if (rest.Length > word.Length && AsciiEqualsIgnoreCase(rest[..word.Length], word) && IsDelimiterAfter(rest[word.Length]))
                return DelimLevels[w];
        }
        return null;
    }

    public static string Label(Level l) => LevelNames.Labels[(int)l];
}
