using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml;

namespace UltraLogViewer.Core;

/// <summary>
/// log4j 1 XMLLayout (log4j:event, also what Chainsaw and log4j2's XmlLayout-compatible senders use) and log4net
/// XmlLayout (log4net:event) events, as they arrive over TCP: a stream of event elements with no root and undeclared
/// prefixes. Each event becomes one JSON line (time, level, logger, thread, message, exception, …) that the JSON
/// format shows with all its fields.
/// </summary>
public sealed class Log4jXmlSplitter(int maxEvent)
{
    private readonly StringBuilder _buf = new();

    public int Truncated { get; private set; }

    /// <summary>Adds decoded text and returns the complete events found so far (as XML fragments).</summary>
    public List<string> Feed(string text)
    {
        var events = new List<string>();
        _buf.Append(text);
        while (true)
        {
            var s = _buf.ToString();
            var start = Start(s, 0);
            if (start < 0)
            {
                // keep a tail that may be the beginning of a start tag
                var keep = Math.Min(s.Length, 16);
                _buf.Remove(0, s.Length - keep);
                return events;
            }
            var prefix = s[(start + 1)..s.IndexOf(":event", start, StringComparison.Ordinal)];
            var close = "</" + prefix + ":event>";
            var end = s.IndexOf(close, start, StringComparison.Ordinal);
            if (end < 0)
            {
                _buf.Remove(0, start);
                if (_buf.Length > maxEvent)
                {
                    // an event without an end: drop it and look for the next one
                    Truncated++;
                    var next = Start(_buf.ToString(), 1);
                    _buf.Remove(0, next < 0 ? _buf.Length : next);
                    continue;
                }
                return events;
            }
            end += close.Length;
            if (end - start <= maxEvent) events.Add(s[start..end]);
            else Truncated++;
            _buf.Remove(0, end);
        }
    }

    private static int Start(string s, int from)
    {
        var a = s.IndexOf("<log4j:event", from, StringComparison.Ordinal);
        var b = s.IndexOf("<log4net:event", from, StringComparison.Ordinal);
        return a < 0 ? b : b < 0 ? a : Math.Min(a, b);
    }
}

public static class Log4jXml
{
    internal static readonly JsonWriterOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal static readonly XmlReaderSettings Safe = new()
    {
        DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreProcessingInstructions = true, IgnoreComments = true,
        MaxCharactersInDocument = 16 * 1024 * 1024,
    };

    /// <summary>
    /// One event as a JSON line, or null when it is not well-formed XML. Attribute and element names are matched
    /// without their prefix, so log4j and log4net events read the same.
    /// </summary>
    public static string? ToJson(string fragment)
    {
        var wrapped = "<r xmlns:log4j=\"urn:log4j\" xmlns:log4net=\"urn:log4net\">" + fragment + "</r>";
        string logger = "", level = "", thread = "", time = "", message = "", exception = "", ndc = "";
        var attrs = new List<KeyValuePair<string, string>>();
        var props = new List<KeyValuePair<string, string>>();
        var location = new List<KeyValuePair<string, string>>();
        try
        {
            using var r = XmlReader.Create(new StringReader(wrapped), Safe);
            r.MoveToContent();
            r.Read(); // into <r>
            while (r.NodeType != XmlNodeType.Element && r.Read()) { }
            if (r.NodeType != XmlNodeType.Element || r.LocalName != "event") return null;
            if (r.MoveToFirstAttribute())
            {
                do
                {
                    switch (r.LocalName)
                    {
                        case "logger": logger = r.Value; break;
                        case "level": level = r.Value; break;
                        case "thread": thread = r.Value; break;
                        case "timestamp": time = Time(r.Value); break;
                        default: if (r.Prefix != "xmlns" && r.LocalName != "xmlns") attrs.Add(new(r.LocalName, r.Value)); break;
                    }
                } while (r.MoveToNextAttribute());
                r.MoveToElement();
            }
            if (r.IsEmptyElement) return Build();
            var depth = r.Depth;
            r.Read();
            while (!r.EOF && r.Depth > depth)
            {
                if (r.NodeType != XmlNodeType.Element)
                {
                    r.Read();
                    continue;
                }
                switch (r.LocalName)
                {
                    // ReadElementContentAsString moves past the element by itself
                    case "message": message = r.ReadElementContentAsString(); continue;
                    case "throwable" or "exception": exception = r.ReadElementContentAsString(); continue;
                    case "NDC": ndc = r.ReadElementContentAsString(); continue;
                    case "data":
                        var n = r.GetAttribute("name");
                        if (n is not null && props.Count < 50) props.Add(new(n, r.GetAttribute("value") ?? ""));
                        break;
                    case "locationInfo":
                        foreach (var k in new[] { "class", "method", "file", "line" })
                            if (r.GetAttribute(k) is { Length: > 0 } v) location.Add(new(k, v));
                        break;
                }
                r.Read();
            }
        }
        catch (XmlException)
        {
            return null;
        }
        return Build();

        string Build()
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, JsonOptions))
            {
                w.WriteStartObject();
                w.WriteString("time", time);
                w.WriteString("level", level);
                w.WriteString("logger", logger);
                w.WriteString("thread", thread);
                w.WriteString("message", message);
                if (exception.Length > 0) w.WriteString("exception", exception.TrimEnd());
                if (ndc.Length > 0) w.WriteString("ndc", ndc);
                foreach (var (k, v) in location) w.WriteString(k, v);
                foreach (var (k, v) in attrs) w.WriteString(k, v);
                if (props.Count > 0)
                {
                    w.WriteStartObject("properties");
                    foreach (var (k, v) in props.DistinctBy(p => p.Key)) w.WriteString(k, v);
                    w.WriteEndObject();
                }
                w.WriteEndObject();
            }
            return Sanitize.Line(Encoding.UTF8.GetString(ms.ToArray()));
        }
    }

    /// <summary>log4j writes epoch milliseconds, log4net an ISO time; both become ISO with the local offset.</summary>
    private static string Time(string v) =>
        long.TryParse(v, out var ms) && ms > 0 && ms < 253402300800000 ? Syslog.Iso(DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime()) : v;
}
