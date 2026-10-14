using System.Text.Json;
using System.Text.Json.Serialization;
using UltraLogViewer.Core;

namespace UltraLogViewer.Desktop;

/// <summary>A saved workspace (.ulvw): open files with their filters, search, bookmarks and columns, plus highlights.</summary>
public sealed class WorkspaceFile
{
    public int Version { get; set; } = 1;
    public int Active { get; set; }
    public List<HighlightRule> Highlights { get; set; } = [];
    public List<WorkspaceTab> Tabs { get; set; } = [];
}

public sealed class WorkspaceTab
{
    public string[] Paths { get; set; } = [];
    /// <summary>The same paths relative to the workspace file's folder, used when the absolute ones are gone.</summary>
    public string[] RelPaths { get; set; } = [];
    public string Format { get; set; } = "";
    /// <summary>The detected format name the column settings belong to.</summary>
    public string FormatKey { get; set; } = "";
    public string Encoding { get; set; } = "auto";
    public FilterSpec Filter { get; set; } = new();
    public WorkspaceSearch Search { get; set; } = new();
    public List<WorkspaceBookmark> Bookmarks { get; set; } = [];
    public List<string> ExtraColumns { get; set; } = [];
    public List<string> ColumnOrder { get; set; } = [];
    public List<string> HiddenColumns { get; set; } = [];
}

public sealed class WorkspaceSearch
{
    public string Text { get; set; } = "";
    public bool Regex { get; set; }
    public bool CaseSensitive { get; set; }
    public bool WholeWord { get; set; }
}

public sealed class WorkspaceBookmark
{
    public string Path { get; set; } = "";
    public int Line { get; set; }
    public string Note { get; set; } = "";
}

public static class Workspace
{
    public const string Extension = "ulvw";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize(WorkspaceFile w) => JsonSerializer.Serialize(w, Json);

    public static WorkspaceFile Deserialize(string json) => JsonSerializer.Deserialize<WorkspaceFile>(json, Json) ?? new WorkspaceFile();

    /// <summary>Relative path from the workspace folder, with forward slashes so it works on every OS.</summary>
    public static string Relative(string path, string dir) => System.IO.Path.GetRelativePath(dir, path).Replace('\\', '/');

    /// <summary>The tab's files that exist: each absolute path, else its relative one resolved against the workspace folder.</summary>
    public static string[] Resolve(WorkspaceTab tab, string dir, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        var list = new List<string>();
        for (var i = 0; i < tab.Paths.Length; i++)
        {
            if (exists(tab.Paths[i])) list.Add(tab.Paths[i]);
            else if (i < tab.RelPaths.Length && System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, tab.RelPaths[i])) is var rel && exists(rel)) list.Add(rel);
        }
        return list.ToArray();
    }
}
