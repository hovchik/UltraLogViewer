using Avalonia;
using UltraLogViewer.Core;

namespace UltraLogViewer.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // One window per user: a later launch (or a clicked ultralogviewer:// link) goes to the running one. Not when
        // reading standard input, which only this process has.
        if (!args.Contains("-") && SingleInstance.TrySend(SingleInstance.DefaultName, new SingleInstance.Handoff(Environment.CurrentDirectory, args).ToJson())) return;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
