using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace UltraLogViewer.Core;

/// <summary>Runs wevtutil.exe. Behind an interface so the export and follow logic can be tested anywhere with a fake.</summary>
public interface IWevtutil
{
    /// <summary>Runs wevtutil with these arguments (never through a shell), passing its output on in pieces as it comes.</summary>
    Task<(int Code, string Error)> RunAsync(IReadOnlyList<string> args, Action<string> output, CancellationToken ct);
}

public sealed class WevtutilProcess : IWevtutil
{
    public async Task<(int Code, string Error)> RunAsync(IReadOnlyList<string> args, Action<string> output, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("wevtutil.exe")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new IOException("Could not start wevtutil.exe");
        using var reg = ct.Register(() => { try { p.Kill(); } catch (InvalidOperationException) { } });
        var err = p.StandardError.ReadToEndAsync(ct);
        var buf = new char[64 * 1024];
        int n;
        while ((n = await p.StandardOutput.ReadAsync(buf, ct)) > 0) output(new string(buf, 0, n));
        await p.WaitForExitAsync(ct);
        return (p.ExitCode, (await err).Trim());
    }
}

/// <summary>
/// Windows event log XML (what <c>wevtutil qe /f:RenderedXml</c> prints) to one JSON line per event:
/// time, level, provider, eventId, channel, computer, recordId, task, opcode, keywords, user, message (from
/// RenderingInfo when present) and data (EventData's named values, or UserData's elements).
/// </summary>
public static class WinEventXml
{
    /// <summary>Picks whole &lt;Event&gt; elements out of wevtutil's output, which has no root element.</summary>
    public sealed class Splitter(int maxEvent = 4 * 1024 * 1024)
    {
        private readonly StringBuilder _buf = new();

        public List<string> Feed(string text)
        {
            var list = new List<string>();
            _buf.Append(text);
            var s = _buf.ToString();
            var pos = 0;
            while (true)
            {
                var start = FindStart(s, pos);
                if (start < 0)
                {
                    pos = Math.Max(pos, s.Length - 8);
                    break;
                }
                var end = s.IndexOf("</Event>", start, StringComparison.Ordinal);
                if (end < 0)
                {
                    pos = start;
                    if (s.Length - start > maxEvent) pos = s.Length; // runaway event: drop it
                    break;
                }
                end += "</Event>".Length;
                list.Add(s[start..end]);
                pos = end;
            }
            _buf.Remove(0, Math.Min(pos, _buf.Length));
            return list;
        }

        private static int FindStart(string s, int from)
        {
            while (true)
            {
                var i = s.IndexOf("<Event", from, StringComparison.Ordinal);
                if (i < 0 || i + 6 >= s.Length) return -1;
                if (s[i + 6] is ' ' or '>' or '\t' or '\r' or '\n') return i;
                from = i + 6; // <EventData>, <EventID>…
            }
        }
    }

    /// <summary>Level number to the name Event Viewer shows (0, LogAlways, shows as Information).</summary>
    public static string LevelName(string n) => n switch
    {
        "1" => "Critical",
        "2" => "Error",
        "3" => "Warning",
        "4" or "0" or "" => "Information",
        "5" => "Verbose",
        _ => n,
    };

    /// <summary>One event as a JSON line, or null when it is not well-formed.</summary>
    public static string? ToJson(string eventXml)
    {
        XElement ev;
        try
        {
            using var r = XmlReader.Create(new StringReader(eventXml), Log4jXml.Safe);
            ev = XElement.Load(r);
        }
        catch (XmlException)
        {
            return null;
        }
        if (ev.Name.LocalName != "Event") return null;
        static XElement? Child(XElement? e, string name) => e?.Elements().FirstOrDefault(x => x.Name.LocalName == name);
        static string Attr(XElement? e, string name) => e?.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value ?? "";
        var sys = Child(ev, "System");
        string S(string name) => Child(sys, name)?.Value.Trim() ?? "";
        var provider = Child(sys, "Provider");
        var render = Child(ev, "RenderingInfo");
        var keywords = string.Join(", ", Child(render, "Keywords")?.Elements().Select(k => k.Value.Trim()).Where(k => k.Length > 0) ?? []);

        var data = new List<KeyValuePair<string, string>>();
        if (Child(ev, "EventData") is { } ed)
        {
            var unnamed = 0;
            foreach (var d in ed.Elements())
            {
                var name = d.Name.LocalName == "Data" ? Attr(d, "Name") is { Length: > 0 } n ? n : $"Data{++unnamed}" : d.Name.LocalName;
                data.Add(new(name, Text(d)));
            }
        }
        // UserData holds one provider-defined element; its leaf elements become fields, nested ones dotted.
        else if (Child(ev, "UserData")?.Elements().FirstOrDefault() is { } ud)
        {
            void Walk(XElement e, string prefix)
            {
                foreach (var c in e.Elements())
                {
                    var k = prefix + c.Name.LocalName;
                    if (c.HasElements) Walk(c, k + ".");
                    else data.Add(new(k, c.Value.Trim()));
                }
            }
            Walk(ud, "");
        }

        var levelNum = S("Level");
        var level = LevelName(levelNum);
        // Security audits have level 0; Event Viewer marks failures only by keyword, which deserves a warning here.
        if (levelNum is "0" or "" && ulong.TryParse(S("Keywords").Replace("0x", ""), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var kw) && (kw & 0x10000000000000UL) != 0)
            level = "Warning";
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Log4jXml.JsonOptions))
        {
            void Opt(string key, string v)
            {
                if (v.Length > 0) w.WriteString(key, v);
            }
            string Named(string name)
            {
                var r = Child(render, name)?.Value.Trim() ?? "";
                if (r.Length > 0) return r;
                var v = S(name);
                return v == "0" ? "" : v;
            }
            w.WriteStartObject();
            w.WriteString("time", Attr(Child(sys, "TimeCreated"), "SystemTime"));
            w.WriteString("level", level);
            w.WriteString("provider", Attr(provider, "Name") is { Length: > 0 } pn ? pn : Attr(provider, "EventSourceName"));
            w.WriteString("eventId", S("EventID"));
            w.WriteString("channel", S("Channel"));
            w.WriteString("computer", S("Computer"));
            w.WriteString("recordId", S("EventRecordID"));
            Opt("task", Named("Task"));
            Opt("opcode", Named("Opcode"));
            Opt("keywords", keywords);
            Opt("user", Attr(Child(sys, "Security"), "UserID"));
            Opt("processId", Attr(Child(sys, "Execution"), "ProcessID"));
            Opt("threadId", Attr(Child(sys, "Execution"), "ThreadID"));
            Opt("activityId", Attr(Child(sys, "Correlation"), "ActivityID"));
            w.WriteString("message", Child(render, "Message")?.Value.Trim() ?? "");
            if (data.Count > 0)
            {
                w.WriteStartObject("data");
                foreach (var (k, v) in data.DistinctBy(p => p.Key).Take(100)) w.WriteString(k, v);
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        return Sanitize.Line(Encoding.UTF8.GetString(ms.ToArray()));
    }

    /// <summary>An element's text; one holding elements gives their texts, space-separated.</summary>
    private static string Text(XElement e) =>
        e.HasElements ? string.Join(" ", e.DescendantNodes().OfType<XText>().Select(t => t.Value.Trim()).Where(t => t.Length > 0)) : e.Value.Trim();
}

/// <summary>"Open Windows event log…": exports a channel to a capture file, then optionally follows new events.</summary>
public sealed partial class WinEventLog : ILiveCapture
{
    [GeneratedRegex(@"^[A-Za-z0-9][\w .%/-]{0,254}$")]
    private static partial Regex ChannelRe();

    public static readonly string[] CommonChannels = ["Application", "System", "Security", "Setup"];

    private readonly IWevtutil _wevtutil;
    private readonly string _channel;
    private readonly CaptureFile _capture;
    private readonly CancellationTokenSource _cts = new();
    private readonly WinEventXml.Splitter _splitter = new();
    private long _events;
    private long _lastRecord;
    private int _stopped;
    private string _state = "Reading";

    private WinEventLog(IWevtutil wevtutil, string channel, CaptureFile capture)
    {
        _wevtutil = wevtutil;
        _channel = channel;
        _capture = capture;
    }

    public string Path => _capture.Path;
    public CaptureFile Capture => _capture;
    public long Events => Interlocked.Read(ref _events);
    public long LastRecordId => Interlocked.Read(ref _lastRecord);
    public bool Running => _stopped == 0;
    /// <summary>Completes when the first export is done (with its error, or null).</summary>
    public Task<string?> FirstExport { get; private set; } = Task.FromResult<string?>(null);
    public bool Following { get; private set; }
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    public string Status =>
        $"Windows event log {_channel}\n{Events.ToString("N0", CultureInfo.CurrentCulture)} {(Events == 1 ? "event" : "events")} · {_state}"
        + (_capture.Dropped > 0 ? $" · {_capture.Dropped:N0} dropped" : "");

    /// <summary>A channel name wevtutil will take as a channel and not as an option.</summary>
    public static bool ValidChannel(string c) => ChannelRe().IsMatch(c) && !c.Contains("..", StringComparison.Ordinal);

    /// <summary>The XPath filter for a time range (null ends are open), or "*".</summary>
    public static string TimeQuery(long? fromMs, long? toMs)
    {
        static string T(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        var parts = new List<string>();
        if (fromMs is long f) parts.Add($"@SystemTime>='{T(f)}'");
        if (toMs is long t) parts.Add($"@SystemTime<='{T(t)}'");
        return parts.Count == 0 ? "*" : $"*[System[TimeCreated[{string.Join(" and ", parts)}]]]";
    }

    /// <summary>wevtutil arguments for one query, oldest first, as an argument array.</summary>
    public static string[] QueryArgs(string channel, string xpath) => ["qe", channel, "/f:RenderedXml", "/rd:false", "/q:" + xpath];

    /// <summary>
    /// Starts exporting <paramref name="channel"/> (events between the two times) to a new capture file; with
    /// <paramref name="follow"/> and no end time, keeps polling for events with a higher record id.
    /// </summary>
    public static WinEventLog Start(IWevtutil wevtutil, string channel, long? fromMs, long? toMs, bool follow, string? dir = null, long capBytes = 1L << 30, TimeSpan? poll = null)
    {
        if (!ValidChannel(channel)) throw new ArgumentException($"Not a channel name: {channel}");
        var safe = Regex.Replace(channel, @"[^\w-]+", "_");
        var log = new WinEventLog(wevtutil, channel, CaptureFile.Create(dir, "eventlog-" + (safe.Length > 40 ? safe[..40] : safe), capBytes)) { PollInterval = poll ?? TimeSpan.FromSeconds(2) };
        log.Following = follow && toMs is null;
        log.FirstExport = log.RunAsync(fromMs, toMs);
        return log;
    }

    private void OnOutput(string text)
    {
        foreach (var ev in _splitter.Feed(text))
        {
            var json = WinEventXml.ToJson(ev);
            if (json is null) continue;
            if (RecordId(json) is long id && id > Interlocked.Read(ref _lastRecord)) Interlocked.Exchange(ref _lastRecord, id);
            if (_capture.WriteLine(json)) Interlocked.Increment(ref _events);
        }
    }

    private static long? RecordId(string json)
    {
        var i = json.IndexOf("\"recordId\":\"", StringComparison.Ordinal);
        if (i < 0) return null;
        i += 12;
        var j = json.IndexOf('"', i);
        return j > i && long.TryParse(json.AsSpan(i, j - i), out var v) ? v : null;
    }

    private async Task<string?> RunAsync(long? fromMs, long? toMs)
    {
        string? error = null;
        try
        {
            var (code, err) = await _wevtutil.RunAsync(QueryArgs(_channel, TimeQuery(fromMs, toMs)), OnOutput, _cts.Token);
            if (code != 0) error = err.Length > 0 ? err : $"wevtutil exited with code {code}";
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            error = e.Message;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        if (error is not null || !Following)
        {
            _state = error is null ? "done" : "error: " + error;
            Interlocked.Exchange(ref _stopped, 1);
            _capture.Dispose();
            return error;
        }
        _state = "following new events";
        _ = Task.Run(FollowLoop);
        return null;
    }

    private async Task FollowLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, _cts.Token);
                var q = $"*[System[EventRecordID>{LastRecordId.ToString(CultureInfo.InvariantCulture)}]]";
                var (code, err) = await _wevtutil.RunAsync(QueryArgs(_channel, q), OnOutput, _cts.Token);
                _state = code == 0 ? "following new events" : "error: " + (err.Length > 0 ? err : $"code {code}") + " (retrying)";
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                _state = "error: " + e.Message + " (retrying)";
            }
        }
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        _state = "stopped";
        _cts.Cancel();
        _capture.Dispose();
    }

    public void Dispose() => Stop();
}

/// <summary>The JSON lines <see cref="WinEventLog"/> writes: Windows event columns instead of the generic JSON ones.</summary>
public sealed partial class WinEventFormat : LogFormat
{
    [GeneratedRegex(@"^\{""time"":""([^""]*)"",""level"":""([^""]*)"",""provider"":")]
    internal static partial Regex Head();

    public override string Id => "winevent";
    public override string Name => "Windows event log (exported)";
    public override IReadOnlyList<ColumnDef> Columns { get; } =
        [CTime, CLevel, Col("provider", "Provider", 170), Col("eventId", "Event ID", 70), Col("computer", "Computer", 120), CMessage];

    public override QuickInfo Quick(string line)
    {
        var m = Head().Match(line);
        return m.Success ? new QuickInfo(Levels.Parse(m.Groups[2].Value), Timestamps.Parse(m.Groups[1].Value)) : new QuickInfo(Levels.Detect(line), Timestamps.None);
    }

    public override Dictionary<string, string> Parse(string entry)
    {
        if (JsonFormat.TryJson(entry) is not JsonElement o) return new() { ["message"] = entry };
        var d = new Dictionary<string, string>();
        foreach (var k in new[] { "time", "level", "provider", "eventId", "computer", "message" })
            d[k] = o.TryGetProperty(k, out var v) ? JsonFormat.Str(v) : "";
        if (d["level"].Length > 0) d["level"] = L(Levels.Parse(d["level"])) is { Length: > 0 } l ? l : d["level"];
        // No rendered message (plain XML export or a provider without message files): show the data instead.
        if (d["message"].Length == 0 && o.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            d["message"] = string.Join(" ", data.EnumerateObject().Select(p => $"{p.Name}={JsonFormat.Str(p.Value)}"));
        JsonFormat.AddExtras(d, o, "", ["time", "level", "provider", "eventId", "computer", "message"], 0);
        return d;
    }
}
