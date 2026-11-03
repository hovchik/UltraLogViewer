using System.Globalization;
using System.Text.RegularExpressions;

namespace UltraLogViewer.Core;

/// <summary>
/// Which part of a file to open. <see cref="Since"/> / <see cref="Until"/> are epoch ms (inclusive);
/// <see cref="Last"/> is a duration back from the newest entry of the file (used when Since is not set).
/// </summary>
public sealed record OpenWindow(long? Since = null, long? Until = null, long? Last = null)
{
    public bool IsEmpty => Since is null && Until is null && Last is null;
}

/// <summary>Parsing of time specs ("-1h", "now", "2024-05-01T10:00") and of the command line.</summary>
public static partial class TimeWindow
{
    [GeneratedRegex(@"^(?:(\d+(?:\.\d+)?)\s*(ms|s|sec|m|min|h|hr|d|w)\s*)+$", RegexOptions.IgnoreCase)]
    private static partial Regex DurationRe();
    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s*(ms|s|sec|m|min|h|hr|d|w)", RegexOptions.IgnoreCase)]
    private static partial Regex DurationPartRe();

    /// <summary>"90s", "15m", "1h30m", "2d", "1w" → ms; null if not a duration.</summary>
    public static long? ParseDuration(string? s)
    {
        s = s?.Trim();
        if (string.IsNullOrEmpty(s) || !DurationRe().IsMatch(s)) return null;
        double total = 0;
        foreach (Match m in DurationPartRe().Matches(s))
        {
            var v = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            total += v * m.Groups[2].Value.ToLowerInvariant() switch
            {
                "ms" => 1,
                "s" or "sec" => 1000,
                "m" or "min" => 60_000,
                "h" or "hr" => 3_600_000,
                "d" => 86_400_000,
                _ => 7 * 86_400_000.0,
            };
        }
        return (long)total;
    }

    /// <summary>
    /// A point in time: "now", "-1h" (relative to now), a date ("2024-05-01", start of day), a date and time
    /// with or without seconds, or a time of day ("10:30", today). Local time unless a zone is given.
    /// </summary>
    public static long? ParsePoint(string? s, long nowMs)
    {
        s = s?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        if (s.Equals("now", StringComparison.OrdinalIgnoreCase)) return nowMs;
        if (s[0] == '-' && ParseDuration(s[1..]) is { } back) return nowMs - back;
        if (Regex.IsMatch(s, @"^\d{4}-\d{2}-\d{2}$")) s += " 00:00:00";
        else if (Regex.IsMatch(s, @"^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}$")) s += ":00";
        else if (Regex.IsMatch(s, @"^\d{1,2}:\d{2}(:\d{2})?$"))
        {
            var parts = s.Split(':').Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
            var today = DateTimeOffset.FromUnixTimeMilliseconds(nowMs).ToLocalTime().Date;
            if (parts[0] > 23 || parts[1] > 59 || parts.Length > 2 && parts[2] > 59) return null;
            var local = today.AddHours(parts[0]).AddMinutes(parts[1]).AddSeconds(parts.Length > 2 ? parts[2] : 0);
            return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUnixTimeMilliseconds();
        }
        var t = Timestamps.Parse(s);
        return t == Timestamps.None ? null : t;
    }

    /// <summary>"Showing 10:00–11:00 · partial file": the shown range, with dates only where they are needed.</summary>
    public static string Describe(long? from, long? to)
    {
        static string F(long ms, string fmt) => DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString(fmt, CultureInfo.InvariantCulture);
        if (from is null && to is null) return "";
        if (from is null) return "until " + F(to!.Value, "yyyy-MM-dd HH:mm");
        if (to is null) return "since " + F(from.Value, "yyyy-MM-dd HH:mm");
        var sameDay = F(from.Value, "yyyy-MM-dd") == F(to.Value, "yyyy-MM-dd");
        return sameDay ? $"{F(from.Value, "yyyy-MM-dd HH:mm")}–{F(to.Value, "HH:mm")}" : $"{F(from.Value, "yyyy-MM-dd HH:mm")}–{F(to.Value, "yyyy-MM-dd HH:mm")}";
    }
}

/// <summary>
/// The command line: files to open, "-" for standard input, and an optional window:
/// <c>--since -1h</c>, <c>--since 2024-05-01T10:00 --until 2024-05-01T11:00</c>, <c>--last 30m</c>; what to show:
/// <c>--filter "level:error"</c>, <c>--search "timeout"</c>, <c>--goto 10:30</c>, <c>--levels error,warn</c>,
/// <c>--format json</c>, <c>--merge</c>, <c>--new-tab</c>; or an <c>ultralogviewer://open?…</c> link. See <see cref="OpenRequest"/>.
/// </summary>
public sealed class CommandLine
{
    public List<string> Files { get; } = [];
    public bool Stdin { get; private set; }
    public OpenWindow? Window { get; private set; }
    public List<string> Errors { get; } = [];
    public string? Filter { get; private set; }
    public string? Search { get; private set; }
    /// <summary>Raw "go to" text (parsed by <see cref="OpenRequest.ParseGoTo"/>).</summary>
    public string? GoTo { get; private set; }
    public string? Levels { get; private set; }
    public string? Format { get; private set; }
    public bool Merge { get; private set; }
    public bool NewTab { get; private set; }
    /// <summary>A deep link given as an argument (how Windows and Linux hand a clicked link to the app).</summary>
    public string? Url { get; private set; }

    public static CommandLine Parse(IReadOnlyList<string> args, long nowMs)
    {
        var c = new CommandLine();
        long? since = null, until = null, last = null;
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            string? Value(string name)
            {
                if (a.StartsWith(name + "=", StringComparison.Ordinal)) return a[(name.Length + 1)..];
                if (i + 1 < args.Count) return args[++i];
                c.Errors.Add($"{name} needs a value");
                return null;
            }
            bool Opt(string name) => a == name || a.StartsWith(name + "=", StringComparison.Ordinal);
            if (a == "-") c.Stdin = true;
            else if (a.StartsWith(OpenRequest.Scheme + ":", StringComparison.OrdinalIgnoreCase)) c.Url = a;
            else if (Opt("--filter")) c.Filter = Value("--filter") ?? c.Filter;
            else if (Opt("--search")) c.Search = Value("--search") ?? c.Search;
            else if (Opt("--goto")) c.GoTo = Value("--goto") ?? c.GoTo;
            else if (Opt("--levels")) c.Levels = Value("--levels") ?? c.Levels;
            else if (Opt("--format")) c.Format = Value("--format") ?? c.Format;
            else if (a == "--merge") c.Merge = true;
            else if (a == "--new-tab") c.NewTab = true;
            else if (a == "--since" || a.StartsWith("--since=", StringComparison.Ordinal))
            {
                var v = Value("--since");
                if (v is null) continue;
                since = TimeWindow.ParsePoint(v, nowMs);
                if (since is null) c.Errors.Add($"--since: cannot read \"{v}\"");
            }
            else if (a == "--until" || a.StartsWith("--until=", StringComparison.Ordinal))
            {
                var v = Value("--until");
                if (v is null) continue;
                until = TimeWindow.ParsePoint(v, nowMs);
                if (until is null) c.Errors.Add($"--until: cannot read \"{v}\"");
            }
            else if (a == "--last" || a.StartsWith("--last=", StringComparison.Ordinal))
            {
                var v = Value("--last");
                if (v is null) continue;
                last = TimeWindow.ParseDuration(v);
                if (last is null) c.Errors.Add($"--last: cannot read \"{v}\"");
            }
            else if (a.StartsWith('-') && !File.Exists(a)) continue; // other switches (the UI framework's)
            else c.Files.Add(a);
        }
        if (since is not null || until is not null || last is not null) c.Window = new OpenWindow(since, until, since is null ? last : null);
        return c;
    }
}
