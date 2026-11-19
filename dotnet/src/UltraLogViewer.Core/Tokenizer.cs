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
    private static readonly (string Pattern, TokenKind Kind)[] Alternatives =
    [
        (@"\b(?:https?|ftp|wss?)://[^\s""'<>]+", TokenKind.Url),
        (@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", TokenKind.Guid),
        (@"\b[0-9]{4}-[0-9]{2}-[0-9]{2}(?:[T ][0-9]{2}:[0-9]{2}(?::[0-9]{2}(?:[.,][0-9]+)?)?(?:Z|[+-][0-9]{2}:?[0-9]{2})?)?\b|\b[0-9]{2}:[0-9]{2}:[0-9]{2}(?:[.,][0-9]+)?\b", TokenKind.Date),
        (@"\b(?:25[0-5]|2[0-4][0-9]|1?[0-9]?[0-9])(?:\.(?:25[0-5]|2[0-4][0-9]|1?[0-9]?[0-9])){3}(?::[0-9]{1,5})?\b", TokenKind.Ip),
        (@"(?<![\w/.])(?:/[\w.\-@~]+){2,}/?|\b[A-Za-z]:\\[^\s""'<>:*?|]+", TokenKind.Path),
        (@"\b[A-Za-z_][\w.$]*(?:Exception|Error)\b", TokenKind.Exception),
        (@"\b[A-Za-z_][\w.\-]*(?==)", TokenKind.Key),
        (@"""[^""\n]{0,200}""|(?<!\w)'[^'\n]{0,200}'(?!\w)", TokenKind.String),
        (@"\b[0-9]+(?:\.[0-9]+)?(?:ms|µs|us|ns|s|sec|m|min|h)\b", TokenKind.Duration),
        (@"(?<![\w.])-?[0-9]+(?:\.[0-9]+)?\b", TokenKind.Number),
        (@"\b(?:GET|POST|PUT|DELETE|PATCH|HEAD|OPTIONS)\b", TokenKind.Method),
        (@"\b(?:true|false|null|None|nil|undefined)\b", TokenKind.Keyword),
    ];

    private const RegexOptions Opts = RegexOptions.Compiled | RegexOptions.CultureInvariant;
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(500);

    /// <summary>One alternation of all the kinds: finds where the next token is.</summary>
    private static readonly Regex Re = new(string.Join("|", Alternatives.Select(a => $"(?:{a.Pattern})")), Opts, Timeout);

    /// <summary>
    /// Each kind on its own, anchored at the match: the first that matches there is the alternative the alternation
    /// took. Telling the kind this way keeps tokenizing free of Match and Group objects (it runs on every entry
    /// for the analytics and patterns, where those allocations were most of the garbage).
    /// </summary>
    private static readonly (Regex At, TokenKind Kind)[] Kinds = Alternatives.Select(a => (new Regex($@"\G(?:{a.Pattern})", Opts, Timeout), a.Kind)).ToArray();

    public static List<Token> Tokenize(string text, int maxTokens = 300)
    {
        var list = new List<Token>();
        if (string.IsNullOrEmpty(text)) return list;
        var s = text.Length > MaxLength ? text[..MaxLength] : text;
        try
        {
            foreach (var m in Re.EnumerateMatches(s))
            {
                if (list.Count >= maxTokens) break;
                var kind = TokenKind.Keyword;
                foreach (var (at, k) in Kinds)
                {
                    if (!at.IsMatch(s, m.Index)) continue;
                    kind = k;
                    break;
                }
                var len = m.Length;
                if (kind == TokenKind.Url)
                    while (len > 0 && ".,;:)]}".Contains(s[m.Index + len - 1])) len--;
                if (len > 0) list.Add(new Token(m.Index, len, kind));
            }
        }
        catch (RegexMatchTimeoutException)
        {
        }
        return list;
    }
}
