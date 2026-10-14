using System.Text;
using System.Text.RegularExpressions;

namespace UltraLogViewer.Core;

/// <summary>
/// Message templates: the variable parts of a message (numbers, ids, IPs, times, quoted values, URLs) masked, so
/// entries that differ only in those parts group together. Also the keys used to fold repeated entries.
/// </summary>
public static class Patterns
{
    private static readonly Regex Hex = new(@"\b(?=[0-9a-fA-F]*[0-9])[0-9a-fA-F]{8,}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);
    private const string SepChars = " \t-:|";

    /// <summary>The parsed message, or the entry text without its leading timestamp.</summary>
    public static string MessageOf(string text, IReadOnlyDictionary<string, string> fields)
    {
        if (fields.TryGetValue("message", out var m) && m.Length > 0) return m;
        if (Timestamps.TryMatch(text, 2000, out _, out var len) && len > 0 && len <= text.Length)
            return text[len..].TrimStart(SepChars.ToCharArray());
        return text;
    }

    public static string Template(string message)
    {
        var nl = message.IndexOf('\n');
        var s = nl >= 0 ? message[..nl] : message;
        if (s.Length > 500) s = s[..500];
        var sb = new StringBuilder(s.Length);
        var pos = 0;
        foreach (var t in Tokenizer.Tokenize(s, 1000))
        {
            var ph = t.Kind switch
            {
                TokenKind.Number or TokenKind.Duration => "<num>",
                TokenKind.Guid => "<guid>",
                TokenKind.Ip => "<ip>",
                TokenKind.Date => "<time>",
                TokenKind.String => "<str>",
                TokenKind.Url => "<url>",
                _ => null,
            };
            if (ph is null) continue;
            sb.Append(s, pos, t.Start - pos).Append(ph);
            pos = t.Start + t.Length;
        }
        sb.Append(s, pos, s.Length - pos);
        var r = Hex.Replace(sb.ToString(), "<hex>");
        return Spaces.Replace(r, " ").Trim();
    }

    /// <summary>Entries with equal keys fold together in "group repeats" (mode "exact" or "pattern").</summary>
    public static string RepeatKey(Level level, IReadOnlyDictionary<string, string> fields, string text, string mode)
    {
        var msg = MessageOf(text, fields);
        if (mode == "pattern") return $"{(int)level}|{Template(msg)}";
        return $"{(int)level}|{(fields.TryGetValue("logger", out var lg) ? lg : "")}|{msg}";
    }

    /// <summary>64-bit FNV-1a.</summary>
    public static ulong Hash(string s)
    {
        var h = 14695981039346656037UL;
        foreach (var c in s)
        {
            h ^= c;
            h *= 1099511628211UL;
        }
        return h;
    }
}

public readonly record struct FoldedLine(string Line, int Folded);

/// <summary>Collapses runs of framework frames in stack traces into one placeholder line.</summary>
public static class StackFolding
{
    public static readonly string[] DefaultPackages =
    [
        "System.", "Microsoft.", "java.", "javax.", "jdk.", "sun.", "com.sun.", "org.springframework.", "org.apache.",
        "org.hibernate.", "kotlin.", "kotlinx.", "scala.", "akka.", "io.netty.", "reactor.", "node:internal",
        "node_modules/", "internal/", "<frozen", "site-packages/", "asyncio/", "django/", "werkzeug/",
    ];

    private static bool IsFrame(string line, out string body, out bool python)
    {
        var t = line.TrimStart();
        python = t.StartsWith("File \"", StringComparison.Ordinal);
        if (python)
        {
            body = t;
            return true;
        }
        if (t.StartsWith("at ", StringComparison.Ordinal))
        {
            body = t[3..].TrimStart();
            return true;
        }
        body = "";
        return false;
    }

    private static bool IsFramework(string body, IReadOnlyList<string> packages)
    {
        foreach (var p in packages)
        {
            if (p.Length == 0) continue;
            if (body.StartsWith(p, StringComparison.Ordinal) || body.Contains("(" + p, StringComparison.Ordinal)) return true;
            if ((p.Contains('/') || p.Contains(':') || p.StartsWith('<')) && body.Contains(p, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    public static List<FoldedLine> Fold(IReadOnlyList<string> lines, IReadOnlyList<string> packages)
    {
        var result = new List<FoldedLine>(lines.Count);
        var i = 0;
        while (i < lines.Count)
        {
            // collect a run of framework frames (with Python source lines attached)
            var j = i;
            var frames = 0;
            var onlyFrames = true;
            while (j < lines.Count && IsFrame(lines[j], out var body, out var py) && IsFramework(body, packages))
            {
                frames++;
                j++;
                if (py && j < lines.Count && !IsFrame(lines[j], out _, out _) && lines[j].Length > 0 && char.IsWhiteSpace(lines[j][0]))
                {
                    j++;
                    onlyFrames = false;
                }
            }
            if (frames >= 2)
            {
                var n = j - i;
                result.Add(new FoldedLine($"… {n} framework {(onlyFrames ? "frames" : "lines")}", n));
                i = j;
            }
            else if (j > i)
            {
                for (; i < j; i++) result.Add(new FoldedLine(lines[i], 0));
            }
            else
            {
                result.Add(new FoldedLine(lines[i], 0));
                i++;
            }
        }
        return result;
    }
}
