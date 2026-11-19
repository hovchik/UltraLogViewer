using System.Reflection;

namespace UltraLogViewer.Desktop;

/// <summary>Name, version, copyright and description of the app, read from the assembly attributes set in the .csproj.</summary>
public static class AppInfo
{
    private static readonly Assembly Asm = typeof(AppInfo).Assembly;

    public static string Product => Asm.GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? "UltraLogViewer";

    /// <summary>The version as written in the .csproj (0.2.0), without the "+commit" suffix the SDK may add.</summary>
    public static string Version
    {
        get
        {
            var v = Asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                    ?? Asm.GetName().Version?.ToString(3) ?? "0.0.0";
            var plus = v.IndexOf('+');
            return plus >= 0 ? v[..plus] : v;
        }
    }

    public static string Copyright => Asm.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "";

    public static string Description => Asm.GetCustomAttribute<AssemblyDescriptionAttribute>()?.Description ?? "";

    /// <summary>The UTC date the app was built (yyyy-MM-dd), or null when the build did not stamp one.</summary>
    public static string? BuildDate =>
        Asm.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "BuildDate")?.Value;

    public const string Homepage = "https://github.com/hovchik/UltraLogViewer";
}
