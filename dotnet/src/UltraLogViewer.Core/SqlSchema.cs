using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace UltraLogViewer.Core;

/// <summary>A column of the <c>logs</c> table: a built-in one, or a parsed field (<paramref name="Field"/> is its key).</summary>
public sealed record SqlColumn(string Name, string? Field)
{
    public bool BuiltIn => Field is null;
}

public sealed record SqlExample(string Title, string Sql);

/// <param name="Columns">Result column names.</param>
/// <param name="Rows">At most the requested number of rows; values are long, double, string, byte[] or null.</param>
/// <param name="Truncated">More rows followed.</param>
/// <param name="Loaded">Entries in the table.</param>
/// <param name="Total">Entries in the document.</param>
/// <param name="Capped">Only the first <see cref="LogSql.Cap"/> entries were loaded.</param>
public sealed record SqlResult(IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows, bool Truncated, string? Error,
    double QueryMs, double LoadMs, int Loaded, int Total, bool Capped, bool Cancelled = false)
{
    public static SqlResult Fail(string error) => new([], [], false, error, 0, 0, 0, 0, false);
}

/// <summary>
/// The SQL side of the log table, shared by both apps: column names, the read-only guard, which columns a query
/// needs, and the example queries. The Electron app has the same rules in src/core/sql.ts.
/// </summary>
public static partial class SqlSchema
{
    /// <summary>A query (not the load) that runs longer than this is stopped.</summary>
    public static readonly TimeSpan TimeLimit = TimeSpan.FromMinutes(2);

    public static string TimeLimitText(TimeSpan limit) => $"The query ran for more than {Math.Round(limit.TotalSeconds)} s and was stopped";

    /// <summary>Always there; logger, thread, message and text (and the fields) are loaded only when a query names them.</summary>
    public static readonly string[] Core = ["row", "line", "source", "time", "time_iso", "level"];
    public static readonly string[] Optional = ["logger", "thread", "message", "text"];
    /// <summary>Parsed fields shown as built-in columns instead (time and level come from the index).</summary>
    private static readonly HashSet<string> ParsedBuiltIns = new(StringComparer.OrdinalIgnoreCase) { "time", "level", "logger", "thread", "message" };
    public const int MaxFields = 100;

    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "abort", "action", "add", "after", "all", "alter", "always", "analyze", "and", "as", "asc", "attach", "autoincrement", "before",
        "begin", "between", "by", "cascade", "case", "cast", "check", "collate", "column", "commit", "conflict", "constraint", "create",
        "cross", "current", "current_date", "current_time", "current_timestamp", "database", "default", "deferrable", "deferred", "delete",
        "desc", "detach", "distinct", "do", "drop", "each", "else", "end", "escape", "except", "exclude", "exclusive", "exists", "explain",
        "fail", "filter", "first", "following", "for", "foreign", "from", "full", "generated", "glob", "group", "groups", "having", "if",
        "ignore", "immediate", "in", "index", "indexed", "initially", "inner", "insert", "instead", "intersect", "into", "is", "isnull",
        "join", "key", "last", "left", "like", "limit", "match", "materialized", "natural", "no", "not", "nothing", "notnull", "null",
        "nulls", "of", "offset", "on", "or", "order", "others", "outer", "over", "partition", "plan", "pragma", "preceding", "primary",
        "query", "raise", "range", "recursive", "references", "regexp", "reindex", "release", "rename", "replace", "restrict", "returning",
        "right", "rollback", "row", "rows", "savepoint", "select", "set", "table", "temp", "temporary", "then", "ties", "to", "transaction",
        "trigger", "unbounded", "union", "unique", "update", "using", "vacuum", "values", "view", "virtual", "when", "where", "window",
        "with", "without",
    };

    /// <summary>
    /// A parsed field's key as a column name: runs of characters other than letters, digits and _ become one _
    /// ("http.status" → http_status), a leading digit gets "f_", and an SQL keyword gets a trailing _.
    /// </summary>
    public static string Sanitize(string key)
    {
        var sb = new StringBuilder(key.Length);
        foreach (var c in key)
        {
            if (c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_') sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '_') sb.Append('_');
        }
        var s = sb.ToString().Trim('_');
        if (s.Length == 0) return "field";
        if (char.IsAsciiDigit(s[0])) s = "f_" + s;
        if (Keywords.Contains(s)) s += "_";
        return s;
    }

    /// <summary>
    /// The table's columns: the built-in ones, then one per field key (keys the built-in columns already cover are
    /// left out; a name taken twice gets _2, _3…), at most <see cref="MaxFields"/> fields.
    /// </summary>
    public static List<SqlColumn> Build(IEnumerable<string> fieldKeys)
    {
        var cols = Core.Concat(Optional).Select(n => new SqlColumn(n, null)).ToList();
        var taken = new HashSet<string>(cols.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var k in fieldKeys)
        {
            if (cols.Count - Core.Length - Optional.Length >= MaxFields) break;
            if (k.Length == 0 || k.StartsWith("__", StringComparison.Ordinal) || ParsedBuiltIns.Contains(k) || !keys.Add(k)) continue;
            var name = Sanitize(k);
            var unique = name;
            for (var n = 2; !taken.Add(unique); n++) unique = $"{name}_{n}";
            cols.Add(new SqlColumn(unique, k));
        }
        return cols;
    }

    // ------------------------------------------------------------- tokens

    internal enum TokKind { Word, Quoted, String, Number, Punct }

    internal readonly record struct Tok(TokKind Kind, string Text);

    /// <summary>Splits SQL into tokens, dropping comments and whitespace. Unterminated strings run to the end.</summary>
    internal static List<Tok> Tokenize(string sql)
    {
        var list = new List<Tok>();
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? sql.Length : end + 2;
                continue;
            }
            if (c is '\'' or '"' or '`' or '[')
            {
                var close = c == '[' ? ']' : c;
                var sb = new StringBuilder();
                var j = i + 1;
                while (j < sql.Length)
                {
                    if (sql[j] == close)
                    {
                        if (close != ']' && j + 1 < sql.Length && sql[j + 1] == close)
                        {
                            sb.Append(close);
                            j += 2;
                            continue;
                        }
                        break;
                    }
                    sb.Append(sql[j++]);
                }
                list.Add(new Tok(c == '\'' ? TokKind.String : TokKind.Quoted, sb.ToString()));
                i = Math.Min(sql.Length, j + 1);
                continue;
            }
            if (IsWordStart(c))
            {
                var j = i + 1;
                while (j < sql.Length && (IsWordStart(sql[j]) || char.IsAsciiDigit(sql[j]) || sql[j] == '$')) j++;
                list.Add(new Tok(TokKind.Word, sql[i..j]));
                i = j;
                continue;
            }
            if (char.IsAsciiDigit(c))
            {
                var j = i + 1;
                while (j < sql.Length && (char.IsAsciiLetterOrDigit(sql[j]) || sql[j] is '.' or '_')) j++;
                list.Add(new Tok(TokKind.Number, sql[i..j]));
                i = j;
                continue;
            }
            list.Add(new Tok(TokKind.Punct, c.ToString()));
            i++;
        }
        return list;
    }

    private static bool IsWordStart(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '_' || c >= 0x80;

    private static readonly HashSet<string> Denied = new(StringComparer.OrdinalIgnoreCase)
    {
        "insert", "update", "delete", "replace", "create", "drop", "alter", "pragma", "vacuum", "reindex", "analyze",
        "begin", "commit", "rollback", "savepoint", "release", "attach", "detach",
    };

    /// <summary>
    /// Why the query may not run, or null: it must be one SELECT (or WITH / VALUES) statement, without anything that
    /// writes, opens other files (ATTACH) or loads extensions. The engines enforce this as well (an authorizer in .NET,
    /// a read-only connection in Electron); the check here gives a clear message first.
    /// </summary>
    public static string? Guard(string sql)
    {
        var toks = Tokenize(sql);
        var semi = toks.FindIndex(t => t is { Kind: TokKind.Punct, Text: ";" });
        if (semi >= 0)
        {
            if (toks.Skip(semi + 1).Any(t => t is not { Kind: TokKind.Punct, Text: ";" })) return "Run one statement at a time.";
            toks = toks.Take(semi).ToList();
        }
        if (toks.Count == 0) return "Enter a query.";
        for (var i = 0; i < toks.Count; i++)
        {
            if (toks[i].Kind != TokKind.Word) continue;
            var w = toks[i].Text.ToLowerInvariant();
            var call = i + 1 < toks.Count && toks[i + 1] is { Kind: TokKind.Punct, Text: "(" };
            if (w == "load_extension") return "load_extension is not allowed.";
            if (w is "attach" or "detach") return $"{w.ToUpperInvariant()} is not allowed: queries cannot open other files.";
            if (Denied.Contains(w) && !(w == "replace" && call)) return $"{w.ToUpperInvariant()} is not allowed: the table is read-only.";
        }
        var first = toks[0].Kind == TokKind.Word ? toks[0].Text.ToLowerInvariant() : "";
        if (first is not ("select" or "with" or "values")) return "Only SELECT queries can run here: the table is read-only.";
        return null;
    }

    /// <summary>
    /// The identifiers a query names (lower case), to load only the columns it uses. <paramref name="star"/> is set
    /// when it selects * (or t.*), which needs every column.
    /// </summary>
    public static HashSet<string> Mentioned(string sql, out bool star)
    {
        var toks = Tokenize(sql);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        star = false;
        for (var i = 0; i < toks.Count; i++)
        {
            var t = toks[i];
            if (t.Kind is TokKind.Word or TokKind.Quoted) set.Add(t.Text.ToLowerInvariant());
            else if (t is { Kind: TokKind.Punct, Text: "*" } && i > 0)
            {
                var p = toks[i - 1];
                if (p is { Kind: TokKind.Punct, Text: "," or "." } || p.Kind == TokKind.Word && p.Text.ToLowerInvariant() is "select" or "distinct" or "all")
                    star = true;
            }
        }
        return set;
    }

    // ------------------------------------------------------------- values

    /// <summary>A field value stored as a number: an integer or decimal without leading zeros (so ids like 007 stay text).</summary>
    public static object? Value(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        if (!NumberRe().IsMatch(s)) return s;
        if (!s.Contains('.') && long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)) return l;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : s;
    }

    [GeneratedRegex(@"^-?(0|[1-9][0-9]{0,14})(\.[0-9]{1,15})?$")]
    private static partial Regex NumberRe();

    /// <summary>A result the chart can draw: a label column, then a number in every row that has a value.</summary>
    public static bool Chartable(SqlResult r) =>
        r.Error is null && r.Columns.Count >= 2 && r.Rows.Count > 0 && r.Rows.All(x => x[1] is null or long or double) && r.Rows.Any(x => x[1] is long or double);

    // ----------------------------------------------------------- examples

    public const string DefaultQuery = "SELECT level, count(*) AS entries\nFROM logs\nGROUP BY level\nORDER BY entries DESC";

    [GeneratedRegex("duration|elapsed|latency|took|response_?time|(^|_)ms$", RegexOptions.IgnoreCase)]
    private static partial Regex DurationRe();

    /// <summary>Example queries for the dropdown; "slowest" uses a parsed duration field when the format has one.</summary>
    public static List<SqlExample> Examples(IReadOnlyList<SqlColumn> cols)
    {
        var dur = cols.FirstOrDefault(c => !c.BuiltIn && DurationRe().IsMatch(c.Name))?.Name;
        var slowest = dur is not null
            ? $"SELECT line, time_iso, {dur} AS duration, message\nFROM logs\nWHERE {dur} IS NOT NULL\nORDER BY CAST({dur} AS REAL) DESC\nLIMIT 50"
            : "SELECT line, time_iso, CAST(regexp_extract(message, '(\\d+(?:\\.\\d+)?) ?ms\\b') AS REAL) AS ms, message\nFROM logs\nWHERE ms IS NOT NULL\nORDER BY ms DESC\nLIMIT 50";
        return
        [
            new("Top loggers by errors", "SELECT logger, count(*) AS errors\nFROM logs\nWHERE level IN ('ERROR', 'FATAL')\nGROUP BY logger\nORDER BY errors DESC\nLIMIT 20"),
            new("Errors per minute", "SELECT strftime('%Y-%m-%d %H:%M', time / 1000, 'unixepoch') AS minute, count(*) AS errors\nFROM logs\nWHERE level IN ('ERROR', 'FATAL') AND time IS NOT NULL\nGROUP BY minute\nORDER BY minute"),
            new("Slowest requests", slowest),
            new("Longest gaps between entries (LAG)", "SELECT line, time_iso, time - LAG(time) OVER (ORDER BY time, line) AS gap_ms, message\nFROM logs\nWHERE time IS NOT NULL\nORDER BY gap_ms DESC\nLIMIT 50"),
            new("Levels in the filtered view", "SELECT level, count(*) AS entries,\n  round(100.0 * count(*) / sum(count(*)) OVER (), 1) AS pct\nFROM view\nGROUP BY level\nORDER BY entries DESC"),
        ];
    }
}
