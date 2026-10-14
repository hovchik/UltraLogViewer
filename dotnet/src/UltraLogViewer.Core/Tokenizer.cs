using System.Text.RegularExpressions;

namespace UltraLogViewer.Core;

public enum TokenKind { Url, Guid, Date, Ip, Path, Exception, Key, String, Duration, Number, Method, Keyword }

public readonly record struct Token(int Start, int Length, TokenKind Kind);

/// <summary>
/// Finds the parts of a log message worth colouring (dates, numbers, IPs, GUIDs, URLs, paths, key=value keys,
/// exception names, quoted strings, ...). One precompiled alternation, run only on the rows being drawn.
/// </summary>
public static class Tokenizer
{
    public const int MaxLength = 2000;

    // Alternatives in priority order: the first one that matches at a position wins.
    private static readonly Regex Re = new(
        @"(?<url>\b(?:https?|ftp|wss?)://[^\s""'<>]+)" +
        @"|(?<guid>\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b)" +
        @"|(?<date>\b[0-9]{4}-[0-9]{2}-[0-9]{2}(?:[T ][0-9]{2}:[0-9]{2}(?::[0-9]{2}(?:[.,][0-9]+)?)?(?:Z|[+-][0-9]{2}:?[0-9]{2})?)?\b|\b[0-9]{2}:[0-9]{2}:[0-9]{2}(?:[.,][0-9]+)?\b)" +
        @"|(?<ip>\b(?:25[0-5]|2[0-4][0-9]|1?[0-9]?[0-9])(?:\.(?:25[0-5]|2[0-4][0-9]|1?[0-9]?[0-9])){3}(?::[0-9]{1,5})?\b)" +
        @"|(?<path>(?<![\w/.])(?:/[\w.\-@~]+){2,}/?|\b[A-Za-z]:\\[^\s""'<>:*?|]+)" +
        @"|(?<exception>\b[A-Za-z_][\w.$]*(?:Exception|Error)\b)" +
        @"|(?<key>\b[A-Za-z_][\w.\-]*(?==))" +
        @"|(?<string>""[^""\n]{0,200}""|(?<!\w)'[^'\n]{0,200}'(?!\w))" +
        @"|(?<duration>\b[0-9]+(?:\.[0-9]+)?(?:ms|µs|us|ns|s|sec|m|min|h)\b)" +
        @"|(?<number>(?<![\w.])-?[0-9]+(?:\.[0-9]+)?\b)" +
        @"|(?<method>\b(?:GET|POST|PUT|DELETE|PATCH|HEAD|OPTIONS)\b)" +
        @"|(?<keyword>\b(?:true|false|null|None|nil|undefined)\b)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(500));

    private static readonly (string Group, TokenKind Kind)[] Groups =
    [
        ("url", TokenKind.Url), ("guid", TokenKind.Guid), ("date", TokenKind.Date), ("ip", TokenKind.Ip), ("path", TokenKind.Path),
        ("exception", TokenKind.Exception), ("key", TokenKind.Key), ("string", TokenKind.String), ("duration", TokenKind.Duration),
        ("number", TokenKind.Number), ("method", TokenKind.Method), ("keyword", TokenKind.Keyword),
    ];

    public static List<Token> Tokenize(string text, int maxTokens = 300)
    {
        var list = new List<Token>();
        if (string.IsNullOrEmpty(text)) return list;
        var s = text.Length > MaxLength ? text[..MaxLength] : text;
        try
        {
            for (var m = Re.Match(s); m.Success && list.Count < maxTokens; m = m.NextMatch())
            {
                foreach (var (name, kind) in Groups)
                {
                    var g = m.Groups[name];
                    if (!g.Success) continue;
                    var len = g.Length;
                    if (kind == TokenKind.Url)
                        while (len > 0 && ".,;:)]}".Contains(s[g.Index + len - 1])) len--;
                    if (len > 0) list.Add(new Token(g.Index, len, kind));
                    break;
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
        }
        return list;
    }
}
