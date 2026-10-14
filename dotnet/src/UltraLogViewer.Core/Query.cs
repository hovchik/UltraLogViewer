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
    /// <c>field:value</c> or <c>field:"two words"</c> (inside a parsed column, e.g. <c>logger:Db</c>, <c>level:error</c>,
    /// <c>http.status:500</c>) and <c>/regex/</c>.
    /// </summary>
    public static List<Term> Parse(string q, bool caseSensitive = false)
    {
        var terms = new List<Term>();
        bool negateNext = false, orNext = false;
        foreach (Match m in TokenRe.Matches(q))
        {
            var raw = m.Value;
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
            var negate = negateNext;
            negateNext = false;
            string? field = null;
            TextPattern pat;
            if (m.Groups["fq"].Success)
            {
                negate |= m.Groups["neg"].Value == "-";
                field = m.Groups["field"].Value.ToLowerInvariant();
                pat = new(m.Groups["fq"].Value, CaseSensitive: caseSensitive);
            }
            else if (m.Groups["q"].Success) pat = new(m.Groups["q"].Value, CaseSensitive: caseSensitive);
            else if (m.Groups["re"].Success) pat = new(m.Groups["re"].Value, Regex: true, CaseSensitive: caseSensitive);
            else
            {
                var tok = raw;
                if (tok.Length > 1 && tok[0] == '-')
                {
                    negate = true;
                    tok = tok[1..];
                }
                var fm = FieldRe.Match(tok);
                if (fm.Success)
                {
                    field = fm.Groups[1].Value.ToLowerInvariant();
                    tok = fm.Groups[2].Value;
                }
                else if (BareFieldRe.IsMatch(tok)) continue; // a field name still being typed ("logger:") filters nothing yet
                pat = tok.Length > 1 && tok[0] == '/' && tok[^1] == '/' ? new(tok[1..^1], Regex: true, CaseSensitive: caseSensitive) : new(tok, CaseSensitive: caseSensitive);
            }
            if (pat.Text.Length == 0) continue;
            var alt = new Alt(field, pat);
            if (orNext && !negate) terms[^1].Any.Add(alt);
            else terms.Add(new Term(negate, [alt]));
            orNext = false;
        }
        return terms;
    }

    private static readonly Regex TokenRe = new(
        "(?<neg>-?)(?<field>[A-Za-z_][\\w.-]*):\"(?<fq>[^\"]*)\"?|\"(?<q>[^\"]*)\"?|/(?<re>(?:\\\\.|[^/])+)/|\\S+",
        RegexOptions.CultureInvariant);
    private static readonly Regex FieldRe = new(@"^([A-Za-z_][\w.-]*):(.+)$", RegexOptions.CultureInvariant);
    private static readonly Regex BareFieldRe = new(@"^[A-Za-z_][\w.-]*:$", RegexOptions.CultureInvariant);

    /// <summary>The whitespace-delimited token that the caret is in or just after (Start..Start+Length), for autocomplete.</summary>
    public static (int Start, int Length) TokenAt(string text, int caret)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        var start = caret;
        var quotes = 0;
        for (var i = 0; i < caret; i++) if (text[i] == '"') quotes++;
        // Inside an open quote, the token starts before the quote's field prefix.
        while (start > 0 && (!char.IsWhiteSpace(text[start - 1]) || quotes % 2 == 1))
        {
            start--;
            if (text[start] == '"') quotes--;
        }
        var end = caret;
        while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
        return (start, end - start);
    }

    /// <summary>Splits an autocomplete token into an optional "-" prefix, a field name (null if none yet) and the partial value.</summary>
    public static (string Prefix, string? Field, string Value) SplitToken(string token)
    {
        var prefix = token.StartsWith('-') ? "-" : "";
        var t = token[prefix.Length..];
        var colon = t.IndexOf(':');
        if (colon <= 0 || !FieldRe.IsMatch(t[..colon] + ":x")) return (prefix, null, t);
        return (prefix, t[..colon], t[(colon + 1)..].Trim('"'));
    }

    /// <summary>A <c>field:value</c> token, quoted when the value contains spaces or quotes.</summary>
    public static string FieldToken(string field, string value) =>
        value.Length > 0 && value.All(c => !char.IsWhiteSpace(c) && c != '"') ? $"{field}:{value}" : $"{field}:\"{value.Replace("\"", "")}\"";

    /// <summary>Matcher for a filter rule (exact rules compare the trimmed value).</summary>
    public static Func<string, bool> RuleMatcher(FilterRule r)
    {
        if (!r.Exact) return Matcher(r.Pattern);
        var cmp = r.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var needle = r.Text.Trim();
        return t => t.AsSpan().Trim().Equals(needle, cmp);
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
