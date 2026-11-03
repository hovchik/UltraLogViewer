using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using UltraLogViewer.Core;

namespace UltraLogViewer.Desktop;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // UltraLogViewer [files] [-] [--since -1h] [--until 2024-05-01T11:00] [--last 30m]
            var cli = CommandLine.Parse(desktop.Args ?? [], DateTimeOffset.Now.ToUnixTimeMilliseconds());
            var files = cli.Files.Where(File.Exists).Select(Path.GetFullPath).ToArray();
            desktop.MainWindow = new MainWindow(AppSettings.Load(), files, cli.Window, cli.Stdin, cli.Errors);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
