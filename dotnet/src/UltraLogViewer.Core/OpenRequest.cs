using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UltraLogViewer.Core;

/// <summary>Where an <see cref="OpenRequest"/> came from. A URL can come from any web page, so it is never trusted.</summary>
public enum RequestSource { CommandLine, Url, Api }

/// <summary>
/// "Go to" target of a request: a time (epoch ms), or a 1-based line in a file. <see cref="File"/> names the file of a
/// merged view: a 1-based position in its file list, or a file name; null is the first file.
/// </summary>
public sealed record GoToTarget(long? Time = null, int Line = 0, string? File = null)
{
    /// <summary>Text form, read back by <see cref="OpenRequest.ParseGoTo"/>: an ISO time (UTC) or "line:[file:]N".</summary>
    public override string ToString() => Time is long t
        ? DateTimeOffset.FromUnixTimeMilliseconds(t).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)
        : File is null ? $"line:{Line}" : $"line:{File}:{Line}";
}

/// <summary>
/// "Open this file at this time with this filter": the one request model behind the command line, the single-instance
/// handoff, <c>ultralogviewer://open?…</c> links, the local HTTP API and "Copy link to this view". It only describes what
/// to show; nothing in it can run a program.
/// </summary>
public sealed partial class OpenRequest
{
    public const string Scheme = "ultralogviewer";
    /// <summary>More than this many files in one request is refused (a link or glob must not open thousands of tabs).</summary>
    public const int MaxFiles = 64;
    private const int MaxText = 4000;

    /// <summary>Files, "archive.zip::member" paths, globs (* and ? in the file name) and "-" for standard input.</summary>
    public List<string> Paths { get; set; } = [];
    /// <summary>A format id (see <see cref="FormatDetector.BuiltIns"/> or "custom:…"); null detects it.</summary>
    public string? Format { get; set; }
    public OpenWindow? Window { get; set; }
    /// <summary>Quick-filter query.</summary>
    public string? Filter { get; set; }
    /// <summary>Text to find (plain text, highlighted and jumped to).</summary>
    public string? Search { get; set; }
    public GoToTarget? GoTo { get; set; }
    /// <summary>Levels to show, indexed by <see cref="Level"/>; null leaves the level toggles alone.</summary>
    public bool[]? Levels { get; set; }
    /// <summary>Several files open as one merged timeline (otherwise one tab each).</summary>
    public bool Merge { get; set; }
    /// <summary>Always open a new tab, even when one over the same files is open.</summary>
    public bool NewTab { get; set; }
    public RequestSource Source { get; set; }
    public List<string> Errors { get; } = [];

    public bool Stdin => Paths.Contains("-");
    public bool IsValid => Errors.Count == 0;

    // ================================================================= parsing parts

    /// <summary>"error,warn" → level toggles with only those on; null (and an error) when a name is unknown.</summary>
    public static bool[]? ParseLevels(string? s, List<string>? errors = null)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var on = new bool[LevelNames.Count];
        foreach (var raw in s.Split(new[] { ',', ' ', ';', '|' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int? lv = raw.Trim().ToLowerInvariant() switch
            {
                "trace" => 1,
                "debug" => 2,
                "info" => 3,
                "warn" or "warning" => 4,
                "error" or "err" => 5,
                "fatal" or "critical" => 6,
                "none" or "other" => 0,
                _ => null,
            };
            if (lv is null)
            {
                errors?.Add($"--levels: unknown level \"{raw}\"");
                return null;
            }
            on[lv.Value] = true;
        }
        return on;
    }

    /// <summary>Level toggles back to "warn,error" (null when all are on).</summary>
    public static string? LevelsText(bool[]? levels)
    {
        if (levels is null || levels.All(l => l)) return null;
        string[] names = ["none", "trace", "debug", "info", "warn", "error", "fatal"];
        return string.Join(",", Enumerable.Range(0, Math.Min(levels.Length, names.Length)).Where(i => levels[i]).Select(i => names[i]));
    }

    [GeneratedRegex(@"^(\d+)$")]
    private static partial Regex LineOnlyRe();

    /// <summary>
    /// A "go to" target: "line:N" / "line:FILE:N" (always a line), a bare number (a line of the first file), a time
    /// ("2024-05-01 10:00:05", "10:30", "-5m"), or "FILE:N" with FILE a 1-based file position or a file name.
    /// </summary>
    public static GoToTarget? ParseGoTo(string? s, long nowMs)
    {
        s = s?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        if (s.StartsWith("line:", StringComparison.OrdinalIgnoreCase)) return FileLine(s[5..]);
        if (LineOnlyRe().IsMatch(s)) return int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var l) && l > 0 ? new GoToTarget(Line: l) : null;
        if (TimeWindow.ParsePoint(s, nowMs) is long t) return new GoToTarget(Time: t);
        return FileLine(s);

        static GoToTarget? FileLine(string v)
        {
            // the last colon: a Windows path ("C:\logs\app.log:12") keeps its drive letter
            var i = v.LastIndexOf(':');
            var lineText = i < 0 ? v : v[(i + 1)..];
            if (!int.TryParse(lineText, NumberStyles.None, CultureInfo.InvariantCulture, out var line) || line <= 0) return null;
            var file = i < 0 ? null : v[..i].Trim();
            return new GoToTarget(Line: line, File: string.IsNullOrEmpty(file) ? null : file);
        }
    }

    /// <summary>Duration text for links: "15m", "2h", "90s" (exact, so it parses back to the same value).</summary>
    public static string DurationText(long ms)
    {
        foreach (var (unit, size) in new[] { ("d", 86_400_000L), ("h", 3_600_000L), ("m", 60_000L), ("s", 1000L) })
            if (ms > 0 && ms % size == 0) return $"{ms / size}{unit}";
        return $"{ms}ms";
    }

    private static string Iso(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static bool HasControl(string s) => s.Any(c => char.IsControl(c) || c is '\u2028' or '\u2029');

    // ============================================================== validation

    /// <summary>
    /// Checks the request; problems go to <see cref="Errors"/>. Every source: no control characters, at most
    /// <see cref="MaxFiles"/> paths, a plain format id. From a URL also: absolute local paths only (no "-", no
    /// relative paths, no network or device paths, which Windows would contact on its own); from the API, full paths.
    /// </summary>
    public OpenRequest Validate()
    {
        if (Paths.Count > MaxFiles) Errors.Add($"Too many files ({Paths.Count}; at most {MaxFiles})");
        foreach (var p in Paths)
        {
            if (p.Length == 0) Errors.Add("Empty path");
            else if (HasControl(p)) Errors.Add("A path contains control characters");
            else if (Source != RequestSource.CommandLine)
            {
                if (p == "-") Errors.Add("Standard input can only be read from the command line");
                else if (Source == RequestSource.Url && (p.StartsWith(@"\\", StringComparison.Ordinal) || p.StartsWith("//", StringComparison.Ordinal)))
                    Errors.Add($"Network paths are not opened from links: {p}");
                else if (!IsAbsolute(p)) Errors.Add($"A full path is needed: {p}");
            }
        }
        foreach (var (name, v) in new[] { ("filter", Filter), ("search", Search) })
        {
            if (v is null) continue;
            if (v.Length > MaxText) Errors.Add($"--{name} is too long");
            else if (HasControl(v)) Errors.Add($"--{name} contains control characters");
        }
        if (Format is { } f && !FormatIdRe().IsMatch(f)) Errors.Add($"--format: not a format id \"{f}\"");
        if (GoTo?.File is { } gf && HasControl(gf)) Errors.Add("--goto contains control characters");
        return this;
    }

    [GeneratedRegex(@"^(custom:)?[\w.\- ]{1,80}$")]
    private static partial Regex FormatIdRe();

    /// <summary>"/var/log/x", "C:\logs\x" or "C:/logs/x", on any OS (a link built on Windows parses the same on Linux).</summary>
    public static bool IsAbsolute(string p) =>
        p.StartsWith('/') || p.Length >= 3 && char.IsAsciiLetter(p[0]) && p[1] == ':' && (p[2] == '\\' || p[2] == '/');

    private static readonly string[] LogExtensions = [".log", ".txt", ".json", ".jsonl", ".ndjson", ".out", ".err", ".csv", ".gz", ".bz2", ".zip", ".trace", ".evtx", ".syslog"];

    /// <summary>
    /// Whether a path looks like a log file: a log extension, a rotated log ("app.log.3", "app.log.2024-05-01"), or no
    /// extension at all ("syslog", "messages"). Anything else (".exe", ".docx", ".ssh/id_rsa"…) is named in a warning.
    /// </summary>
    public static bool LooksLikeLog(string path)
    {
        var name = Archives.DisplayName(path).ToLowerInvariant();
        if (name.StartsWith('.')) return false; // dotfiles: .bashrc, .env, .git-credentials
        if (LogExtensions.Any(e => name.EndsWith(e, StringComparison.Ordinal))) return true;
        if (Regex.IsMatch(name, @"\.(log|txt|out|err)\.[\w.-]+$")) return true;
        return !name.Contains('.');
    }

    // ============================================================ expanding paths

    /// <summary>
    /// The files to open: relative paths resolved against <paramref name="cwd"/>, globs expanded (in name order), zip
    /// members and "-" kept. Missing files and globs that match nothing go to <see cref="Errors"/>.
    /// </summary>
    public List<string> ExpandPaths(string cwd)
    {
        var output = new List<string>();
        foreach (var raw in Paths)
        {
            // "-" is stdin; ssh://host/path and host:/path are remote files, opened over ssh as they are
            if (raw == "-" || RemoteTarget.LooksRemote(raw))
            {
                output.Add(raw);
                continue;
            }
            var p = Path.IsPathRooted(raw) ? raw : Path.Combine(cwd, raw);
            var name = Path.GetFileName(p);
            if (name.IndexOfAny(['*', '?']) >= 0 && Archives.SplitMember(p) is null)
            {
                var dir = Path.GetDirectoryName(p);
                var matches = dir is not null && Directory.Exists(dir)
                    ? Directory.EnumerateFiles(dir, name).Order(StringComparer.Ordinal).Take(MaxFiles + 1).ToList()
                    : [];
                if (matches.Count == 0) Errors.Add($"No files match {raw}");
                output.AddRange(matches.Select(Path.GetFullPath));
                continue;
            }
            var member = Archives.SplitMember(p);
            if (!File.Exists(member?.Archive ?? p)) Errors.Add($"File not found: {raw}");
            else output.Add(member is null ? Path.GetFullPath(p) : Archives.MemberPath(Path.GetFullPath(member.Value.Archive), member.Value.Member));
        }
        if (output.Count > MaxFiles) Errors.Add($"Too many files ({output.Count}; at most {MaxFiles})");
        return output.Distinct(StringComparer.Ordinal).Take(MaxFiles).ToList();
    }

    // ================================================================== command line

    /// <summary>The request of a parsed command line (a deep link on the command line wins: Windows starts us with it).</summary>
    public static OpenRequest FromCommandLine(CommandLine c, long nowMs)
    {
        if (c.Url is { } url) return FromUrl(url, nowMs);
        var r = new OpenRequest
        {
            Source = RequestSource.CommandLine,
            Paths = [.. c.Files],
            Window = c.Window,
            Filter = c.Filter,
            Search = c.Search,
            Format = c.Format,
            Merge = c.Merge,
            NewTab = c.NewTab,
        };
        if (c.Stdin) r.Paths.Add("-");
        r.Errors.AddRange(c.Errors);
        if (c.GoTo is { } g)
        {
            r.GoTo = ParseGoTo(g, nowMs);
            if (r.GoTo is null) r.Errors.Add($"--goto: cannot read \"{g}\"");
        }
        r.Levels = ParseLevels(c.Levels, r.Errors);
        return r.Validate();
    }

    // ========================================================================== URL

    /// <summary>
    /// <c>ultralogviewer://open?path=/var/log/app.log&amp;since=-1h&amp;filter=level:error&amp;goto=…</c>. Keys: path (repeat
    /// it for several files), format, since, until, last, filter, search, goto, levels, merge, newtab.
    /// </summary>
    public static OpenRequest FromUrl(string url, long nowMs)
    {
        var r = new OpenRequest { Source = RequestSource.Url };
        var m = UrlRe().Match(url.Trim());
        if (!m.Success)
        {
            r.Errors.Add("Not an ultralogviewer://open link");
            return r;
        }
        var args = new List<(string Key, string Value)>();
        foreach (var part in m.Groups[1].Value.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            static string D(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));
            try
            {
                args.Add(eq < 0 ? (D(part).ToLowerInvariant(), "") : (D(part[..eq]).ToLowerInvariant(), D(part[(eq + 1)..])));
            }
            catch (UriFormatException)
            {
                r.Errors.Add("The link is not encoded correctly");
            }
        }
        r.Apply(args, nowMs);
        return r.Validate();
    }

    [GeneratedRegex(@"^ultralogviewer:(?://)?open/?(?:\?([^#]*))?(?:#.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRe();

    private static bool Flag(string v) => v is "" or "1" or "true" or "yes" or "on";

    /// <summary>Applies key/value pairs (from a URL query) to the request.</summary>
    private void Apply(IEnumerable<(string Key, string Value)> args, long nowMs)
    {
        long? since = null, until = null, last = null;
        foreach (var (k, v) in args)
        {
            switch (k)
            {
                case "path" or "file": Paths.Add(v); break;
                case "format": Format = v.Length > 0 ? v : null; break;
                case "filter" or "q": Filter = v; break;
                case "search" or "find": Search = v; break;
                case "levels" or "level": Levels = ParseLevels(v, Errors); break;
                case "merge": Merge = Flag(v.ToLowerInvariant()); break;
                case "newtab": NewTab = Flag(v.ToLowerInvariant()); break;
                case "goto":
                    GoTo = ParseGoTo(v, nowMs);
                    if (GoTo is null) Errors.Add($"goto: cannot read \"{v}\"");
                    break;
                case "since":
                    since = TimeWindow.ParsePoint(v, nowMs);
                    if (since is null) Errors.Add($"since: cannot read \"{v}\"");
                    break;
                case "until":
                    until = TimeWindow.ParsePoint(v, nowMs);
                    if (until is null) Errors.Add($"until: cannot read \"{v}\"");
                    break;
                case "last":
                    last = TimeWindow.ParseDuration(v);
                    if (last is null) Errors.Add($"last: cannot read \"{v}\"");
                    break;
                // unknown keys are ignored, so newer links still open in older versions
            }
        }
        if (since is not null || until is not null || last is not null) Window = new OpenWindow(since, until, since is null ? last : null);
    }

    /// <summary>The request as a deep link (times as UTC ISO, so the link means the same moment everywhere).</summary>
    public string ToUrl()
    {
        var q = new List<string>();
        void Add(string k, string? v)
        {
            if (v is not null) q.Add($"{k}={Uri.EscapeDataString(v)}");
        }
        foreach (var p in Paths) Add("path", p);
        if (Merge && Paths.Count > 1) Add("merge", "1");
        Add("format", Format);
        if (Window is { } w)
        {
            if (w.Since is long s) Add("since", Iso(s));
            if (w.Until is long u) Add("until", Iso(u));
            if (w.Since is null && w.Last is long l) Add("last", DurationText(l));
        }
        Add("levels", LevelsText(Levels));
        if (!string.IsNullOrEmpty(Filter)) Add("filter", Filter);
        if (!string.IsNullOrEmpty(Search)) Add("search", Search);
        Add("goto", GoTo?.ToString());
        if (NewTab) Add("newtab", "1");
        return $"{Scheme}://open?{string.Join("&", q)}";
    }

    // ========================================================================= JSON

    /// <summary>
    /// The request as JSON (the HTTP API's <c>POST /open</c> body): {"paths": [...] or "path": "...", "format",
    /// "since"/"until" (a time spec or epoch ms), "last" (a duration or ms), "filter", "search", "goto" (text, or epoch
    /// ms for a time), "levels" ("error,warn" or a list), "merge", "newTab"}.
    /// </summary>
    public static OpenRequest FromJson(string json, long nowMs, RequestSource source = RequestSource.Api)
    {
        var r = new OpenRequest { Source = source };
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
        }
        catch (JsonException e)
        {
            r.Errors.Add($"Bad JSON: {e.Message}");
            return r;
        }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                r.Errors.Add("Expected a JSON object");
                return r;
            }
            var args = new List<(string, string)>();
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                var key = p.Name.ToLowerInvariant();
                var v = p.Value;
                switch (v.ValueKind)
                {
                    case JsonValueKind.Array when key is "paths" or "levels":
                        var items = v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList();
                        if (key == "paths") args.AddRange(items.Select(i => ("path", i)));
                        else args.Add(("levels", string.Join(",", items)));
                        break;
                    case JsonValueKind.String:
                        args.Add((key == "paths" ? "path" : key, v.GetString()!));
                        break;
                    case JsonValueKind.True or JsonValueKind.False:
                        args.Add((key, v.GetBoolean() ? "1" : "0"));
                        break;
                    case JsonValueKind.Number when v.TryGetInt64(out var n):
                        // epoch ms for times, ms for "last"
                        args.Add((key, key is "since" or "until" or "goto" ? Iso(n) : key == "last" ? $"{n}ms" : n.ToString(CultureInfo.InvariantCulture)));
                        break;
                    case JsonValueKind.Null:
                        break;
                    default:
                        r.Errors.Add($"\"{p.Name}\": unexpected value");
                        break;
                }
            }
            r.Apply(args, nowMs);
        }
        return r.Validate();
    }

    public string ToJson()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteStartArray("paths");
            foreach (var p in Paths) w.WriteStringValue(p);
            w.WriteEndArray();
            if (Format is not null) w.WriteString("format", Format);
            if (Window?.Since is long s) w.WriteString("since", Iso(s));
            if (Window?.Until is long u) w.WriteString("until", Iso(u));
            if (Window is { Since: null, Last: long l }) w.WriteString("last", DurationText(l));
            if (LevelsText(Levels) is { } lv) w.WriteString("levels", lv);
            if (!string.IsNullOrEmpty(Filter)) w.WriteString("filter", Filter);
            if (!string.IsNullOrEmpty(Search)) w.WriteString("search", Search);
            if (GoTo is not null) w.WriteString("goto", GoTo.ToString());
            if (Merge) w.WriteBoolean("merge", true);
            if (NewTab) w.WriteBoolean("newTab", true);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>The same request (for tests: what a link or JSON round trip must keep).</summary>
    public bool SameAs(OpenRequest o) =>
        Paths.SequenceEqual(o.Paths) && Format == o.Format && Equals(Window, o.Window) && (Filter ?? "") == (o.Filter ?? "")
        && (Search ?? "") == (o.Search ?? "") && Equals(GoTo, o.GoTo) && LevelsText(Levels) == LevelsText(o.Levels)
        && (Merge && Paths.Count > 1) == (o.Merge && o.Paths.Count > 1) && NewTab == o.NewTab;
}
