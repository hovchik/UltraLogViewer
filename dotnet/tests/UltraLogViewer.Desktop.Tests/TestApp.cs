using Avalonia;
using Avalonia.Headless;
using UltraLogViewer.Desktop;

[assembly: AvaloniaTestApplication(typeof(UltraLogViewer.Desktop.Tests.TestApp))]

namespace UltraLogViewer.Desktop.Tests;

public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
