using System.Text.RegularExpressions;

namespace UltraLogViewer.Core;

public static partial class Query
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

    /// <summary>Matcher for a filter rule (exact rules compare the trimmed value).</summary>
    public static Func<string, bool> RuleMatcher(FilterRule r)
    {
        if (!r.Exact) return Matcher(r.Pattern);
        var cmp = r.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var needle = r.Text.Trim();
        return t => t.AsSpan().Trim().Equals(needle, cmp);
    }
}
