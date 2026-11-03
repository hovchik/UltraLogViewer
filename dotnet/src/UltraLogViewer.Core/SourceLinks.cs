using System.Text.RegularExpressions;

namespace UltraLogViewer.Core;

/// <summary>
/// A source location mentioned in an entry: <c>Foo.java:42</c>, <c>File.cs:line 42</c>, <c>at x (file.ts:10:5)</c>,
/// Python's <c>File "x.py", line 3</c>. <paramref name="Package"/> is the Java/Kotlin package of the frame
/// (<c>at com.acme.Foo.bar(Foo.java:42)</c> gives "com.acme"), which turns a bare file name into a path under a source root.
/// </summary>
public sealed record SourceRef(int Start, int Length, string Path, int Line, int Column = 0, string? Package = null);

/// <summary>Finds source locations in entry text and resolves them against the configured source roots.</summary>
public static class SourceLinks
{
    private const string Exts = "java|kt|kts|scala|groovy|cs|fs|vb|ts|tsx|js|jsx|mjs|cjs|py|go|rb|php|c|cc|cpp|cxx|h|hpp|m|mm|swift|rs|dart|ex|exs|erl|clj|lua|pl|vue|svelte";

    private static readonly Regex Python = new(@"File ""(?<path>[^""\r\n]+)"", line (?<line>\d+)", RegexOptions.CultureInvariant);
    /// <summary>.NET: "in C:\My Projects\App\File.cs:line 42" (spaces allowed after " in "), or a bare "File.cs:line 42".</summary>
    private static readonly Regex DotNetSpaced = new(@"(?<= in )(?<path>(?:[A-Za-z]:[\\/]|/)[^:\r\n]*?\.\w{1,6}):line (?<line>\d+)", RegexOptions.CultureInvariant);
    private static readonly Regex DotNet = new(@"(?<![\w.\\/-])(?<path>(?:[A-Za-z]:[\\/])?[^\s:()""'<>|]*\.\w{1,6}):line (?<line>\d+)", RegexOptions.CultureInvariant);
    private static readonly Regex Generic = new(
        $@"(?<![\w.\\/$-])(?:file:///?)?(?<path>(?:[A-Za-z]:[\\/])?[\w.$@~+/\\-]*?[\w$-]\.(?:{Exts})):(?<line>\d+)(?::(?<col>\d+))?(?!\d)",
        RegexOptions.CultureInvariant);
    /// <summary>The "at com.acme.Foo.bar(" before a Java-style frame's "(Foo.java:42)".</summary>
    private static readonly Regex JavaFrame = new(@"([\w$]+(?:\.[\w$]+)+)\.[\w$<>-]+\($", RegexOptions.CultureInvariant);

    /// <summary>Every source location in the text, in order and without overlaps.</summary>
    public static List<SourceRef> Find(string text)
    {
        var found = new List<SourceRef>();
        if (text.Length == 0) return found;
        void Scan(Regex re)
        {
            foreach (Match m in re.Matches(text))
            {
                if (!int.TryParse(m.Groups["line"].Value, out var line) || line <= 0) continue;
                var start = m.Index;
                var len = m.Length;
                if (found.Any(f => start < f.Start + f.Length && f.Start < start + len)) continue;
                var col = m.Groups["col"].Success && int.TryParse(m.Groups["col"].Value, out var c) ? c : 0;
                var path = m.Groups["path"].Value;
                found.Add(new SourceRef(start, len, path, line, col, PackageOf(text, start, path)));
            }
        }
        Scan(Python);
        Scan(DotNetSpaced);
        Scan(DotNet);
        Scan(Generic);
        found.Sort((a, b) => a.Start.CompareTo(b.Start));
        return found;
    }

    /// <summary>The package of a Java-style frame whose location is a bare file name ("at com.acme.Foo.bar(Foo.java:42)").</summary>
    private static string? PackageOf(string text, int start, string path)
    {
        if (path.Contains('/') || path.Contains('\\') || start == 0 || text[start - 1] != '(') return null;
        var lineStart = text.LastIndexOf('\n', start - 1) + 1;
        var m = JavaFrame.Match(text[lineStart..start]);
        if (!m.Success) return null;
        var cls = m.Groups[1].Value;
        var dot = cls.LastIndexOf('.');
        return dot > 0 ? cls[..dot] : null;
    }

    /// <summary>Paths to try, relative to a source root, best first: the path as written, then the package path of a Java-style frame.</summary>
    public static List<string> Candidates(SourceRef r)
    {
        var list = new List<string> { r.Path.Replace('\\', '/') };
        if (r.Package is { } pkg) list.Insert(0, pkg.Replace('.', '/') + "/" + r.Path);
        return list;
    }

    private static readonly HashSet<string> SourceExts = new(Exts.Split('|'), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The file on disk a location refers to, or null. The text comes from the log, so it is not trusted: only an
    /// existing source file (one of the known source extensions) inside a configured root is ever returned, and no path
    /// segment may step out of the root ("..", a drive or a stream after a colon). The roots are tried in order, and under
    /// each root the candidate paths, longest tail first, so an absolute path under a root opens as written and a path
    /// from the build machine, /build/agent/src/app/File.cs, still finds app/File.cs under a local root.
    /// </summary>
    public static string? Resolve(SourceRef r, IReadOnlyList<string> roots, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        if (!SourceExts.Contains(System.IO.Path.GetExtension(r.Path).TrimStart('.'))) return null;
        foreach (var raw in roots)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var root = raw.Trim();
            foreach (var cand in Candidates(r))
            {
                var segs = cand.Split('/', StringSplitOptions.RemoveEmptyEntries);
                // a drive letter is never a folder under a root
                var first = segs.Length > 0 && segs[0].EndsWith(':') ? 1 : 0;
                // the longest tail that stays inside the root: nothing before the last unsafe segment can be used
                for (var i = first; i < segs.Length; i++)
                    if (segs[i].TrimEnd(' ', '.').Length == 0 || segs[i].Contains(':')) first = i + 1; // also "...", ".. " (Windows trims those to "..")
                for (var k = first; k < segs.Length; k++)
                {
                    var p = System.IO.Path.Combine([root, .. segs[k..]]);
                    if (Inside(p, root) && exists(p)) return p;
                }
            }
        }
        return null;
    }

    /// <summary>Whether <paramref name="path"/>, normalised, is inside <paramref name="root"/> (a second check after the segment rules).</summary>
    private static bool Inside(string path, string root)
    {
        try
        {
            var full = System.IO.Path.GetFullPath(path);
            var dir = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root)) + System.IO.Path.DirectorySeparatorChar;
            return full.StartsWith(dir, OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Editor presets: id, label and the URL or command line template.</summary>
    public static readonly (string Id, string Label, string Template)[] Editors =
    [
        ("vscode", "VS Code", "vscode://file/{path}:{line}"),
        ("rider", "Rider", "rider://open?file={path}&line={line}"),
        ("intellij", "IntelliJ IDEA", "idea://open?file={path}&line={line}"),
        ("custom", "Custom command", ""),
    ];

    /// <summary>The template of an editor preset, or <paramref name="custom"/> for "custom".</summary>
    public static string TemplateFor(string editor, string custom) =>
        editor == "custom" ? custom : Editors.FirstOrDefault(e => e.Id == editor).Template ?? Editors[0].Template;

    /// <summary>A template is a URL ("scheme://…") rather than a command line.</summary>
    public static bool IsUrl(string template) => Regex.IsMatch(template.TrimStart(), @"^[A-Za-z][\w+.-]*://");

    /// <summary>
    /// A URL template that would hand the file itself to the system (file://{path}): that opens a source file with its
    /// default program, which for a script runs it, so it is refused.
    /// </summary>
    public static bool IsUnsafeUrl(string template) => Regex.IsMatch(template.TrimStart(), @"^file:", RegexOptions.IgnoreCase);

    /// <summary>A URL template with its placeholders filled in (forward slashes, spaces and specials escaped).</summary>
    public static string ExpandUrl(string template, string path, int line, int col)
    {
        var p = path.Replace('\\', '/');
        // vscode://file/{path} already has the slash before the path: vscode://file/home/me/x.cs, vscode://file/C:/x.cs
        if (template.Contains("://file/", StringComparison.Ordinal)) p = p.TrimStart('/');
        p = string.Concat(p.Select(c => c is ' ' or '#' or '%' or '?' or '&' ? Uri.HexEscape(c) : c.ToString()));
        return Fill(template.Trim(), p, line, col);
    }

    /// <summary>A command line template split into the program and its arguments, placeholders filled in per argument.</summary>
    public static (string Program, List<string> Args) ExpandCommand(string template, string path, int line, int col)
    {
        var parts = new List<string>();
        var cur = new System.Text.StringBuilder();
        var quoted = false;
        var any = false;
        foreach (var c in template.Trim())
        {
            if (c == '"')
            {
                quoted = !quoted;
                any = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any || cur.Length > 0) parts.Add(cur.ToString());
                cur.Clear();
                any = false;
            }
            else cur.Append(c);
        }
        if (any || cur.Length > 0) parts.Add(cur.ToString());
        var filled = parts.Select(a => Fill(a, path, line, col)).ToList();
        return filled.Count == 0 ? ("", []) : (filled[0], filled.Skip(1).ToList());
    }

    private static string Fill(string s, string path, int line, int col) =>
        s.Replace("{path}", path).Replace("{line}", line.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("{col}", Math.Max(1, col).ToString(System.Globalization.CultureInfo.InvariantCulture));

    public const string NoMatchHint = "Add the folder that holds your source code under Edit › Editor and source roots…";
}
