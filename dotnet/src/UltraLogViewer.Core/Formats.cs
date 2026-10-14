using System.Text.Json;
using System.Text.RegularExpressions;

namespace UltraLogViewer.Core;

public readonly record struct QuickInfo(Level Level, long Time);

/// <summary>
/// A log format answers three questions: does a line start a new entry (other lines are continuation
/// lines such as stack traces); <see cref="Quick"/> gives level + time while indexing every entry;
/// <see cref="Parse"/> gives all columns, only for rows on screen.
/// </summary>
public abstract class LogFormat
{
    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract IReadOnlyList<ColumnDef> Columns { get; }
    public virtual bool Multiline => false;
    public virtual bool IsEntryStart(string line) => true;
    public abstract QuickInfo Quick(string line);

    /// <summary>IsEntryStart + Quick in one pass; false for continuation lines.</summary>
    public virtual bool Scan(string line, out QuickInfo info)
    {
        if (Multiline && !IsEntryStart(line))
        {
            info = default;
            return false;
        }
        info = Quick(line);
        return true;
    }

    /// <summary>
    /// Allocation-free variant of <see cref="Scan"/> for the indexing hot path.
    /// Returns null when the format needs a string (the caller then uses <see cref="Scan"/>).
    /// </summary>
    public virtual bool? ScanSpan(ReadOnlySpan<char> line, out QuickInfo info)
    {
        info = default;
        return null;
    }

    public abstract Dictionary<string, string> Parse(string entry);

    protected static ColumnDef Col(string key, string title, double width) => new(key, title, width);
    protected static readonly ColumnDef CTime = new("time", "Time", 190);
    protected static readonly ColumnDef CLevel = new("level", "Level", 64);
    protected static readonly ColumnDef CThread = new("thread", "Thread", 110);
    protected static readonly ColumnDef CLogger = new("logger", "Logger", 180);
    protected static readonly ColumnDef CMessage = new("message", "Message", 0);

    protected static string FirstLine(string s)
    {
        var i = s.IndexOf('\n');
        return i < 0 ? s : s[..i];
    }

    protected static string RestLines(string s)
    {
        var i = s.IndexOf('\n');
        return i < 0 ? "" : s[i..];
    }

    protected static string L(Level l) => Levels.Label(l);
}

public sealed class PlainFormat : LogFormat
{
    public override string Id => "plain";
    public override string Name => "Plain text";
    public override IReadOnlyList<ColumnDef> Columns { get; } = [CLevel, CMessage];
    public override QuickInfo Quick(string line) => new(Levels.Detect(line), Timestamps.None);

    public override bool? ScanSpan(ReadOnlySpan<char> line, out QuickInfo info)
    {
        info = new(Levels.Detect(line), Timestamps.None);
        return true;
    }

    public override Dictionary<string, string> Parse(string entry) => new() { ["level"] = L(Levels.Detect(entry)), ["message"] = entry };
}

/// <summary>
/// Generic "timestamp first" format. Covers log4j/logback/log4net/NLog/Serilog/Python logging layouts, e.g.
/// <c>2024-05-01 10:00:00,123 [main] INFO  com.acme.App - Started</c>,
/// <c>2024-05-01T10:00:00.123Z ERROR 1234 --- [worker-1] c.a.Service : boom</c>,
/// <c>2024-05-01 10:00:00.1234|WARN|Acme.Service|Low disk</c>.
/// </summary>
public sealed partial class TimestampedFormat(int year) : LogFormat
{
    public override string Id => "timestamped";
    public override string Name => "Timestamped text (log4j, logback, NLog, Serilog, Python…)";
    public override IReadOnlyList<ColumnDef> Columns { get; } = [CTime, CLevel, CThread, CLogger, CMessage];
    public override bool Multiline => true;

    [GeneratedRegex(@"^(TRACE|VERBOSE|DEBUG|DBG|INFO|INF|NOTICE|WARN|WARNING|WRN|ERROR|ERR|SEVERE|FATAL|CRITICAL|CRIT|FINE|FINER|FINEST|FTL|VRB|trace|debug|info|warn|warning|error|fatal|critical)\b")]
    private static partial Regex LevelToken();
    [GeneratedRegex(@"^([A-Za-z_$][\w$.:<>@/-]*)\s*(?:-|:|\|)\s+")]
    private static partial Regex LoggerToken();
    [GeneratedRegex(@"^\d+\s+---\s+")]
    private static partial Regex SpringPid();
    [GeneratedRegex(@"^[\w-]+\.([A-Za-z]+):\s")]
    private static partial Regex MonologLevel();

    public override bool IsEntryStart(string line) => Timestamps.TryMatch(line, year, out _, out _);

    public override bool Scan(string line, out QuickInfo info) => ScanSpan(line, out info) == true;

    public override bool? ScanSpan(ReadOnlySpan<char> line, out QuickInfo info)
    {
        if (!Timestamps.TryMatch(line, year, out var ms, out var len))
        {
            info = default;
            return false;
        }
        var rest = line[len..];
        info = new QuickInfo(Levels.Detect(rest.Length > 120 ? rest[..120] : rest), ms);
        return true;
    }

    public override QuickInfo Quick(string line) => Scan(line, out var q) ? q : new QuickInfo(Levels.Detect(line), Timestamps.None);

    public override Dictionary<string, string> Parse(string entry)
    {
        var line = FirstLine(entry);
        var f = new Dictionary<string, string> { ["time"] = "", ["level"] = "", ["thread"] = "", ["logger"] = "", ["message"] = "" };
        if (!Timestamps.TryMatch(line, year, out _, out var len))
        {
            f["message"] = entry;
            f["level"] = L(Levels.Detect(line));
            return f;
        }
        f["time"] = line[..len].Trim('[', ']');
        var r = line[len..];
        var level = Level.None;
        for (var guard = 0; guard < 8; guard++)
        {
            r = r.TrimStart(' ', '\t', '|', ',', ';');
            Match t;
            if (r.Length > 0 && (r[0] == '[' || r[0] == '(' || r[0] == '<'))
            {
                var close = r[0] switch { '[' => ']', '(' => ')', _ => '>' };
                var end = r.IndexOf(close);
                if (end < 0 || end > 120) break;
                var inner = r[1..end].Trim();
                var lv = Levels.Parse(inner);
                if (lv != Level.None && level == Level.None) level = lv;
                else if (f["thread"].Length == 0) f["thread"] = inner;
                r = r[(end + 1)..];
            }
            else if (level == Level.None && (t = LevelToken().Match(r)).Success)
            {
                level = Levels.Parse(t.Groups[1].Value);
                r = r[t.Length..].TrimStart(':');
            }
            else if ((t = SpringPid().Match(r)).Success)
                r = r[t.Length..];
            else if (level == Level.None && (t = MonologLevel().Match(r)).Success && Levels.Parse(t.Groups[1].Value) != Level.None)
            {
                level = Levels.Parse(t.Groups[1].Value);
                r = r[t.Length..];
            }
            else
                break;
        }
        r = r.TrimStart(' ', '\t', '|');
        var lg = LoggerToken().Match(r);
        if (lg.Success && lg.Groups[1].Length < 120 && !Regex.IsMatch(lg.Groups[1].Value, "^(https?|ftp)$", RegexOptions.IgnoreCase))
        {
            f["logger"] = lg.Groups[1].Value.TrimEnd(':');
            r = r[lg.Length..];
        }
        else if (r.Contains('|'))
        {
            var parts = r.Split('|');
            if (parts.Length >= 2 && parts[0].Length < 120)
            {
                f["logger"] = parts[0].Trim();
                r = string.Join('|', parts.Skip(1));
            }
        }
        if (level == Level.None) level = Levels.Detect(line);
        f["level"] = L(level);
        f["message"] = r.TrimStart() + RestLines(entry);
        return f;
    }
}

public sealed class JsonFormat : LogFormat
{
    private static readonly string[] TimeKeys = ["@timestamp", "timestamp", "time", "ts", "@t", "t", "date", "datetime", "eventtime", "asctime", "logtime"];
    private static readonly string[] LevelKeys = ["level", "@l", "severity", "lvl", "loglevel", "log.level", "levelname", "severity_text", "priority"];
    private static readonly string[] MessageKeys = ["message", "msg", "@m", "@mt", "text", "log", "event", "body"];
    private static readonly string[] LoggerKeys = ["logger", "logger_name", "loggername", "name", "category", "source", "sourcecontext", "module", "component"];
    private static readonly string[] ErrorKeys = ["exception", "@x", "error", "stack", "stacktrace", "err"];

    public override string Id => "json";
    public override string Name => "JSON lines (Serilog, pino, bunyan, ECS, Logstash…)";
    public override IReadOnlyList<ColumnDef> Columns { get; } = [CTime, CLevel, CLogger, CMessage];

    internal static JsonElement? TryJson(string line)
    {
        var t = line.AsSpan().TrimStart();
        if (t.IsEmpty || t[0] != '{') return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement? Pick(JsonElement o, string[] keys) => PickNamed(o, keys).Value;

    /// <summary>The first property matching one of the keys, and its name (a dotted path for nested keys).</summary>
    private static (JsonElement? Value, string? Name) PickNamed(JsonElement o, string[] keys)
    {
        foreach (var k in keys)
            if (o.TryGetProperty(k, out var v)) return (v, k);
        foreach (var p in o.EnumerateObject())
        {
            var lk = p.Name.ToLowerInvariant();
            if (keys.Contains(lk)) return (p.Value, p.Name);
        }
        foreach (var k in keys)
        {
            var dot = k.IndexOf('.');
            if (dot > 0 && o.TryGetProperty(k[..dot], out var parent) && parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(k[(dot + 1)..], out var child))
                return (child, k);
        }
        return (null, null);
    }

    private const int MaxExtraFields = 60;

    /// <summary>Adds the remaining properties as fields, nested objects flattened to dotted names (http.request.path).</summary>
    private static void AddExtras(Dictionary<string, string> d, JsonElement o, string prefix, HashSet<string> skip, int depth)
    {
        foreach (var p in o.EnumerateObject())
        {
            if (d.Count >= MaxExtraFields) return;
            var name = prefix + p.Name;
            if (skip.Contains(name)) continue;
            if (p.Value.ValueKind == JsonValueKind.Object && depth < 4)
            {
                AddExtras(d, p.Value, name + ".", skip, depth + 1);
                continue;
            }
            d.TryAdd(name, Str(p.Value));
        }
    }

    private static string Str(JsonElement? v) => v is not JsonElement e ? "" : e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? "",
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        _ => e.GetRawText(),
    };

    private static Level LevelOf(JsonElement? v) => v is JsonElement e && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var n) ? Levels.FromNumber(n) : Levels.Parse(Str(v));

    private static long TimeOf(JsonElement? v) => v is JsonElement e && e.ValueKind == JsonValueKind.Number ? Timestamps.FromNumber(e.GetDouble()) : Timestamps.Parse(Str(v));

    public override QuickInfo Quick(string line)
    {
        if (TryJson(line) is not JsonElement o) return new QuickInfo(Levels.Detect(line), Timestamps.None);
        return new QuickInfo(LevelOf(Pick(o, LevelKeys)), TimeOf(Pick(o, TimeKeys)));
    }

    public override Dictionary<string, string> Parse(string entry)
    {
        if (TryJson(entry) is not JsonElement o)
            return new() { ["time"] = "", ["level"] = L(Levels.Detect(entry)), ["logger"] = "", ["message"] = entry };
        var (msg, msgKey) = PickNamed(o, MessageKeys);
        var (err, errKey) = PickNamed(o, ErrorKeys);
        var (time, timeKey) = PickNamed(o, TimeKeys);
        var (level, levelKey) = PickNamed(o, LevelKeys);
        var (logger, loggerKey) = PickNamed(o, LoggerKeys);
        var message = Str(msg);
        if (err is JsonElement ex)
        {
            var exText = ex.ValueKind == JsonValueKind.Object && ex.TryGetProperty("stack", out var st) ? Str(st) : Str(ex);
            message += (message.Length > 0 ? "\n" : "") + exText;
        }
        var lvl = L(LevelOf(level));
        var d = new Dictionary<string, string>
        {
            ["time"] = Str(time),
            ["level"] = lvl.Length > 0 ? lvl : Str(level),
            ["logger"] = Str(logger),
            ["message"] = message.Length > 0 ? message : entry,
        };
        var skip = new HashSet<string>(new[] { msgKey, errKey, timeKey, levelKey, loggerKey }.OfType<string>(), StringComparer.Ordinal);
        AddExtras(d, o, "", skip, 0);
        return d;
    }
}

public sealed partial class SyslogFormat(int year) : LogFormat
{
    [GeneratedRegex(@"^([A-Z][a-z]{2} {1,2}\d{1,2} \d{2}:\d{2}:\d{2}(?:\.\d+)?) (\S+) ([^\s:\[]+)(?:\[(\d+)\])?:? ?(.*)$")]
    internal static partial Regex Re();

    public override string Id => "syslog";
    public override string Name => "Syslog (RFC 3164 / journalctl)";
    public override IReadOnlyList<ColumnDef> Columns { get; } = [CTime, Col("host", "Host", 110), Col("app", "App", 130), Col("pid", "PID", 64), CLevel, CMessage];
    public override bool Multiline => true;
    public override bool IsEntryStart(string line) => Re().IsMatch(line);

    public override QuickInfo Quick(string line)
    {
        var m = Re().Match(line);
        if (!m.Success) return new QuickInfo(Levels.Detect(line), Timestamps.None);
        Timestamps.TryMatch(m.Groups[1].Value, year, out var ms, out _);
        return new QuickInfo(Levels.Detect(m.Groups[5].Value), ms);
    }

    public override Dictionary<string, string> Parse(string entry)
    {
        var m = Re().Match(FirstLine(entry));
        if (!m.Success) return new() { ["message"] = entry };
        return new()
        {
            ["time"] = m.Groups[1].Value, ["host"] = m.Groups[2].Value, ["app"] = m.Groups[3].Value, ["pid"] = m.Groups[4].Value,
            ["level"] = L(Levels.Detect(m.Groups[5].Value)), ["message"] = m.Groups[5].Value + RestLines(entry),
        };
    }
}

public sealed partial class Syslog5424Format : LogFormat
{
    [GeneratedRegex(@"^<(\d{1,3})>1 (\S+) (\S+) (\S+) (\S+) (\S+) (-|\[.*?\](?:\[.*?\])*) ?(.*)$")]
    internal static partial Regex Re();

    public override string Id => "syslog5424";
    public override string Name => "Syslog (RFC 5424)";
    public override IReadOnlyList<ColumnDef> Columns { get; } =
        [CTime, Col("host", "Host", 110), Col("app", "App", 120), Col("pid", "PID", 64), Col("msgid", "MsgID", 80), CLevel, CMessage];
    public override bool Multiline => true;
    public override bool IsEntryStart(string line) => Re().IsMatch(line);

    private static Level Sev(Match m) => Levels.FromNumber(int.Parse(m.Groups[1].Value) % 8);

    public override QuickInfo Quick(string line)
    {
        var m = Re().Match(line);
        return m.Success ? new QuickInfo(Sev(m), Timestamps.Parse(m.Groups[2].Value)) : new QuickInfo(Levels.Detect(line), Timestamps.None);
    }

    public override Dictionary<string, string> Parse(string entry)
    {
        var m = Re().Match(FirstLine(entry));
        if (!m.Success) return new() { ["message"] = entry };
        var sd = m.Groups[7].Value == "-" ? "" : m.Groups[7].Value + " ";
        return new()
        {
            ["time"] = m.Groups[2].Value, ["host"] = m.Groups[3].Value, ["app"] = m.Groups[4].Value, ["pid"] = m.Groups[5].Value,
            ["msgid"] = m.Groups[6].Value, ["level"] = L(Sev(m)), ["message"] = sd + m.Groups[8].Value + RestLines(entry),
        };
    }
}

public sealed partial class AccessLogFormat : LogFormat
{
    [GeneratedRegex(@"^(\S+) \S+ (\S+) \[([^\]]+)\] ""([^""]*)"" (\d{3}) (\S+)(?: ""([^""]*)"" ""([^""]*)"")?(.*)$")]
    internal static partial Regex Re();

    public override string Id => "access";
    public override string Name => "Web access log (Apache / Nginx combined)";
    public override IReadOnlyList<ColumnDef> Columns { get; } =
        [CTime, Col("client", "Client", 120), Col("status", "Status", 60), Col("method", "Method", 70), Col("url", "URL", 320), Col("bytes", "Bytes", 70), Col("agent", "User agent", 220), CMessage];

    internal static Level StatusLevel(int s) => s >= 500 ? Level.Error : s >= 400 ? Level.Warn : Level.Info;

    public override QuickInfo Quick(string line)
    {
        var m = Re().Match(line);
        if (!m.Success) return new QuickInfo(Levels.Detect(line), Timestamps.None);
        Timestamps.TryMatch(m.Groups[3].Value, 0, out var ms, out _);
        return new QuickInfo(StatusLevel(int.Parse(m.Groups[5].Value)), ms);
    }

    public override Dictionary<string, string> Parse(string entry)
    {
        var m = Re().Match(entry);
        if (!m.Success) return new() { ["message"] = entry };
        var req = m.Groups[4].Value.Split(' ');
        return new()
        {
            ["time"] = m.Groups[3].Value, ["client"] = m.Groups[1].Value, ["user"] = m.Groups[2].Value, ["status"] = m.Groups[5].Value,
            ["method"] = req.Length > 0 ? req[0] : "", ["url"] = req.Length > 1 ? req[1] : m.Groups[4].Value, ["protocol"] = req.Length > 2 ? req[2] : "",
            ["bytes"] = m.Groups[6].Value, ["referer"] = m.Groups[7].Value, ["agent"] = m.Groups[8].Value,
            ["level"] = L(StatusLevel(int.Parse(m.Groups[5].Value))), ["message"] = m.Groups[4].Value,
        };
    }
}

public sealed class W3cFormat : LogFormat
{
    private readonly string[] _fields;
    private readonly int _date, _time, _status;

    public W3cFormat(string[] fields)
    {
        _fields = fields;
        _date = Array.IndexOf(fields, "date");
        _time = Array.IndexOf(fields, "time");
        _status = Array.IndexOf(fields, "sc-status");
        var cols = new List<ColumnDef> { CTime, CLevel };
        foreach (var f in fields)
            if (f is not ("date" or "time")) cols.Add(Col(f, f, f == "cs-uri-stem" ? 260 : f == "cs(User-Agent)" ? 200 : 100));
        cols[^1] = cols[^1] with { Width = 0 };
        Columns = cols;
    }

    public override string Id => "w3c";
    public override string Name => "W3C extended (IIS)";
    public override IReadOnlyList<ColumnDef> Columns { get; }

    private (string Raw, long Ms) TimeOf(string[] p)
    {
        var raw = $"{(_date >= 0 && _date < p.Length ? p[_date] : "1970-01-01")} {(_time >= 0 && _time < p.Length ? p[_time] : "00:00:00")}";
        return (raw, Timestamps.Parse(raw.Replace(' ', 'T') + "Z"));
    }

    private Level LevelOf(string[] p) => _status >= 0 && _status < p.Length && int.TryParse(p[_status], out var s) ? AccessLogFormat.StatusLevel(s) : Level.Info;

    public override QuickInfo Quick(string line)
    {
        if (line.StartsWith('#')) return new QuickInfo(Level.None, Timestamps.None);
        var p = line.Split(' ');
        return new QuickInfo(LevelOf(p), TimeOf(p).Ms);
    }

    public override Dictionary<string, string> Parse(string entry)
    {
        if (entry.StartsWith('#')) return new() { ["message"] = entry };
        var p = entry.Split(' ');
        var d = new Dictionary<string, string> { ["time"] = TimeOf(p).Raw, ["level"] = L(LevelOf(p)), ["message"] = entry };
        for (var i = 0; i < _fields.Length; i++)
            if (_fields[i] is not ("date" or "time")) d[_fields[i]] = i < p.Length ? p[i] : "";
        return d;
    }
}

public sealed partial class LogcatFormat(int year) : LogFormat
{
    [GeneratedRegex(@"^(\d{2})-(\d{2}) (\d{2}):(\d{2}):(\d{2})\.(\d{3})\s+(\d+)\s+(\d+) ([VDIWEFA]) ([^:]*?)\s*: (.*)$")]
    internal static partial Regex Re();

    public override string Id => "logcat";
    public override string Name => "Android logcat (threadtime)";
    public override IReadOnlyList<ColumnDef> Columns { get; } = [CTime, Col("pid", "PID", 60), Col("tid", "TID", 60), CLevel, Col("tag", "Tag", 160), CMessage];

    private static Level Lv(Match m) => Levels.Parse(m.Groups[9].Value == "A" ? "F" : m.Groups[9].Value);
    private static int G(Match m, int i) => int.Parse(m.Groups[i].Value);

    public override QuickInfo Quick(string line)
    {
        var m = Re().Match(line);
        if (!m.Success) return new QuickInfo(Levels.Detect(line), Timestamps.None);
        return new QuickInfo(Lv(m), Timestamps.Build(year, G(m, 1), G(m, 2), G(m, 3), G(m, 4), G(m, 5), G(m, 6), null));
    }

    public override Dictionary<string, string> Parse(string entry)
    {
        var m = Re().Match(entry);
        if (!m.Success) return new() { ["message"] = entry };
        return new()
        {
            ["time"] = $"{m.Groups[1]}-{m.Groups[2]} {m.Groups[3]}:{m.Groups[4]}:{m.Groups[5]}.{m.Groups[6]}",
            ["pid"] = m.Groups[7].Value, ["tid"] = m.Groups[8].Value, ["level"] = L(Lv(m)), ["tag"] = m.Groups[10].Value, ["message"] = m.Groups[11].Value,
        };
    }
}

/// <summary>User-defined regex with named groups: timestamp, level, thread, logger, message and any others.</summary>
public sealed class CustomRegexFormat : LogFormat
{
    private readonly Regex _re;
    private readonly int _year;

    public CustomRegexFormat(CustomFormat def, int year)
    {
        _re = new Regex(def.Pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        _year = year;
        Name = def.Name;
        var known = new Dictionary<string, ColumnDef> { ["time"] = CTime, ["level"] = CLevel, ["thread"] = CThread, ["logger"] = CLogger, ["message"] = CMessage };
        var cols = _re.GetGroupNames().Where(g => !int.TryParse(g, out _))
            .Select(g => Key(g))
            .Select(k => known.TryGetValue(k, out var c) ? c : Col(k, char.ToUpperInvariant(k[0]) + k[1..], 120))
            .ToList();
        if (cols.Count > 0 && !cols.Any(c => c.Width == 0)) cols[^1] = cols[^1] with { Width = 0 };
        Columns = cols;
    }

    private static string Key(string g) => g == "timestamp" ? "time" : g;

    public override string Id => $"custom:{Name}";
    public override string Name { get; }
    public override IReadOnlyList<ColumnDef> Columns { get; }
    public override bool Multiline => true;
    public override bool IsEntryStart(string line) => _re.IsMatch(line);

    private Dictionary<string, string> Groups(Match m)
    {
        var d = new Dictionary<string, string>();
        foreach (var name in _re.GetGroupNames())
            if (!int.TryParse(name, out _)) d[Key(name)] = m.Groups[name].Value;
        return d;
    }

    public override QuickInfo Quick(string line)
    {
        var m = _re.Match(line);
        if (!m.Success) return new QuickInfo(Levels.Detect(line), Timestamps.None);
        var g = Groups(m);
        var ms = Timestamps.None;
        if (g.TryGetValue("time", out var t) && t.Length > 0 && !Timestamps.TryMatch(t, _year, out ms, out _, allowEpoch: true)) ms = Timestamps.Parse(t, _year);
        var lv = g.TryGetValue("level", out var l) && l.Length > 0 ? Levels.Parse(l) : Levels.Detect(line);
        return new QuickInfo(lv, ms);
    }

    public override Dictionary<string, string> Parse(string entry)
    {
        var m = _re.Match(FirstLine(entry));
        if (!m.Success) return new() { ["message"] = entry };
        var g = Groups(m);
        if (g.ContainsKey("message")) g["message"] += RestLines(entry);
        else g["message"] = entry;
        return g;
    }
}

public static class FormatDetector
{
    public static readonly (string Id, string Label)[] BuiltIns =
    [
        ("timestamped", "Timestamped text"), ("json", "JSON lines"), ("syslog", "Syslog RFC 3164"), ("syslog5424", "Syslog RFC 5424"),
        ("access", "Apache / Nginx"), ("logcat", "Android logcat"), ("plain", "Plain text"),
    ];

    /// <summary>Picks the best format for a sample of lines from the start of a file.</summary>
    public static LogFormat Detect(IReadOnlyList<string> sample, int year = 0, IEnumerable<CustomFormat>? custom = null, string? force = null)
    {
        if (year == 0) year = DateTime.Now.Year;
        var lines = sample.Where(l => l.Trim().Length > 0).Take(500).ToList();
        var candidates = new List<(LogFormat F, double Min, Func<string, bool> Test)>();
        foreach (var def in custom ?? [])
        {
            try
            {
                var f = new CustomRegexFormat(def, year);
                candidates.Add((f, 0.5, f.IsEntryStart));
            }
            catch (ArgumentException)
            {
                // invalid user regex: skip
            }
        }
        var w3c = lines.FirstOrDefault(l => l.StartsWith("#Fields:"));
        if (w3c is not null) candidates.Add((new W3cFormat(w3c[8..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)), 0, _ => true));
        var ts = new TimestampedFormat(year);
        candidates.Add((new JsonFormat(), 0.6, l => JsonFormat.TryJson(l) is not null));
        candidates.Add((new AccessLogFormat(), 0.6, l => AccessLogFormat.Re().IsMatch(l)));
        candidates.Add((new Syslog5424Format(), 0.6, l => Syslog5424Format.Re().IsMatch(l)));
        candidates.Add((new LogcatFormat(year), 0.6, l => LogcatFormat.Re().IsMatch(l)));
        candidates.Add((new SyslogFormat(year), 0.5, l => SyslogFormat.Re().IsMatch(l)));
        candidates.Add((ts, 0.1, ts.IsEntryStart));

        if (!string.IsNullOrEmpty(force))
        {
            if (force == "plain") return new PlainFormat();
            var forced = candidates.FirstOrDefault(c => c.F.Id == force);
            if (forced.F is not null) return forced.F;
        }
        if (lines.Count == 0) return new PlainFormat();
        foreach (var (f, min, test) in candidates)
        {
            var hits = lines.Count(test);
            var ratio = (double)hits / lines.Count;
            // The first non-empty line must start an entry unless most lines do, otherwise we'd be guessing.
            if (hits > 0 && ratio >= min && (min == 0 || test(lines[0]) || ratio > 0.5)) return f;
        }
        return new PlainFormat();
    }
}
