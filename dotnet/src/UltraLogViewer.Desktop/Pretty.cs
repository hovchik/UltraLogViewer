using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;

namespace UltraLogViewer.Desktop;

/// <summary>Pretty-prints JSON and XML payloads embedded in a log entry for the detail pane.</summary>
public static class Pretty
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Entry(string text)
    {
        var sb = new StringBuilder();
        var rest = text;
        // Try each opening bracket in turn: "[thread] payload {...}" must skip the "[thread]".
        var from = 0;
        for (int found = 0, attempts = 0; found < 5 && attempts < 64; attempts++)
        {
            var i = rest.IndexOfAny(['{', '['], from);
            if (i < 0) break;
            var j = rest.LastIndexOf(rest[i] == '{' ? '}' : ']');
            var json = j > i ? TryJson(rest[i..(j + 1)]) : null;
            if (json is null)
            {
                from = i + 1;
                continue;
            }
            sb.Append(rest[..i].TrimEnd()).Append('\n').Append(json).Append('\n');
            rest = rest[(j + 1)..];
            from = 0;
            found++;
        }
        var xi = rest.IndexOf('<');
        var xj = rest.LastIndexOf('>');
        if (xi >= 0 && xj > xi && TryXml(rest[xi..(xj + 1)]) is { } xml)
        {
            sb.Append(rest[..xi].TrimEnd()).Append('\n').Append(xml).Append('\n').Append(rest[(xj + 1)..]);
            return sb.ToString().Trim('\n');
        }
        sb.Append(rest);
        return sb.ToString().Trim('\n');
    }

    private static string? TryJson(string s)
    {
        try
        {
            var node = JsonNode.Parse(s);
            return node is JsonObject or JsonArray ? node.ToJsonString(Indented) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TryXml(string s)
    {
        try
        {
            var doc = XDocument.Parse(s);
            return doc.Root is null ? null : doc.Root.ToString(SaveOptions.None);
        }
        catch (XmlException)
        {
            return null;
        }
    }
}
