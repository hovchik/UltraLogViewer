using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UltraLogViewer.Core;

// Built-in formats for databases, Rust, macOS, Laravel and logfmt. Each regex is written from the format's
// documented default layout; the samples in the tests show what each one accepts.

/// <summary>logfmt <c>key=value</c> pairs: values are bare up to a space, or "quoted" with backslash escapes.</summary>
public static class Logfmt
{
    public static List<KeyValuePair<string, string>> Pairs(string line, bool requireEquals = false)
    {
        var list = new List<KeyValuePair<string, string>>();
        var i = 0;
        var sb = new StringBuilder();
        while (i < line.Length)
        {
            while (i < line.Length && line[i] is ' ' or '\t') i++;
            var ks = i;
            while (i < line.Length && line[i] is not (' ' or '\t' or '=' or '"')) i++;
            var key = line[ks..i];
            if (key.Length == 0)
            {
                if (i < line.Length) i++; // stray '=' or '"'
                continue;
            }
            if (i >= line.Length || line[i] != '=')
            {
                if (!requireEquals) list.Add(new(key, ""));
                continue;
            }
            i++;
            string value;
            if (i < line.Length && line[i] == '"')
            {
                sb.Clear();
                i++;
                while (i < line.Length && line[i] != '"')
                {
                    if (line[i] == '\\' && i + 1 < line.Length)
                    {
                        i++;
                        sb.Append(line[i] switch { 'n' => '\n', 't' => '\t', var c => c });
                    }
                    else sb.Append(line[i]);
                    i++;
                }
                i++;
                value = sb.ToString();
            }
            else
            {
                var vs = i;
                while (i < line.Length && line[i] is not (' ' or '\t')) i++;
                value = line[vs..i];
            }
            list.Add(new(key, value));
        }
        return list;
    }
}

public sealed partial class LogfmtFormat : LogFormat
{
    private static readonly string[] TimeKeys = ["ts", "time", "t", "timestamp", "@timestamp", "date", "datetime"];
    private static readonly string[] LevelKeys = ["level", "lvl", "severity", "loglevel", "log.level"];
    private static readonly string[] MessageKeys = ["msg", "message", "event"];
    private static readonly string[] LoggerKeys = ["logger", "component", "module", "name", "source"];

    [GeneratedRegex(@"^[A-Za-z_@][\w.@/-]*=")]
    private static partial Regex StartsWithPair();

    public override string Id => "logfmt";
    public override string Name => "logfmt (key=value: Go kit, logrus, Grafana, Heroku…)";
    public override IReadOnlyList<ColumnDef> Columns { get; } = [CTime, CLevel, CLogger, CMessage];

    /// <summary>A logfmt line: starts with key=value, has at least two pairs, and one of them is a time, level or message key.</summary>
    internal static bool Test(string line)
    {
        if (!StartsWithPair().IsMatch(line)) return false;
        var pairs = Logfmt.Pairs(line, requireEquals: true);
        return pairs.Count >= 2 && pairs.Any(p => Known(p.Key));
    }

    private static bool Known(string k)
    {
        var lk = k.ToLowerInvariant();
        return TimeKeys.Contains(lk) || LevelKeys.Contains(lk) || MessageKeys.Contains(lk);
    }

    private static string Pick(List<KeyValuePair<string, string>> pairs, string[] keys, out string? key)
    {
        foreach (var k in keys)
            foreach (var p in pairs)
                if (p.Key.Equals(k, StringComparison.OrdinalIgnoreCase))
                {
                    key = p.Key;
                    return p.Value;
                }
        key = null;
        return "";
    }

    public override QuickInfo Quick(string line)
    {
        var pairs = Logfmt.Pairs(line, requireEquals: true);
        if (pairs.Count == 0) return new QuickInfo(Levels.Detect(line), Timestamps.None);
        var lv = Pick(pairs, LevelKeys, out var lk);
        return new QuickInfo(lk is null ? Levels.Detect(line) : Levels.Parse(lv), Timestamps.Parse(Pick(pairs, TimeKeys, out _)));
    }

    public override Dictionary<string, string> Parse(string entry)
    {
        var pairs = Logfmt.Pairs(entry, requireEquals: true);
        var time = Pick(pairs, TimeKeys, out var tk);
        var level = Pick(pairs, LevelKeys, out var lk);
        var msg = Pick(pairs, MessageKeys, out var mk);
        var logger = Pick(pairs, LoggerKeys, out var gk);
        var lvl = lk is null ? L(Levels.Detect(entry)) : L(Levels.Parse(level));
        var d = new Dictionary<string, string>
        {
            ["time"] = time, ["level"] = lvl.Length > 0 ? lvl : level, ["logger"] = logger, ["message"] = mk is null ? entry : msg,
        };
        foreach (var p in pairs)
            if (p.Key != tk && p.Key != lk && p.Key != mk && p.Key != gk && d.Count < 60) d.TryAdd(p.Key, p.Value);
        return d;
    }
}

/// <summary>
/// Rust <c>tracing_subscriber::fmt</c> default output:
/// <c>2024-05-01T10:00:00.123456Z  INFO request{id=7}:db: my_app::store: saved rows=3</c>.
/// Span fields and trailing <c>key=value</c> fields become fields.
/// </summary>
public sealed partial class RustTracingFormat : LogFormat
{
    // Microsecond (or finer) RFC 3339 time, as tracing prints it; generic "ISO INFO name: text" lines stay "timestamped".
    [GeneratedRegex(@"^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{6,9}(?:Z|[+-]\d{2}:\d{2})) {1,2}(TRACE|DEBUG|INFO|WARN|ERROR) (?:((?:[\w-]+(?:\{[^}]*\})?)(?::[\w-]+(?:\{[^}]*\})?)*): )??([A-Za-z_]\w*(?:::\w+)*): (.*)$")]
    internal static partial Regex Re();
    [GeneratedRegex(@"(?<![\w.])([A-Za-z_][\w.]*)=(""(?:[^""\\]|\\.)*""|[^\s{}]+)")]
    private static partial Regex FieldRe();

    public override string Id => "rust";
    public override string Name => "Rust tracing (fmt)";
    public override IReadOnlyList<ColumnDef> Columns { get; } = [CTime, CLevel, Col("spans", "Spans", 160), Col("target", "Target", 180), CMessage];
    public override bool Multiline => true;
    public override bool IsEntryStart(string line) => Re().IsMatch(line);

    public override QuickInfo Quick(string line)
    {
        var m = Re().Match(line);
        return m.Success ? new QuickInfo(Levels.Parse(m.Groups[2].Value), Timestamps.Parse(m.Groups[1].Value)) : new QuickInfo(Levels.Detect(line), Timestamps.None);
    }

    public override Dictionary<string, string> Parse(string entry)
    {
        var m = Re().Match(FirstLine(entry));
        if (!m.Success) return new() { ["message"] = entry };
        var d = new Dictionary<string, string>
        {
            ["time"] = m.Groups[1].Value, ["level"] = L(Levels.Parse(m.Groups[2].Value)), ["spans"] = m.Groups[3].Value,
            ["target"] = m.Groups[4].Value, ["message"] = m.Groups[5].Value + RestLines(entry),
        };
        foreach (Match f in FieldRe().Matches(m.Groups[3].Value + " " + m.Groups[5].Value))
        {
            var v = f.Groups[2].Value;
            d.TryAdd(f.Groups[1].Value, v.Length > 1 && v[0] == '"' ? v[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\") : v);
        }
        return d;
    }
}

/// <summary>
/// PostgreSQL with the default <c>log_line_prefix = '%m [%p] '</c> (Debian: <c>'%m [%p] %q%u@%d '</c>, pgBadger:
/// <c>'%t [%p]: [%l-1] user=%u,db=%d,app=%a,client=%h '</c>). DETAIL/HINT/STATEMENT/CONTEXT lines belong to the
/// message above them, so an ERROR keeps its statement.
/// </summary>
public sealed partial class PostgresFormat : LogFormat
{
    [GeneratedRegex(@"^(?<time>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}(?:\.\d+)?)(?: (?<tz>[A-Z]{2,5}|[+-]\d{2}(?::?\d{2})?))? \[(?<pid>\d+)\](?::? \[\d+(?:-\d+)?\])? (?:user=(?<user>[^,\s]*),db=(?<db>[^,\s]*)(?:,app=(?<app>[^,]*?))?(?:,client=(?<client>\S*?))? |(?<user2>[^@\s]*)@(?<db2>\S*) )?(?<sev>DEBUG[1-5]?|LOG|INFO|NOTICE|WARNING|ERROR|FATAL|PANIC|DETAIL|HINT|STATEMENT|CONTEXT|QUERY|LOCATION):  ?(?<msg>.*)$")]
    internal static partial Regex Re();
    [GeneratedRegex(@"\bduration: (\d+(?:\.\d+)? ms)")]
    private static partial Regex Duration();

    public override string Id => "postgres";
    public override string Name => "PostgreSQL";
    public override IReadOnlyList<ColumnDef> Columns { get; } =
        [CTime, Col("pid", "PID", 64), Col("user", "User", 90), Col("db", "DB", 90), CLevel, CMessage];
    public override bool Multiline => true;

    private static bool IsDetail(string sev) => sev is "DETAIL" or "HINT" or "STATEMENT" or "CONTEXT" or "QUERY" or "LOCATION";

    public override bool IsEntryStart(string line) => Re().Match(line) is { Success: true } m && !IsDetail(m.Groups["sev"].Value);

    internal static Level Severity(string sev) => sev switch
    {
        "LOG" or "INFO" or "NOTICE" => Level.Info,
        "WARNING" => Level.Warn,
        "ERROR" => Level.Error,
        "FATAL" or "PANIC" => Level.Fatal,
        _ when sev.StartsWith("DEBUG", StringComparison.Ordinal) => Level.Debug,
        _ => Level.None,
    };

    private static long Time(Match m)
    {
        var tz = m.Groups["tz"].Value;
        var iso = m.Groups["time"].Value.Replace(' ', 'T');
        // Named zones other than UTC/GMT are read as local time.
        return Timestamps.Parse(tz is "UTC" or "GMT" ? iso + "Z" : tz.Length > 0 && tz[0] is '+' or '-' ? iso + (tz.Length == 3 ? tz + ":00" : tz) : iso);
    }

    public override bool Scan(string line, out QuickInfo info)
    {
        var m = Re().Match(line);
        if (!m.Success || IsDetail(m.Groups["sev"].Value))
        {
            info = default;
            return false;
        }
        info = new QuickInfo(Severity(m.Groups["sev"].Value), Time(m));
        return true;
    }

    public override QuickInfo Quick(string line) => Scan(line, out var q) ? q : new QuickInfo(Levels.Detect(line), Timestamps.None);

    public override Dictionary<string, string> Parse(string entry)
    {
        var m = Re().Match(FirstLine(entry));
        if (!m.Success) return new() { ["message"] = entry };
        string G(string a, string b) => m.Groups[a].Success ? m.Groups[a].Value : m.Groups[b].Value;
        var tz = m.Groups["tz"].Value;
        var d = new Dictionary<string, string>
        {
            ["time"] = m.Groups["time"].Value + (tz.Length > 0 ? " " + tz : ""), ["pid"] = m.Groups["pid"].Value,
            ["user"] = G("user", "user2"), ["db"] = G("db", "db2"), ["level"] = L(Severity(m.Groups["sev"].Value)),
            ["severity"] = m.Groups["sev"].Value, ["message"] = m.Groups["msg"].Value + RestLines(entry),
        };
        if (m.Groups["app"].Success) d["app"] = m.Groups["app"].Value;
        if (m.Groups["client"].Success) d["client"] = m.Groups["client"].Value;
        if (Duration().Match(m.Groups["msg"].Value) is { Success: true } dm) d["duration"] = dm.Groups[1].Value;
        return d;
    }
}

/// <summary>MySQL / MariaDB error log: <c>2024-05-01T10:00:00.123456Z 0 [Warning] [MY-010068] [Server] CA certificate is self signed.</c></summary>
public sealed partial class MySqlErrorFormat : LogFormat
{
    [GeneratedRegex(@"^(\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})?) +(\d+) \[(System|Note|Warning|Error|ERROR|Warn)\](?: \[(MY-\d+)\])?(?: \[(\w+)\])? ?(.*)$")]
    internal static partial Regex Re();

    public override string Id => "mysql-error";
    public override string Name => "MySQL error log";
    public override IReadOnlyList<ColumnDef> Columns { get; } =
        [CTime, Col("thread", "Thread", 64), CLevel, Col("code", "Code", 100), Col("subsystem", "Subsystem", 90), CMessage];
    public override bool Multiline => true;
    public override bool IsEntryStart(string line) => Re().IsMatch(line);

    private static Level Lv(string s) => s switch { "Warning" or "Warn" => Level.Warn, "Error" or "ERROR" => Level.Error, _ => Level.Info };

    public override QuickInfo Quick(string line)
    {
        var m = Re().Match(line);
        return m.Success ? new QuickInfo(Lv(m.Groups[3].Value), Timestamps.Parse(m.Groups[1].Value)) : new QuickInfo(Levels.Detect(line), Timestamps.None);
    }

    public override Dictionary<string, string> Parse(string entry)
    {
        var m = Re().Match(FirstLine(entry));
        if (!m.Success) return new() { ["message"] = entry };
        return new()
        {
            ["time"] = m.Groups[1].Value, ["thread"] = m.Groups[2].Value, ["level"] = L(Lv(m.Groups[3].Value)), ["code"] = m.Groups[4].Value,
            ["subsystem"] = m.Groups[5].Value, ["message"] = m.Groups[6].Value + RestLines(entry),
        };
    }
}

/// <summary>Lines MySQL writes at the top of the general and slow query logs, and again after every restart.</summary>
internal static partial class MySqlPreamble
{
    [GeneratedRegex(@"^(?:.*mysqld(?:\.exe)?, Version: .*started with:|Tcp port: \d+ .*|Time +Id +Command +Argument)\s*$")]
    internal static partial Regex Re();
}

/// <summary>MySQL general query log: <c>2024-05-01T10:00:00.123456Z\t    8 Query\tSELECT 1</c>.</summary>
public sealed partial class MySqlGeneralFormat : LogFormat
{
    [GeneratedRegex(@"^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2}))\s+(\d+) (Connect|Query|Quit|Init DB|Prepare|Execute|Close stmt|Reset stmt|Field List|Statistics|Ping|Shutdown|Change user|Binlog Dump|Long Data|Refresh|Kill|Debug|Processlist|Set option|Fetch|Daemon|Error|Sleep|Time|Register Slave|Connect Out|Create DB|Drop DB)(?:\t(.*))?$")]
    internal static partial Regex Re();

    public override string Id => "mysql-general";
    public override string Name => "MySQL general query log";
    public override IReadOnlyList<ColumnDef> Columns { get; } = [CTime, Col("id", "Thread", 64), Col("command", "Command", 90), CLevel, CMessage];
    public override bool Multiline => true;
    public override bool IsEntryStart(string line) => Re().IsMatch(line) || MySqlPreamble.Re().IsMatch(line);
    internal static bool Test(string line) => Re().IsMatch(line) || MySqlPreamble.Re().IsMatch(line);

    private static Level Lv(string command, string arg) =>
        command == "Error" ? Level.Error : command == "Connect" && arg.StartsWith("Access denied", StringComparison.Ordinal) ? Level.Warn : Level.Info;

    public override QuickInfo Quick(string line)
    {
        var m = Re().Match(line);
        return m.Success ? new QuickInfo(Lv(m.Groups[3].Value, m.Groups[4].Value), Timestamps.Parse(m.Groups[1].Value)) : new QuickInfo(Level.None, Timestamps.None);
    }

    public override Dictionary<string, string> Parse(string entry)
    {
        var m = Re().Match(FirstLine(entry));
        if (!m.Success) return new() { ["message"] = entry };
        return new()
        {
            ["time"] = m.Groups[1].Value, ["id"] = m.Groups[2].Value, ["command"] = m.Groups[3].Value,
            ["level"] = L(Lv(m.Groups[3].Value, m.Groups[4].Value)), ["message"] = m.Groups[4].Value + RestLines(entry),
        };
    }
}

/// <summary>
/// MySQL slow query log. Each entry starts at <c># Time:</c> and holds the <c># User@Host</c> and <c># Query_time</c>
/// lines and the statement. Servers that print <c># Time:</c> only when the second changes (MySQL 5.6, MariaDB) put
/// queries of the same second in one entry.
/// </summary>
public sealed partial class MySqlSlowFormat : LogFormat
{
    [GeneratedRegex(@"^# Time: (?:(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})?)|(\d{2})(\d{2})(\d{2}) +(\d{1,2}):(\d{2}):(\d{2}))\s*$")]
    internal static partial Regex TimeRe();
    [GeneratedRegex(@"^# User@Host: (\S*?)\[[^\]]*\] @ (\S*) \[([^\]]*)\](?:\s+Id:\s*(\d+))?")]
    private static partial Regex UserRe();
    [GeneratedRegex(@"(\w+): (\S+)")]
    private static partial Regex StatRe();
    [GeneratedRegex(@"^use (\w+);$", RegexOptions.IgnoreCase)]
    private static partial Regex UseRe();

    public override string Id => "mysql-slow";
    public override string Name => "MySQL slow query log";
    public override IReadOnlyList<ColumnDef> Columns { get; } =
        [CTime, Col("query_time", "Query time", 90), Col("rows_examined", "Rows examined", 100), Col("user", "User", 90), Col("db", "DB", 80), CMessage];
    public override bool Multiline => true;
    public override bool IsEntryStart(string line) => line.StartsWith("# Time: ", StringComparison.Ordinal) || MySqlPreamble.Re().IsMatch(line);

    private long Time(Match m) => m.Groups[1].Success
        ? Timestamps.Parse(m.Groups[1].Value)
        : Timestamps.Build(2000 + int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value), int.Parse(m.Groups[5].Value), int.Parse(m.Groups[6].Value), int.Parse(m.Groups[7].Value), 0, null);

    public override QuickInfo Quick(string line) =>
        TimeRe().Match(line) is { Success: true } m ? new QuickInfo(Level.Info, Time(m)) : new QuickInfo(Level.None, Timestamps.None);

    public override Dictionary<string, string> Parse(string entry)
    {
        var lines = entry.Split('\n');
        var d = new Dictionary<string, string> { ["time"] = "", ["query_time"] = "", ["rows_examined"] = "", ["user"] = "", ["db"] = "", ["level"] = "", ["message"] = "" };
        if (TimeRe().Match(lines[0].TrimEnd('\r')) is not { Success: true } tm) return new() { ["message"] = entry };
        d["time"] = lines[0][8..].Trim();
        d["level"] = L(Level.Info);
        var sql = new List<string>();
        foreach (var raw in lines.Skip(1))
        {
            var l = raw.TrimEnd('\r');
            Match m;
            if ((m = UserRe().Match(l)).Success)
            {
                d["user"] = m.Groups[1].Value;
                d["host"] = m.Groups[2].Value.Length > 0 ? m.Groups[2].Value : m.Groups[3].Value;
                if (m.Groups[4].Success) d["id"] = m.Groups[4].Value;
            }
            else if (l.StartsWith('#'))
            {
                // "# Query_time: 2.5  Lock_time: 0.0001 Rows_sent: 1  Rows_examined: 100000", MariaDB "# Schema: shop ..."
                foreach (Match s in StatRe().Matches(l))
                {
                    var k = s.Groups[1].Value.ToLowerInvariant();
                    d[k == "schema" ? "db" : k] = s.Groups[2].Value;
                }
            }
            else if ((m = UseRe().Match(l)).Success) d["db"] = m.Groups[1].Value;
            else if (!l.StartsWith("SET timestamp=", StringComparison.OrdinalIgnoreCase) && l.Length > 0) sql.Add(l);
        }
        d["message"] = sql.Count > 0 ? string.Join('\n', sql) : entry;
        return d;
    }
}

/// <summary>MongoDB 4.4+ structured log: <c>{"t":{"$date":"…"},"s":"I","c":"NETWORK","id":22943,"ctx":"listener","msg":"…","attr":{…}}</c>.</summary>
public sealed partial class MongoFormat : LogFormat
{
    [GeneratedRegex(@"^\s*\{""t"":\{""\$date"":""([^""]+)""\},\s*""s"":""(\w+)""")]
    internal static partial Regex Head();

    public override string Id => "mongodb";
    public override string Name => "MongoDB (4.4+ JSON)";
    public override IReadOnlyList<ColumnDef> Columns { get; } =
        [CTime, CLevel, Col("component", "Component", 90), Col("ctx", "Context", 110), Col("id", "ID", 64), CMessage];

    /// <summary>F fatal, E error, W warning, I info, D1 debug, D2–D5 more verbose debug (shown as trace).</summary>
    internal static Level Severity(string s) => s switch
    {
        "F" => Level.Fatal,
        "E" => Level.Error,
        "W" => Level.Warn,
        "I" => Level.Info,
        "D" or "D1" => Level.Debug,
        _ when s.Length == 2 && s[0] == 'D' => Level.Trace,
        _ => Level.None,
    };

    public override QuickInfo Quick(string line)
    {
        var m = Head().Match(line);
        return m.Success ? new QuickInfo(Severity(m.Groups[2].Value), Timestamps.Parse(m.Groups[1].Value)) : new QuickInfo(Levels.Detect(line), Timestamps.None);
    }

    public override Dictionary<string, string> Parse(string entry)
    {
        if (JsonFormat.TryJson(entry) is not JsonElement o) return new() { ["message"] = entry };
        static string S(JsonElement o, string k) => o.TryGetProperty(k, out var v) ? JsonFormat.Str(v) : "";
        var time = o.TryGetProperty("t", out var t) && t.ValueKind == JsonValueKind.Object ? S(t, "$date") : S(o, "t");
        var d = new Dictionary<string, string>
        {
            ["time"] = time, ["level"] = L(Severity(S(o, "s"))), ["component"] = S(o, "c"), ["ctx"] = S(o, "ctx"), ["id"] = S(o, "id"),
            ["message"] = S(o, "msg"),
        };
        JsonFormat.AddExtras(d, o, "", ["s", "c", "ctx", "id", "msg"], 0);
        d.Remove("t.$date"); // already the time
        return d;
    }
}

/// <summary>macOS unified log as printed by <c>log show</c> (default style).</summary>
public sealed partial class MacUnifiedFormat : LogFormat
{
    [GeneratedRegex(@"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d+[+-]\d{4})\s+(0x[0-9a-f]+)\s+(\w+)\s+(0x[0-9a-f]+)\s+(\d+)\s+(?:(\d+)\s+)?(.*)$")]
    internal static partial Regex Re();
    [GeneratedRegex(@"^(.+?)(?:\[\d+\])?: (?:\(([^)]*)\) )?(?:\[([^\]:]+)(?::([^\]]*))?\] )?(.*)$", RegexOptions.Singleline)]
    private static partial Regex Rest();
    [GeneratedRegex(@"^(?:Filtering the log data using .*|Skipping info and debug messages.*|Timestamp +Thread +Type +Activity +PID.*|-{20,}|Log +- Default:.*)$")]
    internal static partial Regex Preamble();

    public override string Id => "macos";
    public override string Name => "macOS unified log (log show)";
    public override IReadOnlyList<ColumnDef> Columns { get; } =
        [CTime, CLevel, Col("process", "Process", 140), Col("pid", "PID", 64), Col("subsystem", "Subsystem", 160), CMessage];
    public override bool Multiline => true;
    public override bool IsEntryStart(string line) => Re().IsMatch(line) || Preamble().IsMatch(line);
    internal static bool Test(string line) => Re().IsMatch(line) || Preamble().IsMatch(line);

    private static Level Lv(string type) => type switch
    {
        "Debug" => Level.Debug,
        "Info" or "Default" => Level.Info,
        "Error" => Level.Error,
        "Fault" => Level.Fatal,
        _ => Level.None,
    };

    public override QuickInfo Quick(string line)
    {
        var m = Re().Match(line);
        return m.Success ? new QuickInfo(Lv(m.Groups[3].Value), Timestamps.Parse(m.Groups[1].Value)) : new QuickInfo(Level.None, Timestamps.None);
    }

    public override Dictionary<string, string> Parse(string entry)
    {
        var m = Re().Match(FirstLine(entry));
        if (!m.Success) return new() { ["message"] = entry };
        var d = new Dictionary<string, string>
        {
            ["time"] = m.Groups[1].Value, ["level"] = L(Lv(m.Groups[3].Value)), ["process"] = "", ["pid"] = m.Groups[5].Value, ["subsystem"] = "",
            ["message"] = m.Groups[7].Value + RestLines(entry), ["thread"] = m.Groups[2].Value, ["type"] = m.Groups[3].Value, ["activity"] = m.Groups[4].Value,
        };
        if (m.Groups[6].Success) d["ttl"] = m.Groups[6].Value;
        var r = Rest().Match(m.Groups[7].Value + RestLines(entry));
        if (r.Success)
        {
            d["process"] = r.Groups[1].Value;
            if (r.Groups[2].Success) d["library"] = r.Groups[2].Value;
            d["subsystem"] = r.Groups[3].Value;
            if (r.Groups[4].Success) d["category"] = r.Groups[4].Value;
            d["message"] = r.Groups[5].Value;
        }
        return d;
    }
}

/// <summary>Laravel (Monolog line format): <c>[2024-05-01 10:00:00] production.ERROR: message {"exception":"…"} []</c> plus a stack trace.</summary>
public sealed partial class LaravelFormat : LogFormat
{
    [GeneratedRegex(@"^\[(\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:?\d{2})?)\] ([\w-]+)\.(DEBUG|INFO|NOTICE|WARNING|ERROR|CRITICAL|ALERT|EMERGENCY): (.*)$")]
    internal static partial Regex Re();
    [GeneratedRegex(@"""exception"":""\[object\] \(([\w\\]+)\(code: (-?\d+)\)")]
    private static partial Regex Exception();

    public override string Id => "laravel";
    public override string Name => "Laravel";
    public override IReadOnlyList<ColumnDef> Columns { get; } = [CTime, Col("channel", "Env", 90), CLevel, CMessage];
    public override bool Multiline => true;
    public override bool IsEntryStart(string line) => Re().IsMatch(line);

    public override QuickInfo Quick(string line)
    {
        var m = Re().Match(line);
        return m.Success ? new QuickInfo(Levels.Parse(m.Groups[3].Value), Timestamps.Parse(m.Groups[1].Value)) : new QuickInfo(Levels.Detect(line), Timestamps.None);
    }

    public override Dictionary<string, string> Parse(string entry)
    {
        var m = Re().Match(FirstLine(entry));
        if (!m.Success) return new() { ["message"] = entry };
        var d = new Dictionary<string, string>
        {
            ["time"] = m.Groups[1].Value, ["channel"] = m.Groups[2].Value, ["level"] = L(Levels.Parse(m.Groups[3].Value)), ["message"] = m.Groups[4].Value + RestLines(entry),
        };
        if (Exception().Match(m.Groups[4].Value) is { Success: true } e)
        {
            d["exception"] = e.Groups[1].Value.Replace("\\\\", "\\");
            d["code"] = e.Groups[2].Value;
        }
        return d;
    }
}
