using System.Globalization;
using System.Text.RegularExpressions;

namespace UltraLogViewer.Core;

/// <summary>A parsed quick-filter query (see <see cref="Query.ParseExpr"/>).</summary>
public abstract record QueryNode;
public sealed record AndNode(IReadOnlyList<QueryNode> Items) : QueryNode;
public sealed record OrNode(IReadOnlyList<QueryNode> Items) : QueryNode;
public sealed record NotNode(QueryNode Item) : QueryNode;
/// <summary>Text or regex inside the entry, or inside one parsed field (<c>field:value</c>).</summary>
public sealed record TextNode(string? Field, TextPattern Pattern) : QueryNode;
/// <summary><c>field op value</c> with op one of <c>&gt; &gt;= &lt; &lt;= == != ?=</c>.</summary>
public sealed record CompareNode(string Field, string Op, string Value) : QueryNode;
public sealed record InNode(string Field, IReadOnlyList<string> Values) : QueryNode;
public sealed record ExistsNode(string Field) : QueryNode;
/// <summary><c>since:</c> or <c>until:</c>, resolved to epoch ms and tested against the indexed entry time.</summary>
public sealed record TimeNode(bool Since, long Ms, string Text) : QueryNode;

/// <summary>
/// The quick-filter language. Old queries keep their meaning: words (all must appear), <c>a OR b</c>, <c>-word</c> /
/// <c>NOT word</c>, <c>"exact phrase"</c>, <c>field:value</c> / <c>field:"two words"</c> (contains, in a parsed field),
/// <c>/regex/</c>. New: <c>and</c>/<c>or</c>/<c>not</c> in either case with parentheses, comparisons
/// <c>status&gt;=500</c>, <c>duration&gt;2s</c>, <c>status!=200</c>, <c>user==bob</c>, <c>field?=x</c> (also matches
/// entries without the field), <c>user in (alice,bob)</c>, <c>exists(traceId)</c>, and <c>since:-3h</c> /
/// <c>until:2024-05-01T10:00</c>. OR binds tighter than AND (as it always has: <c>a b OR c</c> is a AND (b OR c)).
/// Input that is still being typed (a trailing operator, an unclosed parenthesis, <c>logger:</c>) filters nothing
/// rather than failing.
/// </summary>
public static partial class Query
{
    /// <summary>Pseudo-field answered from the index: the entry's time in epoch ms, or "" when it has none.</summary>
    public const string TimeField = "@time";

    private enum Tk { Atom, LParen, RParen, And, Or, Not }
    private readonly record struct Tok(Tk Kind, QueryNode? Node = null);

    private const string FieldStart = "[A-Za-z_@]", FieldChar = @"[\w.-]";
    private static readonly Regex RegexTok = new(@"\G/(?:\\.|[^/])+/", RegexOptions.CultureInvariant);
    private static readonly Regex QuotedFieldTok = new($@"\G({FieldStart}{FieldChar}*?)(:|>=|<=|==|!=|\?=|>|<)""([^""]*)""?", RegexOptions.CultureInvariant);
    private static readonly Regex FieldOpRe = new($@"^({FieldStart}{FieldChar}*?)(:|>=|<=|==|!=|\?=|>|<)(.*)$", RegexOptions.CultureInvariant | RegexOptions.Singleline);
    private static readonly Regex ExistsRe = new(@"^exists\(([^()]*)\)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex InRe = new(@"\G\s+(?:in|IN)\s*\(", RegexOptions.CultureInvariant);
    private static readonly Regex FieldNameRe = new($@"^{FieldStart}{FieldChar}*$", RegexOptions.CultureInvariant);
    private static readonly Regex DurationPart = new(@"\G(\d+(?:\.\d+)?|\.\d+)\s*([a-zµ]+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex NumberRe = new(@"^[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?$", RegexOptions.CultureInvariant);
    private static readonly Regex UnitLike = new(@"^[+-]?(?:\d+(?:\.\d+)?|\.\d+)\s*[A-Za-zµ]+$", RegexOptions.CultureInvariant);
    private static readonly Regex AbsTime = new(@"^(\d{4})-(\d{2})-(\d{2})(?:[T ](\d{2}):(\d{2})(?::(\d{2})(?:[.,](\d{1,9}))?)?)?\s*(Z|[+-]\d{2}:?\d{2})?$", RegexOptions.CultureInvariant);

    /// <summary>Parses a quick-filter query; null means "everything matches". Throws <see cref="ArgumentException"/> with a readable message.</summary>
    public static QueryNode? ParseExpr(string q, bool caseSensitive = false, long? now = null)
    {
        var toks = Lex(q, caseSensitive, now ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var pos = 0;
        return ParseAnd(toks, ref pos, false);
    }

    /// <summary>The error message for a query, or null when it is valid.</summary>
    public static string? ValidateQuery(string q, bool caseSensitive = false)
    {
        try
        {
            CompileQuery(q, caseSensitive);
            return null;
        }
        catch (ArgumentException e)
        {
            return e.Message;
        }
    }

    /// <summary>
    /// Compiles a quick-filter query. <c>field(name)</c> returns a parsed field value ("" when missing); it must answer
    /// "level" and <see cref="TimeField"/> from the index, because clauses on those run first and can reject an entry
    /// before anything has to parse it.
    /// </summary>
    public static Func<string, Func<string, string>, bool> CompileQuery(string q, bool caseSensitive = false, long? now = null)
    {
        var root = ParseExpr(q, caseSensitive, now);
        if (root is null) return (_, _) => true;
        var p = Build(root, caseSensitive).P;
        return (text, field) => p(text, field);
    }

    /// <summary>
    /// <see cref="CompileQuery"/> with the <c>since:</c>/<c>until:</c> clauses that every match must pass taken out as a
    /// time range, so the caller can test them with its time index like the time-range filter (no per-entry work at
    /// all for a query such as <c>since:-1h</c>). Predicate is null when nothing else is left to test.
    /// </summary>
    public static (Func<string, Func<string, string>, bool>? Predicate, long? From, long? To) CompileQueryWithTimeRange(string q, bool caseSensitive = false, long? now = null)
    {
        var root = ParseExpr(q, caseSensitive, now);
        long? from = null, to = null;
        var items = root is AndNode a ? a.Items.ToList() : root is null ? [] : [root];
        var rest = new List<QueryNode>();
        foreach (var n in items)
        {
            if (n is TimeNode { Since: true } s) from = Math.Max(from ?? long.MinValue, s.Ms);
            else if (n is TimeNode u) to = Math.Min(to ?? long.MaxValue, u.Ms);
            else rest.Add(n);
        }
        if (rest.Count == 0) return (null, from, to);
        var p = Build(rest.Count == 1 ? rest[0] : new AndNode(rest), caseSensitive).P;
        return ((text, field) => p(text, field), from, to);
    }

    // ------------------------------------------------------------------ lexer

    private static List<Tok> Lex(string q, bool cs, long now)
    {
        var toks = new List<Tok>();
        var depth = 0;
        var afterDash = false;
        var i = 0;
        while (i < q.Length)
        {
            var c = q[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                afterDash = false;
                continue;
            }
            var dash = afterDash;
            afterDash = false;
            if (c == '(')
            {
                toks.Add(new(Tk.LParen));
                depth++;
                i++;
                continue;
            }
            if (c == ')' && (depth > 0 || i + 1 == q.Length || char.IsWhiteSpace(q[i + 1])))
            {
                if (depth == 0) throw new ArgumentException("Unmatched \")\"");
                toks.Add(new(Tk.RParen));
                depth--;
                i++;
                continue;
            }
            if (c == '-' && !dash && i + 1 < q.Length && !char.IsWhiteSpace(q[i + 1]))
            {
                // "-x" negates; "--x" negates "-x" (as before).
                toks.Add(new(Tk.Not));
                afterDash = true;
                i++;
                continue;
            }
            if (c == '"')
            {
                var close = q.IndexOf('"', i + 1); // an unclosed quote is fine: the user is still typing
                var text = close < 0 ? q[(i + 1)..] : q[(i + 1)..close];
                i = close < 0 ? q.Length : close + 1;
                if (text.Length > 0) toks.Add(new(Tk.Atom, new TextNode(null, new(text, CaseSensitive: cs))));
                continue;
            }
            Match m;
            if (c == '/' && (m = RegexTok.Match(q, i)).Success)
            {
                i += m.Length;
                toks.Add(new(Tk.Atom, new TextNode(null, new(m.Value[1..^1], Regex: true, CaseSensitive: cs))));
                continue;
            }
            if ((m = QuotedFieldTok.Match(q, i)).Success)
            {
                i += m.Length;
                if (FieldNode(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, true, cs, now) is { } fn) toks.Add(new(Tk.Atom, fn));
                continue;
            }
            var end = i;
            while (end < q.Length && !char.IsWhiteSpace(q[end])) end++;
            var word = q[i..end];
            // ")" glued to a word closes a group ("(a OR b)"), unless the word opened it itself ("foo()").
            if (depth > 0)
            {
                var excess = word.Count(ch => ch == ')') - word.Count(ch => ch == '(');
                var strip = 0;
                while (strip < Math.Min(excess, depth) && word.Length - strip > 1 && word[^(strip + 1)] == ')') strip++;
                word = word[..^strip];
                end -= strip;
            }
            i = end;
            switch (word)
            {
                case "OR" or "or" or "|" or "||":
                    toks.Add(new(Tk.Or));
                    continue;
                case "AND" or "and" or "&&":
                    toks.Add(new(Tk.And));
                    continue;
                case "NOT" or "not":
                    toks.Add(new(Tk.Not));
                    continue;
            }
            if ((m = ExistsRe.Match(word)).Success)
            {
                var f = m.Groups[1].Value.Trim();
                if (f.Length == 0) throw new ArgumentException("exists() needs a field name, e.g. exists(traceId)");
                toks.Add(new(Tk.Atom, new ExistsNode(f.ToLowerInvariant())));
                continue;
            }
            if (word.StartsWith("exists(", StringComparison.OrdinalIgnoreCase) && !word.Contains(')')) continue; // still typing
            if ((m = FieldOpRe.Match(word)).Success && FieldNode(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, false, cs, now, out var skip) is var node && (node is not null || skip))
            {
                if (node is not null) toks.Add(new(Tk.Atom, node));
                continue;
            }
            if (FieldNameRe.IsMatch(word) && (m = InRe.Match(q, end)).Success)
            {
                var (values, next) = ReadList(q, end + m.Length);
                i = next;
                if (values is null) continue; // "user in (ali" is still being typed
                if (values.Count == 0) throw new ArgumentException($"{word} in (): add at least one value, e.g. {word} in (alice,bob)");
                toks.Add(new(Tk.Atom, new InNode(word.ToLowerInvariant(), values)));
                continue;
            }
            toks.Add(new(Tk.Atom, new TextNode(null, new(word, CaseSensitive: cs))));
        }
        return toks;
    }

    private static QueryNode? FieldNode(string field, string op, string value, bool quoted, bool cs, long now) =>
        FieldNode(field, op, value, quoted, cs, now, out _);

    /// <summary>The node for <c>field op value</c>. Null with <paramref name="skip"/> = nothing to filter yet; null without it = not a field token.</summary>
    private static QueryNode? FieldNode(string field, string op, string value, bool quoted, bool cs, long now, out bool skip)
    {
        skip = true;
        var f = field.ToLowerInvariant();
        if (op == ":")
        {
            if (f is "since" or "until")
            {
                if (value.Length == 0) return null;
                var t = ParseWhen(value, now) ?? throw new ArgumentException($"{f}:{value}: use a relative time such as -3h or -15m, or a date such as 2024-05-01T10:00");
                return new TimeNode(f == "since", t, value);
            }
            if (value.Length == 0) return null; // "logger:" is still being typed
            var regex = !quoted && value.Length > 1 && value[0] == '/' && value[^1] == '/';
            // An unquoted "*" makes the value a whole-value wildcard: logger:com.acme.* or thread:worker-*.
            if (!regex && !quoted && value.Contains('*')) return new TextNode(f, new(GlobRegex(value), Regex: true, CaseSensitive: cs));
            return new TextNode(f, regex ? new(value[1..^1], Regex: true, CaseSensitive: cs) : new(value, CaseSensitive: cs));
        }
        // "a->b" and "x.>y" are text, not comparisons.
        if (f.EndsWith('-') || f.EndsWith('.'))
        {
            skip = false;
            return null;
        }
        if (value.Length == 0) return null; // "status>=" is still being typed
        var node = new CompareNode(f, op, value);
        CompileCompare(node, cs); // reports bad units and levels now
        return node;
    }

    /// <summary>
    /// The regex of a <c>field:value</c> wildcard: "*" is any run of characters and the whole value must match. A
    /// trailing ".*" is a package (logger hierarchy): <c>com.acme.*</c> matches com.acme itself and everything below it.
    /// </summary>
    public static string GlobRegex(string glob)
    {
        var pkg = glob.Length > 2 && glob.EndsWith(".*", StringComparison.Ordinal) && !glob[..^2].Contains('*');
        if (pkg) return "^" + Regex.Escape(glob[..^2]) + @"(?:\..*)?$";
        return "^" + string.Join(".*", glob.Split('*').Select(Regex.Escape)) + "$";
    }

    /// <summary>Reads "a, b, "c d")" after "in (". Values is null when the list is not closed yet.</summary>
    private static (List<string>? Values, int Next) ReadList(string q, int i)
    {
        var values = new List<string>();
        var cur = new System.Text.StringBuilder();
        var quoted = false;
        void Flush()
        {
            var v = cur.ToString().Trim();
            if (v.Length > 0) values.Add(v);
            cur.Clear();
        }
        for (; i < q.Length; i++)
        {
            var c = q[i];
            if (c == '"') quoted = !quoted;
            else if (quoted) cur.Append(c);
            else if (c == ',') Flush();
            else if (c == ')')
            {
                Flush();
                return (values, i + 1);
            }
            else cur.Append(c);
        }
        return (null, q.Length);
    }

    // ----------------------------------------------------------------- parser

    // and := or ([AND] or)*    or := unary (OR unary)*    unary := (NOT | -) unary | "(" and ")" | atom
    private static QueryNode? ParseAnd(List<Tok> t, ref int pos, bool inGroup)
    {
        var items = new List<QueryNode>();
        while (pos < t.Count)
        {
            var k = t[pos].Kind;
            if (k == Tk.RParen && inGroup) break;
            if (k is Tk.And or Tk.Or or Tk.RParen)
            {
                pos++; // a stray operator (e.g. a leading OR) filters nothing, as before
                continue;
            }
            if (ParseOr(t, ref pos) is { } x) items.Add(x);
        }
        return items.Count switch { 0 => null, 1 => items[0], _ => new AndNode(items) };
    }

    private static QueryNode? ParseOr(List<Tok> t, ref int pos)
    {
        var items = new List<QueryNode>();
        if (ParseUnary(t, ref pos) is { } first) items.Add(first);
        while (pos < t.Count && t[pos].Kind == Tk.Or)
        {
            pos++;
            if (ParseUnary(t, ref pos) is { } x) items.Add(x);
        }
        return items.Count switch { 0 => null, 1 => items[0], _ => new OrNode(items) };
    }

    private static QueryNode? ParseUnary(List<Tok> t, ref int pos)
    {
        if (pos >= t.Count) return null;
        switch (t[pos].Kind)
        {
            case Tk.Not:
                pos++;
                return ParseUnary(t, ref pos) switch { null => null, NotNode n => n.Item, var x => new NotNode(x) };
            case Tk.LParen:
                pos++;
                var e = ParseAnd(t, ref pos, true);
                if (pos < t.Count && t[pos].Kind == Tk.RParen)
                {
                    pos++;
                    if (e is null) throw new ArgumentException("Empty parentheses \"()\"");
                }
                return e; // an unclosed "(" closes at the end
            case Tk.Atom:
                return t[pos++].Node;
            default:
                return null;
        }
    }

    /// <summary>A canonical text form of a parsed query, for tests and diagnostics.</summary>
    public static string Describe(QueryNode? n) => n switch
    {
        null => "",
        AndNode a => $"(and {string.Join(' ', a.Items.Select(Describe))})",
        OrNode o => $"(or {string.Join(' ', o.Items.Select(Describe))})",
        NotNode x => $"(not {Describe(x.Item)})",
        TextNode tn => (tn.Field is null ? "" : tn.Field + ":") + (tn.Pattern.Regex ? $"/{tn.Pattern.Text}/" : $"\"{tn.Pattern.Text}\""),
        CompareNode c => c.Field + c.Op + c.Value,
        InNode i => $"{i.Field} in ({string.Join(',', i.Values)})",
        ExistsNode e => $"exists({e.Field})",
        TimeNode tm => (tm.Since ? "since:" : "until:") + tm.Text,
        _ => "?",
    };

    // -------------------------------------------------------------- evaluator

    private delegate bool Pred(string text, Func<string, string> field);

    /// <summary>
    /// Cost classes: 0 = answered from the index (level, time), 1 = a scan of the raw text, 2 = needs the parsed
    /// entry. AND and OR evaluate their children cheapest first, so a level or time clause rejects an entry before
    /// any clause has to parse it.
    /// </summary>
    private static int FieldCost(string f) => f is "level" or TimeField ? 0 : 2;

    private static (Pred P, int Cost) Build(QueryNode n, bool cs)
    {
        switch (n)
        {
            case AndNode a:
            {
                var parts = a.Items.Select(x => Build(x, cs)).OrderBy(x => x.Cost).ToArray();
                var ps = parts.Select(x => x.P).ToArray();
                return ((text, f) =>
                {
                    foreach (var p in ps) if (!p(text, f)) return false;
                    return true;
                }, parts.Max(x => x.Cost));
            }
            case OrNode o:
            {
                var parts = o.Items.Select(x => Build(x, cs)).OrderBy(x => x.Cost).ToArray();
                var ps = parts.Select(x => x.P).ToArray();
                return ((text, f) =>
                {
                    foreach (var p in ps) if (p(text, f)) return true;
                    return false;
                }, parts.Max(x => x.Cost));
            }
            case NotNode x:
            {
                var (p, cost) = Build(x.Item, cs);
                return ((text, f) => !p(text, f), cost);
            }
            case TextNode t:
            {
                var m = Matcher(t.Pattern);
                if (t.Field is not { } key) return ((text, _) => m(text), 1);
                return ((_, f) => m(f(key)), FieldCost(key));
            }
            case CompareNode c:
            {
                var test = CompileCompare(c, cs);
                var key = c.Field;
                return ((_, f) => test(f(key)), FieldCost(key));
            }
            case InNode inn:
            {
                var tests = inn.Values.Select(v => CompileCompare(new CompareNode(inn.Field, "==", v), cs)).ToArray();
                var key = inn.Field;
                return ((_, f) =>
                {
                    var v = f(key);
                    foreach (var t in tests) if (t(v)) return true;
                    return false;
                }, FieldCost(key));
            }
            case ExistsNode e:
            {
                var key = e.Field;
                return ((_, f) => f(key).Length > 0, FieldCost(key));
            }
            case TimeNode tm:
            {
                var (since, ms) = (tm.Since, tm.Ms);
                return ((_, f) => long.TryParse(f(TimeField), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var t) && (since ? t >= ms : t <= ms), 0);
            }
            default:
                throw new ArgumentException("Unknown query node");
        }
    }

    private static bool Ordered(string op, int cmp) => op switch
    {
        ">" => cmp > 0,
        ">=" => cmp >= 0,
        "<" => cmp < 0,
        "<=" => cmp <= 0,
        _ => cmp == 0,
    };

    /// <summary>
    /// A test on one field value. Numbers compare numerically, durations by length (a bare number on one side is read
    /// in the other side's unit, so <c>query_time&gt;1s</c> matches "2.5"), dates as times, anything else as text.
    /// Every comparison except <c>?=</c> needs the field to be present.
    /// </summary>
    private static Func<string, bool> CompileCompare(CompareNode c, bool cs)
    {
        var (op, value) = (c.Op, c.Value);
        var equality = op is "==" or "!=" or "?=";
        Func<string, bool> Wrap(Func<string, int?> compare) => op switch
        {
            "!=" => s => s.Length > 0 && compare(s) is int r && r != 0,
            "?=" => s => s.Length == 0 || compare(s) == 0,
            _ => s => s.Length > 0 && compare(s) is int r && Ordered(op, r),
        };
        if (c.Field == "level")
        {
            var want = Levels.Parse(value);
            if (want == Level.None && !value.Equals("none", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"level{op}{value}: \"{value}\" is not a level (trace, debug, info, warn, error, fatal)");
            return Wrap(s => ((int)Levels.Parse(s)).CompareTo((int)want));
        }
        var strCmp = cs ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (TryQuantity(value, out var qv, out var qu))
        {
            return Wrap(s =>
            {
                if (!TryQuantity(s, out var fv, out var fu)) return equality ? (string.Equals(s.Trim(), value, strCmp) ? 0 : 1) : null;
                // a bare number takes the other side's unit
                if (fu == 0 && qu > 0) fv *= qu;
                var q = qu == 0 && fu > 0 ? qv * fu : qv;
                return fv.CompareTo(q);
            });
        }
        if (UnitLike.IsMatch(value) && !equality)
            throw new ArgumentException($"{c.Field}{op}{value}: unknown unit (use ms, s, m or h, e.g. {c.Field}{op}2s)");
        if (!equality && ParseAbsolute(value) is long qt)
            return Wrap(s => Timestamps.Parse(s) is var t && t != Timestamps.None ? t.CompareTo(qt) : (int?)null);
        return Wrap(s => string.Compare(s.Trim(), value, strCmp));
    }

    private static double UnitMs(string u) => u.ToLowerInvariant() switch
    {
        "ns" => 1e-6,
        "us" or "µs" => 1e-3,
        "ms" => 1,
        "s" or "sec" or "secs" => 1000,
        "m" or "min" or "mins" => 60_000,
        "h" or "hr" or "hrs" => 3_600_000,
        "d" => 86_400_000,
        "w" => 604_800_000,
        _ => 0,
    };

    /// <summary>
    /// A number, or a duration such as "250ms", "1.5s" or "2m30s" (value in ms). <paramref name="unit"/> is the length
    /// of the last unit in ms, or 0 for a bare number.
    /// </summary>
    internal static bool TryQuantity(string s, out double value, out double unit)
    {
        s = s.Trim();
        value = unit = 0;
        if (s.Length == 0) return false;
        if (NumberRe.IsMatch(s)) return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        var neg = s[0] == '-';
        var i = neg || s[0] == '+' ? 1 : 0;
        double total = 0;
        while (i < s.Length)
        {
            var m = DurationPart.Match(s, i);
            if (!m.Success) return false;
            var u = UnitMs(m.Groups[2].Value);
            if (u == 0) return false;
            total += double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * u;
            unit = u;
            i += m.Length;
        }
        if (unit == 0) return false;
        value = neg ? -total : total;
        return true;
    }

    /// <summary>"2024-05-01", "2024-05-01T10:00", "2024-05-01 10:00:05.5+02:00" (local time without an offset), or any timestamp the log parser knows.</summary>
    private static long? ParseAbsolute(string s)
    {
        var m = AbsTime.Match(s.Trim());
        if (m.Success)
        {
            int G(int g) => m.Groups[g].Success ? int.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture) : 0;
            var frac = m.Groups[7].Success ? int.Parse(m.Groups[7].Value.PadRight(3, '0')[..3], CultureInfo.InvariantCulture) : 0;
            int? tz = null;
            if (m.Groups[8].Success)
            {
                var z = m.Groups[8].Value.Replace(":", "");
                tz = z == "Z" ? 0 : (z[0] == '-' ? -1 : 1) * (int.Parse(z[1..3], CultureInfo.InvariantCulture) * 60 + int.Parse(z[3..5], CultureInfo.InvariantCulture));
            }
            var t = Timestamps.Build(G(1), G(2), G(3), G(4), G(5), G(6), frac, tz);
            return t == Timestamps.None ? null : t;
        }
        // Other log timestamp shapes, when the whole value is one and it carries a date (not "10:00:00" or "5").
        s = s.Trim();
        return Timestamps.TryMatch(s, DateTime.Now.Year, out var ms, out var len) && len == s.Length && ms > 366 * 86_400_000L ? ms : null;
    }

    /// <summary>The time a <c>since:</c>/<c>until:</c> value names: -3h, 15m (both "ago"), now, today, yesterday, or a date.</summary>
    internal static long? ParseWhen(string s, long now)
    {
        s = s.Trim();
        switch (s.ToLowerInvariant())
        {
            case "now":
                return now;
            case "today":
            case "yesterday":
                var local = DateTimeOffset.FromUnixTimeMilliseconds(now).ToLocalTime();
                var midnight = new DateTimeOffset(local.Date, TimeZoneInfo.Local.GetUtcOffset(local.Date)).ToUnixTimeMilliseconds();
                return s.Equals("today", StringComparison.OrdinalIgnoreCase) ? midnight : midnight - 86_400_000;
        }
        var rel = s.StartsWith('-') ? s[1..] : s;
        if (!NumberRe.IsMatch(rel) && TryQuantity(rel, out var ms, out var unit) && unit > 0) return now - (long)ms;
        return ParseAbsolute(s);
    }

    // ---------------------------------------------------------- autocomplete

    /// <summary>
    /// Splits an autocomplete token into a prefix ("-", "(" or "not("…), a field name (null if none yet), the
    /// operator after it (":" or a comparison) and the partial value.
    /// </summary>
    public static (string Prefix, string? Field, string Op, string Value) SplitToken(string token)
    {
        var p = 0;
        while (p < token.Length && token[p] is '-' or '(') p++;
        var prefix = token[..p];
        var t = token[p..];
        var m = FieldOpRe.Match(t);
        if (!m.Success || (m.Groups[2].Value != ":" && (m.Groups[1].Value.EndsWith('-') || m.Groups[1].Value.EndsWith('.')))) return (prefix, null, "", t);
        return (prefix, m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value.Trim('"'));
    }

    /// <summary>A <c>field:value</c> (or <c>field&gt;=value</c>…) token, quoted when the value contains spaces or quotes.</summary>
    public static string FieldToken(string field, string value, string op = ":") =>
        value.Length > 0 && value.All(c => !char.IsWhiteSpace(c) && c != '"' && c != '(' && c != ')') ? $"{field}{op}{value}" : $"{field}{op}\"{value.Replace("\"", "")}\"";

    /// <summary>Comparison operators offered by autocomplete after a complete field name.</summary>
    public static readonly string[] Operators = [":", ">=", "<=", ">", "<", "==", "!=", "?="];

    // ------------------------------------------------------- time shortcuts

    /// <summary>The time-filter shortcuts: "Now" sets To, the others set From and clear To.</summary>
    public static readonly (string Label, long Span)[] TimeShortcuts = [("Now", 0), ("-15m", 15 * 60_000L), ("-1h", 3_600_000L), ("-24h", 86_400_000L)];

    /// <summary>
    /// The moment time shortcuts count back from: the wall clock while following a live file (new lines are
    /// arriving now), otherwise the last entry, so "-1h" on yesterday's log shows its last hour rather than nothing.
    /// </summary>
    public static long ShortcutReference(long? lastEntry, bool live, long now) => live || lastEntry is null ? now : lastEntry.Value;

    public const string ShortcutTip = "Shortcuts count back from the last entry in the file, or from the clock while following a live file. Now sets To; the others set From and clear To.";
}
