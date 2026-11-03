using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UltraLogViewer.Core;

/// <summary>One request to the model: fixed instructions plus the user part, which the consent dialog shows redacted and editable.</summary>
public sealed record AiPrompt(string System, string User);

/// <summary>
/// Builds the prompts for "Extract fields…", "Explain this entry / pattern" and "Ask AI for a filter", and checks the answers.
/// Pure functions; keep in sync with src/core/aiPrompts.ts.
/// </summary>
public static class AiPrompts
{
    /// <summary>Per-entry cap, so a giant stack trace or JSON blob cannot blow up the request.</summary>
    public const int MaxEntryChars = 4000;
    public const int MaxContextChars = 600;

    /// <summary>The quick-filter grammar, as the model needs it (the Help dialog has the long version).</summary>
    public const string Grammar = """
        Quick-filter query language:
        - words: entries containing all of them (case-insensitive); "exact phrase"; /regex/
        - a OR b, a | b: either (OR binds tighter than AND); -word or NOT word: without; and/or/not with parentheses
        - field:value: a parsed field contains the value (field:"two words", field:/re/)
        - field>=n, >, <, <=, ==, !=: compare numerically (durations like 2s, 150ms work), level>=warn compares by severity
        - field in (a,b): equals one of the values; logger:com.acme.* wildcard
        - exists(field); field?=x (equals x or is missing)
        - since:-3h, until:-10m (relative to now), since:2024-05-01T10:00, since:today
        - level:error matches the level; levels are trace, debug, info, warn, error, fatal
        """;

    public static AiPrompt Extract(IReadOnlyList<string> lines) => new(
        """
        You write regular expressions that parse log lines into fields for a log viewer.
        Answer with JSON only, no prose and no code fence: {"name": "<short format name>", "pattern": "<regex>"}
        Rules for the pattern:
        - It must work in both .NET and JavaScript: named groups as (?<name>...), no lookbehind, no inline flags, no possessive quantifiers.
        - Anchor it with ^ and make it match the whole first sample line.
        - Use the group names time, level, thread, logger and message for those parts; give other fields short lower-case names.
        - End with a message group that captures the rest of the line.
        - Prefer general classes (\d, \S+, [^\]]*) over literal values from the samples.
        """,
        "Sample lines (the first one must match):\n" + string.Join("\n", lines.Select(l => Cap(l, MaxContextChars))));

    public static AiPrompt ExplainEntry(string format, IReadOnlyList<string> before, string entry, IReadOnlyList<string> after, bool filtered)
    {
        var sb = new StringBuilder();
        sb.Append("Log format: ").Append(format).Append('\n');
        if (filtered) sb.Append("(The surrounding lines come from a filtered view.)\n");
        sb.Append("\nContext before:\n");
        foreach (var l in before) sb.Append(Cap(l, MaxContextChars)).Append('\n');
        sb.Append("\n>>> Entry to explain:\n").Append(Cap(entry, MaxEntryChars)).Append("\n<<<\n\nContext after:\n");
        foreach (var l in after) sb.Append(Cap(l, MaxContextChars)).Append('\n');
        return new(ExplainSystem("the entry between >>> and <<<"), sb.ToString().TrimEnd());
    }

    public static AiPrompt ExplainPattern(string format, string template, int count, IReadOnlyList<string> samples)
    {
        var sb = new StringBuilder();
        sb.Append("Log format: ").Append(format).Append('\n');
        sb.Append("Message pattern (<*> marks values that vary), seen ").Append(count.ToString("N0", CultureInfo.InvariantCulture)).Append(" times:\n");
        sb.Append(Cap(template, MaxEntryChars)).Append("\n\nExamples:\n");
        foreach (var s in samples) sb.Append(Cap(s, MaxContextChars)).Append('\n');
        return new(ExplainSystem("this log message pattern"), sb.ToString().TrimEnd());
    }

    private static string ExplainSystem(string what) => $"""
        You help a developer read application logs. Explain {what}: what it means, the likely cause,
        and what to check next. Be concise (at most about 200 words). Use plain text or simple Markdown
        (short headings, bullet lists, `code`). Some values were replaced by placeholders such as <ip> or <email>
        for privacy; do not guess them.
        """;

    /// <param name="fields">Parsed fields with their most common values (the sidebar's field stats).</param>
    /// <param name="levels">Level names present in the view, with counts.</param>
    public static AiPrompt Filter(string question, IReadOnlyList<(string Key, IReadOnlyList<string> Top)> fields,
        IReadOnlyList<(string Level, int Count)> levels, long? timeMin, long? timeMax, DateTime now)
    {
        var sb = new StringBuilder();
        sb.Append("Request: ").Append(question.Trim()).Append("\n\n");
        sb.Append("Now: ").Append(now.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)).Append('\n');
        if (timeMin is long a && timeMax is long b)
            sb.Append("The file covers ").Append(Timestamps.Format(a)).Append(" to ").Append(Timestamps.Format(b)).Append('\n');
        if (levels.Count > 0)
            sb.Append("Levels present: ").Append(string.Join(", ", levels.Select(l => $"{l.Level.ToLowerInvariant()} ({l.Count.ToString(CultureInfo.InvariantCulture)})"))).Append('\n');
        if (fields.Count > 0)
        {
            sb.Append("Fields and their most common values:\n");
            foreach (var (key, top) in fields.Take(20))
                sb.Append("- ").Append(key).Append(top.Count > 0 ? ": " + string.Join(", ", top.Take(8).Select(v => Cap(v.Replace('\n', ' '), 60))) : "").Append('\n');
        }
        return new(
            $$"""
            You turn a request into a query for a log viewer's quick filter.
            {{Grammar}}
            Use only fields from the list (or plain words). For times relative to now use since:/until:.
            Answer with JSON only, no prose and no code fence: {"query": "<the query>", "explanation": "<one short sentence>"}
            """,
            sb.ToString().TrimEnd());
    }

    // ------------------------------------------------------------------ answers

    /// <summary>The JSON object in an answer: tolerates a code fence or a sentence around it.</summary>
    public static JsonElement? JsonObject(string answer)
    {
        var a = answer.IndexOf('{');
        var b = answer.LastIndexOf('}');
        if (a < 0 || b <= a) return null;
        try
        {
            using var doc = JsonDocument.Parse(answer[a..(b + 1)]);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Checks an "Extract fields" answer: valid JSON, a regex that compiles, has named groups and matches the sample.</summary>
    public static (string Name, string Pattern)? ParseExtract(string answer, string sample, out string? error)
    {
        error = null;
        if (JsonObject(answer) is not { } o || Str(o, "pattern") is not { Length: > 0 } pattern)
        {
            error = "the answer was not the JSON that was asked for";
            return null;
        }
        Regex re;
        try
        {
            re = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException e)
        {
            error = $"the regex does not compile ({e.Message})";
            return null;
        }
        if (!re.GetGroupNames().Any(g => !int.TryParse(g, out _)))
        {
            error = "the regex has no named groups";
            return null;
        }
        try
        {
            if (!re.IsMatch(sample))
            {
                error = "the regex does not match the line";
                return null;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            error = "the regex is too slow";
            return null;
        }
        var name = Str(o, "name")?.Trim();
        return (string.IsNullOrEmpty(name) ? "Extracted" : Cap(name, 40), pattern);
    }

    /// <summary>Reads a "filter" answer. The query is checked by the caller with <see cref="Query.ValidateQuery"/>.</summary>
    public static (string Query, string Explanation)? ParseFilter(string answer, out string? error)
    {
        error = null;
        if (JsonObject(answer) is not { } o || Str(o, "query") is not { } q || q.Trim().Length == 0)
        {
            error = "the answer was not the JSON that was asked for";
            return null;
        }
        return (q.Trim(), Str(o, "explanation")?.Trim() ?? "");
    }

    /// <summary>
    /// Light Markdown for the answer pane: heading marks and emphasis dropped, bullets as •, code fences removed.
    /// The answer is shown as plain selectable text, so nothing in it is ever interpreted as markup or a link.
    /// </summary>
    public static string PlainAnswer(string md)
    {
        var lines = md.Replace("\r\n", "\n").Split('\n').Where(l => !l.TrimStart().StartsWith("```")).Select(l =>
        {
            var t = l.TrimStart();
            var indent = l[..(l.Length - t.Length)];
            if (t.StartsWith('#')) t = t.TrimStart('#').Trim();
            else if (t.StartsWith("- ") || t.StartsWith("* ")) t = "• " + t[2..];
            return indent + t.Replace("**", "").Replace("__", "");
        });
        return string.Join("\n", lines).Trim();
    }

    public static string Cap(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
