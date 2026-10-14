using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace UltraLogViewer.Desktop;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var files = (desktop.Args ?? []).Where(File.Exists).Select(Path.GetFullPath).ToArray();
            desktop.MainWindow = new MainWindow(AppSettings.Load(), files);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
