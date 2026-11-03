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
            // UltraLogViewer [files|globs] [-] [--since -1h] [--until 2024-05-01T11:00] [--last 30m] [--filter q] [--search text]
            //   [--goto time|line] [--levels error,warn] [--format id] [--merge] [--new-tab] | ultralogviewer://open?…
            var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            var request = OpenRequest.FromCommandLine(CommandLine.Parse(desktop.Args ?? [], now), now);
            var window = new MainWindow(AppSettings.Load(), []);
            window.StartWith(request, Environment.CurrentDirectory);
            desktop.MainWindow = window;
            // macOS hands a clicked link to the running app as an event (Windows and Linux on the command line)
            if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
                activatable.Activated += (_, e) =>
                {
                    if (e is ProtocolActivatedEventArgs { Kind: ActivationKind.OpenUri } p) window.OpenLink(p.Uri.OriginalString);
                };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
