using System.Text.RegularExpressions;

namespace UltraLogViewer.Core;

/// <summary>
/// Values picked for one field in the sidebar (Ctrl+click several loggers, or a package node), kept as one clause of the
/// quick-filter query so the query box always says what is shown: <c>logger in (a,b)</c>, <c>logger:com.acme.*</c>, or
/// <c>(logger in (a,b) or logger:com.acme.*)</c> when both kinds are picked. The clause is found again by its shape, so
/// the rest of the query is left exactly as the user typed it.
/// </summary>
public static partial class Query
{
    /// <summary>The values and package prefixes (without the "*", e.g. "com.acme.") picked for a field, read back from the query.</summary>
    public static (List<string> Values, List<string> Prefixes) FieldPicks(string query, string field)
    {
        var values = new List<string>();
        var prefixes = new List<string>();
        if (FindPickClause(query, field) is not { } m) return (values, prefixes);
        foreach (Match alt in AltRe(field).Matches(m.Value))
        {
            if (alt.Groups["list"].Success)
            {
                var (list, _) = ReadList(alt.Groups["list"].Value + ")", 0);
                if (list is not null) values.AddRange(list);
            }
            else prefixes.Add(alt.Groups["prefix"].Value);
        }
        return (values, prefixes);
    }

    /// <summary>The query with the field's pick clause replaced (or added, or removed when nothing is picked).</summary>
    public static string SetFieldPicks(string query, string field, IEnumerable<string> values, IEnumerable<string> prefixes)
    {
        var clause = PickClause(field, values.ToList(), prefixes.ToList());
        var m = FindPickClause(query, field);
        if (m is null) return clause.Length == 0 ? query : (query.TrimEnd() + " " + clause).Trim();
        var before = query[..m.Index].TrimEnd();
        var after = query[(m.Index + m.Length)..].TrimStart();
        return string.Join(" ", new[] { before, clause, after }.Where(s => s.Length > 0));
    }

    /// <summary>The clause for picked values and prefixes ("" when nothing is picked).</summary>
    public static string PickClause(string field, IReadOnlyList<string> values, IReadOnlyList<string> prefixes)
    {
        var parts = new List<string>();
        if (values.Count > 0) parts.Add(InClause(field, values));
        parts.AddRange(prefixes.Select(p => $"{field}:{p}*"));
        return parts.Count switch { 0 => "", 1 => parts[0], _ => "(" + string.Join(" or ", parts) + ")" };
    }

    /// <summary><c>field in (a,b)</c>, quoting values that contain spaces, commas or parentheses.</summary>
    public static string InClause(string field, IEnumerable<string> values) =>
        $"{field} in ({string.Join(",", values.Select(v => v.Any(c => char.IsWhiteSpace(c) || c is ',' or '(' or ')' or '"') ? "\"" + v.Replace("\"", "") + "\"" : v))})";

    /// <summary>
    /// Package prefixes of a dotted name for the hierarchy, outermost first: "com.acme.web.Api" gives "com.", "com.acme."
    /// and "com.acme.web.". Names without a dot have none.
    /// </summary>
    public static List<string> PackagePrefixes(string name)
    {
        var list = new List<string>();
        for (var i = name.IndexOf('.'); i > 0 && i < name.Length - 1; i = name.IndexOf('.', i + 1)) list.Add(name[..(i + 1)]);
        return list;
    }

    private static readonly Regex DottedName = new(@"^[A-Za-z_$][\w$-]*(?:\.[\w$-]+)+$", RegexOptions.CultureInvariant);

    /// <summary>A hierarchical name such as a logger or class (com.acme.Db), as opposed to a version or an IP address.</summary>
    public static bool IsDottedName(string value) => DottedName.IsMatch(value);

    private static string Alt(string f) => $@"{f}\s+in\s*\((?<list>(?:""[^""]*""|[^()""])*)\)|{f}:(?<prefix>[^\s()""*]+)\*";

    private static Regex AltRe(string field) => new(Alt(Regex.Escape(field)), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static Match? FindPickClause(string query, string field)
    {
        var a = Alt(Regex.Escape(field));
        var re = new Regex($@"(?<=^|\s)(?:\((?:{a})(?:\s+or\s+(?:{a}))*\)|(?:{a}))(?=$|\s)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var m = re.Match(query);
        return m.Success ? m : null;
    }
}
