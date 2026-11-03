using System.Text;
using System.Text.RegularExpressions;

namespace UltraLogViewer.Core;

/// <summary>
/// The offline "Extract fields…": guesses a custom-format regex with named groups from one sample line, by the same
/// token classes the message colouring uses (timestamp, level, IP, number, key=value, quoted string, logger). It reads
/// the line's header left to right and stops at the first word of free text, which becomes the message.
/// Keep in sync with src/core/aiRegexGuess.ts: both apps share the test vectors.
/// </summary>
public static class AiRegexGuess
{
    private const RegexOptions O = RegexOptions.CultureInvariant;

    // ISO-like dates with an optional time, a bare time of day, or a syslog "May  1 10:00:00".
    private static readonly Regex TimeRe = new(
        @"^(?:\d{4}[-/]\d{2}[-/]\d{2}(?:[T ]\d{2}:\d{2}(?::\d{2}(?:[.,]\d+)?)?(?:Z|[+-]\d{2}:?\d{2})?)?|\d{2}:\d{2}:\d{2}(?:[.,]\d+)?|[A-Z][a-z]{2} +\d{1,2} \d{2}:\d{2}:\d{2}(?:[.,]\d+)?)", O);
    private static readonly Regex LevelRe = new(@"^(?:TRACE|DEBUG|DBG|INFO|INFORMATION|NOTICE|WARN|WARNING|ERROR|ERR|FATAL|CRITICAL|CRIT|SEVERE|VERBOSE)$", O | RegexOptions.IgnoreCase);
    private static readonly Regex IpRe = new(@"^\d{1,3}(?:\.\d{1,3}){3}(?::\d{1,5})?$", O);
    private static readonly Regex NumberRe = new(@"^-?\d+(?:\.\d+)?$", O);
    private static readonly Regex KeyValueRe = new(@"^([A-Za-z_][\w.\-]*)=", O);
    // Logger names: com.acme.Service, Foo.Bar (identifiers with dots), or a capitalised "MyService:".
    private static readonly Regex DottedRe = new(@"^[A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*)+$", O);
    private static readonly Regex NameRe = new(@"^[A-Z][\w$]*$", O);
    private static readonly Regex PunctRe = new(@"^[^\w\s]+$", O);

    /// <summary>A regex anchored at ^, ending in a message group, that matches <paramref name="line"/>.</summary>
    public static string Guess(string line)
    {
        line = line.TrimEnd('\r', '\n');
        var sb = new StringBuilder("^");
        var names = new HashSet<string>();
        string Group(string name, string body)
        {
            var n = name;
            for (var i = 2; !names.Add(n); i++) n = name + i;
            return $"(?<{n}>{body})";
        }
        var pos = 0;
        var time = false;
        var level = false;
        var logger = false;
        var thread = false;
        var message = false;
        // Leading "[2024-05-01 10:00:00]" or a bare timestamp.
        var open = line.StartsWith('[') ? 1 : 0;
        var tm = TimeRe.Match(line[open..]);
        if (tm.Success && (open == 0 || line.Length > open + tm.Length && line[open + tm.Length] == ']'))
        {
            sb.Append(open == 1 ? @"\[" : "").Append(Group("time", Generalize(tm.Value))).Append(open == 1 ? @"\]" : "");
            pos = open * 2 + tm.Length;
            time = true;
        }
        while (pos < line.Length)
        {
            var ws = pos;
            while (pos < line.Length && char.IsWhiteSpace(line[pos])) pos++;
            if (pos >= line.Length) break;
            var sep = pos > ws ? @"\s+" : "";
            var c = line[pos];
            if (!time && TimeRe.Match(line[pos..]) is { Success: true } tm2 && (pos + tm2.Length == line.Length || !char.IsLetterOrDigit(line[pos + tm2.Length])))
            {
                time = true;
                sb.Append(sep).Append(Group("time", Generalize(tm2.Value)));
                pos += tm2.Length;
                continue;
            }
            if (c == '[' && line.IndexOf(']', pos + 1) is var close and > 0)
            {
                var inner = line[(pos + 1)..close];
                string body;
                if (!level && LevelRe.IsMatch(inner.Trim()))
                {
                    level = true;
                    body = @"\[\s*" + Group("level", "[A-Za-z]+") + @"\s*\]";
                }
                else if (!thread)
                {
                    thread = true;
                    body = @"\[" + Group("thread", @"[^\]]*") + @"\]";
                }
                else body = @"\[" + Group("field", @"[^\]]*") + @"\]";
                sb.Append(sep).Append(body);
                pos = close + 1;
                continue;
            }
            if (c == '"' && line.IndexOf('"', pos + 1) is var q and > 0)
            {
                sb.Append(sep).Append('"').Append(Group("text", "[^\"]*")).Append('"');
                pos = q + 1;
                continue;
            }
            var end = pos;
            while (end < line.Length && !char.IsWhiteSpace(line[end])) end++;
            var word = line[pos..end];
            // A trailing ":" or "," is kept as a literal, so "Db:" can be a logger and "3," a number.
            var tail = word.Length > 1 && word[^1] is ':' or ',' ? word[^1..] : "";
            var core = word[..^tail.Length];
            string? piece = null;
            if (PunctRe.IsMatch(word)) piece = Escape(word);
            else if (!level && LevelRe.IsMatch(core)) { level = true; piece = Group("level", "[A-Za-z]+") + Escape(tail); }
            else if (IpRe.IsMatch(core)) piece = core.Contains(':') ? Group("ip", @"\d{1,3}(?:\.\d{1,3}){3}") + ":" + Group("port", @"\d+") + Escape(tail) : Group("ip", @"\d{1,3}(?:\.\d{1,3}){3}") + Escape(tail);
            else if (NumberRe.IsMatch(core)) piece = Group("number", @"-?\d+(?:\.\d+)?") + Escape(tail);
            else if (KeyValueRe.Match(word) is { Success: true } kv)
            {
                var key = kv.Groups[1].Value;
                var rest = line[(pos + kv.Length)..];
                var name = Identifier(key);
                if (name == "message") name = "msg"; // the message group is the rest of the line
                if (rest.StartsWith('"') && rest.IndexOf('"', 1) is var kq and > 0)
                {
                    piece = Escape(key) + "=\"" + Group(name, "[^\"]*") + "\"";
                    end = pos + kv.Length + kq + 1;
                }
                else piece = Escape(key) + "=" + Group(name, @"\S*");
            }
            else if (!logger && (DottedRe.IsMatch(core) || tail == ":" && NameRe.IsMatch(core)))
            {
                logger = true;
                piece = Group("logger", @"[\w.$]+") + Escape(tail);
            }
            if (piece is null)
            {
                sb.Append(sep).Append(Group("message", ".*"));
                message = true;
                break;
            }
            sb.Append(sep).Append(piece);
            pos = end;
        }
        if (!message) sb.Append(@"\s*").Append(Group("message", ".*"));
        return sb.Append('$').ToString();
    }

    /// <summary>Digit runs become \d{n} (fractions \d+), month names [A-Z][a-z]{2}, spaces \s+; the rest is literal.</summary>
    internal static string Generalize(string s)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < s.Length;)
        {
            var c = s[i];
            var j = i;
            if (char.IsAsciiDigit(c))
            {
                while (j < s.Length && char.IsAsciiDigit(s[j])) j++;
                var frac = i > 1 && s[i - 1] is '.' or ',' && char.IsAsciiDigit(s[i - 2]);
                sb.Append(frac ? @"\d+" : j - i == 1 ? @"\d{1,2}" : $@"\d{{{j - i}}}");
            }
            else if (char.IsAsciiLetterUpper(c) && i + 2 < s.Length && char.IsAsciiLetterLower(s[i + 1]) && char.IsAsciiLetterLower(s[i + 2]))
            {
                j = i + 3;
                sb.Append("[A-Z][a-z]{2}");
            }
            else if (c == ' ')
            {
                while (j < s.Length && s[j] == ' ') j++;
                sb.Append(@"\s+");
            }
            else
            {
                j = i + 1;
                sb.Append(Escape(c.ToString()));
            }
            i = j;
        }
        return sb.ToString();
    }

    /// <summary>Escapes regex metacharacters the same way in .NET and JavaScript (no "\ " or "\#").</summary>
    public static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (@"\^$.|?*+()[]{}/".Contains(c)) sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>A group name valid in both engines: word characters, starting with a letter.</summary>
    public static string Identifier(string key)
    {
        var s = Regex.Replace(key, @"[^A-Za-z0-9_]", "_");
        if (s.Length == 0 || !char.IsAsciiLetter(s[0])) s = "f_" + s;
        return s;
    }
}
