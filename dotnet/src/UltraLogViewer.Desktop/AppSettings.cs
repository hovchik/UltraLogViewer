using System.Text.Json;
using System.Text.Json.Serialization;
using UltraLogViewer.Core;

namespace UltraLogViewer.Desktop;

public sealed class HighlightRule
{
    public string Text { get; set; } = "";
    public bool Regex { get; set; }
    public bool CaseSensitive { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>#RRGGBB</summary>
    public string Color { get; set; } = "#E8590C";
    /// <summary>Color the whole row instead of just the matched text.</summary>
    public bool Row { get; set; }
    [JsonIgnore] public TextPattern Pattern => new(Text, Regex, CaseSensitive);
}

public sealed class FilterPreset
{
    public string Name { get; set; } = "";
    public List<FilterRule> Rules { get; set; } = [];
    public string Query { get; set; } = "";
}

public sealed class SavedBookmark
{
    public int Line { get; set; }
    public string Note { get; set; } = "";
}

/// <summary>User settings, stored as JSON in the per-user application data folder.</summary>
public sealed class AppSettings
{
    public string Theme { get; set; } = "system";
    public List<HighlightRule> Highlights { get; set; } = DefaultHighlights();
    public List<FilterPreset> Presets { get; set; } = [];
    public List<CustomFormat> CustomFormats { get; set; } = [];
    public List<string> Recent { get; set; } = [];
    public bool ShowHistogram { get; set; } = true;
    public bool ShowDetail { get; set; } = true;
    public bool ShowSidebar { get; set; } = true;
    public double DetailHeight { get; set; } = 220;
    public Dictionary<string, List<SavedBookmark>> Bookmarks { get; set; } = [];
    public Dictionary<string, double> ColumnWidths { get; set; } = [];
    public List<string> HiddenColumns { get; set; } = [];
    /// <summary>Parsed fields the user added as columns, per format name.</summary>
    public Dictionary<string, List<string>> ExtraColumns { get; set; } = [];
    /// <summary>Column keys in the order the user arranged them, per format name.</summary>
    public Dictionary<string, List<string>> ColumnOrder { get; set; } = [];
    /// <summary>Recently applied quick-filter queries, most recent first.</summary>
    public List<string> QueryHistory { get; set; } = [];

    [JsonIgnore] public string? FilePath { get; set; }

    public static List<HighlightRule> DefaultHighlights() =>
    [
        new() { Text = @"\b\w+(Exception|Error)\b", Regex = true, CaseSensitive = true, Color = "#D9480F" },
        new() { Text = "timeout|timed out", Regex = true, Color = "#9C36B5" },
        new() { Text = @"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b", Regex = true, Color = "#1971C2" },
    ];

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static string DefaultPath
    {
        get
        {
            // ApplicationData can be empty (e.g. no HOME on Linux); never fall back to the working directory.
            var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(root)) root = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(root)) root = Path.GetTempPath();
            return Path.Combine(root, "UltraLogViewer", "settings.json");
        }
    }

    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new AppSettings();
                s.FilePath = path;
                return s;
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // corrupt or unreadable settings: start fresh
        }
        return new AppSettings { FilePath = path };
    }

    public void Save()
    {
        if (FilePath is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void AddQueryHistory(string query)
    {
        query = query.Trim();
        if (query.Length == 0) return;
        QueryHistory = new[] { query }.Concat(QueryHistory.Where(q => q != query)).Take(20).ToList();
    }

    public void AddRecent(IEnumerable<string> paths)
    {
        var list = paths.ToList();
        Recent = list.Concat(Recent.Where(p => !list.Contains(p))).Take(15).ToList();
    }
}
