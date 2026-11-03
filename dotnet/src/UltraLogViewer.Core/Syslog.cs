using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace UltraLogViewer.Core;

/// <summary>One syslog message split into its RFC 5424 parts (RFC 3164 messages fill what they have).</summary>
public sealed class SyslogMessage
{
    /// <summary>The PRI value 0–191, or -1 when the message had none.</summary>
    public int Pri { get; init; } = -1;
    /// <summary>5424, 3164, or 0 when no header was recognised (the whole text is the message).</summary>
    public int Rfc { get; init; }
    public string Time { get; init; } = "";
    public string Host { get; init; } = "";
    public string App { get; init; } = "";
    public string Pid { get; init; } = "";
    public string MsgId { get; init; } = "";
    /// <summary>The structured data as written ("" for none), and its parameters as "SD-ID.name" = value.</summary>
    public string StructuredData { get; init; } = "";
    public List<KeyValuePair<string, string>> Params { get; init; } = [];
    public string Message { get; init; } = "";

    public int Facility => Pri < 0 ? -1 : Pri / 8;
    public int Severity => Pri < 0 ? -1 : Pri % 8;
}

/// <summary>
/// Syslog parsing (RFC 5424 and RFC 3164), the normalised line the receivers write, and the field names the formats
/// show. Network input is untrusted: every parser here accepts any text and falls back to "the whole text is the message".
/// </summary>
public static partial class Syslog
{
    public static readonly string[] Facilities =
    [
        "kern", "user", "mail", "daemon", "auth", "syslog", "lpr", "news", "uucp", "cron", "authpriv", "ftp", "ntp", "security",
        "console", "solaris-cron", "local0", "local1", "local2", "local3", "local4", "local5", "local6", "local7",
    ];
    public static readonly string[] Severities = ["emerg", "alert", "crit", "err", "warning", "notice", "info", "debug"];

    private static readonly string[] Months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    public static string FacilityName(int pri) => pri < 0 ? "" : Facilities[Math.Min(pri / 8, Facilities.Length - 1)];
    public static string SeverityName(int pri) => pri < 0 ? "" : Severities[pri % 8];

    [GeneratedRegex(@"^([A-Z][a-z]{2}) {1,2}(\d{1,2}) (\d{2}):(\d{2}):(\d{2})(?:\.(\d{1,6}))? ")]
    private static partial Regex BsdTime();
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,9})?(?:Z|[+-]\d{2}:\d{2}) ")]
    private static partial Regex IsoTime();
    [GeneratedRegex(@"^([^\s:\[\]]{1,64})(?:\[([^\]\s]{1,32})\])?:(?: |$)")]
    private static partial Regex Tag();

    /// <summary>"&lt;PRI&gt;" at the start: 1–3 digits, no leading zero, at most 191. Returns the length taken (0 = none).</summary>
    private static int ReadPri(string s, out int pri)
    {
        pri = -1;
        if (s.Length < 3 || s[0] != '<') return 0;
        var i = 1;
        var v = 0;
        while (i < s.Length && i <= 4 && char.IsAsciiDigit(s[i])) v = v * 10 + (s[i++] - '0');
        var digits = i - 1;
        if (digits == 0 || digits > 3 || i >= s.Length || s[i] != '>' || (digits > 1 && s[1] == '0') || v > 191) return 0;
        pri = v;
        return i + 1;
    }

    /// <summary>Parses one received message (framing already removed). Never throws.</summary>
    public static SyslogMessage Parse(string s)
    {
        var at = ReadPri(s, out var pri);
        if (at > 0 && s.Length >= at + 2 && s[at] == '1' && s[at + 1] == ' ' && Parse5424(s, at + 2, pri) is { } m5) return m5;
        return Parse3164(s, at, pri);
    }

    private static string Field(string s, ref int i)
    {
        var j = s.IndexOf(' ', i);
        if (j < 0) j = s.Length;
        var v = s[i..j];
        i = Math.Min(s.Length, j + 1);
        return v == "-" ? "" : v;
    }

    private static SyslogMessage? Parse5424(string s, int i, int pri)
    {
        var time = Field(s, ref i);
        var host = Field(s, ref i);
        var app = Field(s, ref i);
        var pid = Field(s, ref i);
        var msgid = Field(s, ref i);
        if (i >= s.Length && msgid.Length == 0 && app.Length == 0) return null; // too short to be RFC 5424
        var ps = new List<KeyValuePair<string, string>>();
        var sdStart = i;
        var sdEnd = ReadStructuredData(s, i, ps);
        string sd;
        if (sdEnd < 0)
        {
            sd = ""; // malformed structured data: keep the text as the message
            ps.Clear();
        }
        else
        {
            sd = s[sdStart..sdEnd] == "-" ? "" : s[sdStart..sdEnd];
            i = sdEnd < s.Length && s[sdEnd] == ' ' ? sdEnd + 1 : sdEnd;
        }
        var msg = i < s.Length ? s[i..] : "";
        if (msg.Length > 0 && msg[0] == '\uFEFF') msg = msg[1..]; // the BOM RFC 5424 puts before UTF-8 text
        return new SyslogMessage { Pri = pri, Rfc = 5424, Time = time, Host = host, App = app, Pid = pid, MsgId = msgid, StructuredData = sd, Params = ps, Message = msg };
    }

    /// <summary>Reads "-" or one or more [SD-ID name="value" …] elements. Returns the end index, or -1 when malformed.</summary>
    internal static int ReadStructuredData(string s, int i, List<KeyValuePair<string, string>> ps)
    {
        if (i < s.Length && s[i] == '-' && (i + 1 == s.Length || s[i + 1] == ' ')) return i + 1;
        if (i >= s.Length || s[i] != '[') return -1;
        var sb = new StringBuilder();
        while (i < s.Length && s[i] == '[')
        {
            var j = i + 1;
            while (j < s.Length && s[j] is not (' ' or ']' or '"' or '=')) j++;
            var id = s[(i + 1)..j];
            if (id.Length == 0 || j >= s.Length) return -1;
            i = j;
            while (i < s.Length && s[i] == ' ')
            {
                j = ++i;
                while (j < s.Length && s[j] is not (' ' or ']' or '"' or '=')) j++;
                if (j + 1 >= s.Length || s[j] != '=' || s[j + 1] != '"' || j == i) return -1;
                var name = s[i..j];
                i = j + 2;
                sb.Clear();
                while (i < s.Length && s[i] != '"')
                {
                    if (s[i] == '\\' && i + 1 < s.Length && s[i + 1] is '"' or '\\' or ']') i++;
                    sb.Append(s[i++]);
                }
                if (i >= s.Length) return -1;
                i++; // closing quote
                if (ps.Count < 50) ps.Add(new(id + "." + name, sb.ToString()));
            }
            if (i >= s.Length || s[i] != ']') return -1;
            i++;
        }
        return i;
    }

    private static SyslogMessage Parse3164(string s, int at, int pri)
    {
        var rest = s[at..];
        string time;
        var tm = BsdTime().Match(rest);
        if (tm.Success) time = tm.Value[..^1];
        else if (IsoTime().Match(rest) is { Success: true } im) time = (tm = im).Value[..^1];
        else return new SyslogMessage { Pri = pri, Message = rest.TrimStart() };
        rest = rest[tm.Length..];
        // HOSTNAME is optional in practice: "Oct 11 22:14:15 su: …" has the tag right after the time.
        var host = "";
        if (!Tag().IsMatch(rest))
        {
            var sp = rest.IndexOf(' ');
            host = sp < 0 ? rest : rest[..sp];
            rest = sp < 0 ? "" : rest[(sp + 1)..];
        }
        var app = "";
        var pid = "";
        if (Tag().Match(rest) is { Success: true } t)
        {
            app = t.Groups[1].Value;
            pid = t.Groups[2].Value;
            rest = rest[t.Length..];
        }
        return new SyslogMessage { Pri = pri, Rfc = 3164, Time = time, Host = host, App = app, Pid = pid, Message = rest };
    }

    /// <summary>
    /// RFC 3164 "Mmm dd hh:mm:ss" as an RFC 3339 time: the year is this year, or last year when that would put the
    /// time more than two days in the future (December messages read in January); the zone is the local one.
    /// </summary>
    internal static string BsdToIso(string bsd, DateTimeOffset now)
    {
        var m = BsdTime().Match(bsd + " ");
        if (!m.Success) return bsd;
        var mon = Array.FindIndex(Months, x => x.Equals(m.Groups[1].Value, StringComparison.OrdinalIgnoreCase)) + 1;
        var day = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        if (mon < 1 || day < 1 || day > 31) return bsd;
        var frac = m.Groups[6].Success ? int.Parse((m.Groups[6].Value + "00")[..3], CultureInfo.InvariantCulture) : 0;
        DateTime? Make(int y) =>
            day <= DateTime.DaysInMonth(y, mon) && int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) is var h && h < 24
                ? new DateTime(y, mon, day, h, Math.Min(59, int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture)), Math.Min(59, int.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture)), frac, DateTimeKind.Local)
                : null;
        var local = now.ToLocalTime().DateTime;
        var d = Make(local.Year);
        if (d is { } dd && dd > local.AddDays(2)) d = Make(local.Year - 1);
        return d is { } v ? Iso(new DateTimeOffset(v)) : bsd;
    }

    internal static string Iso(DateTimeOffset t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture);

    /// <summary>A header token (no spaces, at most <paramref name="max"/> characters), or "-" when empty.</summary>
    private static string Token(string v, int max)
    {
        if (v.Length == 0) return "-";
        var sb = new StringBuilder(Math.Min(v.Length, max));
        foreach (var c in v)
        {
            if (sb.Length >= max) break;
            sb.Append(c is ' ' or '\t' ? '_' : c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// The line a syslog receiver writes for one message: always RFC 5424, so a capture holds one format whatever the
    /// senders use. RFC 3164 headers are converted; a missing time or host is filled with the receive time and the
    /// sender's address, and a missing PRI with 13 (user.notice), as RFC 3164 asks of relays.
    /// </summary>
    public static string ToLine(string raw, DateTimeOffset received, string sender)
    {
        var m = Parse(Sanitize.Line(raw));
        var pri = m.Pri < 0 ? 13 : m.Pri;
        var time = m.Rfc == 3164 && BsdTime().IsMatch(m.Time + " ") ? BsdToIso(m.Time, received) : m.Time;
        if (time.Length == 0) time = Iso(received);
        var host = m.Host.Length > 0 ? m.Host : sender;
        var sb = new StringBuilder(raw.Length + 64);
        sb.Append('<').Append(pri.ToString(CultureInfo.InvariantCulture)).Append(">1 ").Append(Token(time, 64)).Append(' ').Append(Token(host, 255)).Append(' ')
            .Append(Token(m.App, 48)).Append(' ').Append(Token(m.Pid, 128)).Append(' ').Append(Token(m.MsgId, 32)).Append(' ')
            .Append(m.StructuredData.Length > 0 ? m.StructuredData : "-");
        if (m.Message.Length > 0) sb.Append(' ').Append(m.Message);
        return sb.ToString();
    }
}

/// <summary>Cleaning text that arrives from the network before it is written to a capture.</summary>
public static class Sanitize
{
    /// <summary>
    /// Removes NULs and other control characters except tab, so one message stays one line and nothing can hide in
    /// the view: line breaks become a space, the rest (C0, DEL, C1) are dropped.
    /// </summary>
    public static string Line(string s)
    {
        var i = 0;
        while (i < s.Length && !IsControl(s[i])) i++;
        if (i == s.Length) return s;
        var sb = new StringBuilder(s.Length);
        sb.Append(s, 0, i);
        for (; i < s.Length; i++)
        {
            var c = s[i];
            if (!IsControl(c)) sb.Append(c);
            else if (c == '\n' || (c == '\r' && (i + 1 >= s.Length || s[i + 1] != '\n'))) sb.Append(' ');
        }
        return sb.ToString();
    }

    private static bool IsControl(char c) => (c < 0x20 && c != '\t') || (c >= 0x7F && c <= 0x9F);
}
