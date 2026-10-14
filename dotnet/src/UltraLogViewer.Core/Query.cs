using System.Text.RegularExpressions;

namespace UltraLogViewer.Core;

public static class Query
{
    /// <summary>Builds a regex for a pattern; throws <see cref="ArgumentException"/> on an invalid regex.</summary>
    public static Regex Compile(TextPattern p)
    {
        var src = p.Regex ? p.Text : Regex.Escape(p.Text);
        if (p.WholeWord) src = $@"\b(?:{src})\b";
        var opts = RegexOptions.CultureInvariant | (p.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
        return new Regex(src, opts, TimeSpan.FromSeconds(2));
    }

    public static string? Validate(TextPattern p)
    {
        try
        {
            Compile(p);
            return null;
        }
        catch (ArgumentException e)
        {
            return e.Message;
        }
    }

    /// <summary>A fast test function. Plain substring search avoids the regex engine where possible.</summary>
    public static Func<string, bool> Matcher(TextPattern p)
    {
        if (!p.Regex && !p.WholeWord)
        {
            var needle = p.Text;
            var cmp = p.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            return t => t.Contains(needle, cmp);
        }
        var re = Compile(p);
        return t => re.IsMatch(t);
    }

    /// <summary>All (start, length) matches of a regex in a string, for highlighting.</summary>
    public static List<(int Start, int Length)> Ranges(Regex re, string text, int limit = 500)
    {
        var list = new List<(int, int)>();
        try
        {
            foreach (var m in re.EnumerateMatches(text))
            {
                if (m.Length == 0) continue;
                list.Add((m.Index, m.Length));
                if (list.Count >= limit) break;
            }
        }
        catch (RegexMatchTimeoutException)
        {
        }
        return list;
    }

    public sealed record Alt(string? Field, TextPattern Pattern);
    public sealed record Term(bool Negate, List<Alt> Any);

    /// <summary>
    /// Quick-filter language:
    /// <c>words</c> (all must appear), <c>a OR b</c>, <c>-word</c> / <c>NOT word</c>, <c>"exact phrase"</c>,
    /// <c>field:value</c> (inside a parsed column, e.g. <c>logger:Db</c>, <c>level:error</c>) and <c>/regex/</c>.
    /// </summary>
    public static List<Term> Parse(string q, bool caseSensitive = false)
    {
        var tokens = new List<string>();
        foreach (Match m in Regex.Matches(q, "\"([^\"]*)\"|/((?:\\\\.|[^/])+)/|(\\S+)"))
            tokens.Add(m.Groups[1].Success ? "\"" + m.Groups[1].Value : m.Groups[2].Success ? "/" + m.Groups[2].Value : m.Groups[3].Value);
        var terms = new List<Term>();
        bool negateNext = false, orNext = false;
        foreach (var raw in tokens)
        {
            if (raw is "OR" or "|")
            {
                orNext = terms.Count > 0;
                continue;
            }
            if (raw == "AND") continue;
            if (raw == "NOT")
            {
                negateNext = true;
                continue;
            }
            var tok = raw;
            var negate = negateNext;
            negateNext = false;
            if (tok.Length > 1 && tok[0] == '-')
            {
                negate = true;
                tok = tok[1..];
            }
            string? field = null;
            var fm = Regex.Match(tok, @"^([A-Za-z_][\w-]*):(.+)$");
            if (fm.Success && tok[0] != '"' && tok[0] != '/')
            {
                field = fm.Groups[1].Value.ToLowerInvariant();
                tok = fm.Groups[2].Value;
            }
            TextPattern pat = tok[0] switch
            {
                '/' => new(tok[1..], Regex: true, CaseSensitive: caseSensitive),
                '"' => new(tok[1..], CaseSensitive: caseSensitive),
                _ => new(tok, CaseSensitive: caseSensitive),
            };
            var alt = new Alt(field, pat);
            if (orNext && !negate) terms[^1].Any.Add(alt);
            else terms.Add(new Term(negate, [alt]));
            orNext = false;
        }
        return terms;
    }

    /// <summary>Compiles a quick-filter query. <c>field(name)</c> returns a parsed column value.</summary>
    public static Func<string, Func<string, string>, bool> CompileQuery(string q, bool caseSensitive = false)
    {
        var terms = Parse(q, caseSensitive)
            .Select(t => (t.Negate, Alts: t.Any.Select(a => (a.Field, M: Matcher(a.Pattern))).ToArray()))
            .ToArray();
        if (terms.Length == 0) return (_, _) => true;
        return (text, field) =>
        {
            foreach (var (negate, alts) in terms)
            {
                var hit = false;
                foreach (var (f, m) in alts)
                {
                    if (f is null ? m(text) : m(field(f)))
                    {
                        hit = true;
                        break;
                    }
                }
                if (hit == negate) return false;
            }
            return true;
        };
    }
}
