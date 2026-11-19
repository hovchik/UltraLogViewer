using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;

namespace UltraLogViewer.Desktop.Tests;

public sealed class AboutUiTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ulv-about-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static async Task Until(Func<bool> cond, int timeoutMs = 10_000)
    {
        var start = Environment.TickCount64;
        while (!cond())
        {
            if (Environment.TickCount64 - start > timeoutMs) throw new TimeoutException("Condition not met in time");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(15);
        }
        Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public void App_info_comes_from_the_assembly_attributes()
    {
        Assert.Equal("UltraLogViewer", AppInfo.Product);
        Assert.Matches(@"^\d+\.\d+\.\d+", AppInfo.Version);
        Assert.DoesNotContain("+", AppInfo.Version);
        Assert.Equal(typeof(AppInfo).Assembly.GetName().Version?.ToString(3), AppInfo.Version.Split('-')[0]);
        Assert.Contains("Hovhannes Stepanyan", AppInfo.Copyright);
        Assert.Matches(@"© \d{4}", AppInfo.Copyright);
        Assert.Contains("log viewer", AppInfo.Description);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", AppInfo.BuildDate);
    }

    [AvaloniaFact]
    public async Task Help_about_shows_version_copyright_and_description()
    {
        var settings = new AppSettings { FilePath = Path.Combine(_dir, "settings.json") };
        var w = new MainWindow(settings, []) { Width = 1000, Height = 700 };
        w.Show();
        var launched = new List<string>();
        w.LaunchForTest = (target, _) => launched.Add(target);

        var about = w.PaletteActions().Single(a => a.Label == "Help › About UltraLogViewer");
        about.Item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Until(() => w.OwnedWindows.Any(o => o.Title == "About UltraLogViewer"));
        var dialog = w.OwnedWindows.Single(o => o.Title == "About UltraLogViewer");

        var text = string.Join("\n", dialog.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text));
        Assert.Contains($"Version {AppInfo.Version}", text);
        Assert.Contains("Copyright © 2026 Hovhannes Stepanyan", text);
        Assert.Contains(AppInfo.Description, text);
        Assert.Contains($"Built {AppInfo.BuildDate}", text);

        if (Environment.GetEnvironmentVariable("ULV_SCREENSHOTS") is { } dir)
        {
            Directory.CreateDirectory(dir);
            for (var i = 0; i < 3; i++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }
            dialog.CaptureRenderedFrame()?.Save(Path.Combine(dir, "about.png"));
        }

        Button(dialog, "Project on GitHub").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Assert.Equal([AppInfo.Homepage], launched);
        Button(dialog, "Close").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        await Until(() => w.OwnedWindows.Count == 0);
    }

    private static Button Button(Window dialog, string content) =>
        dialog.GetLogicalDescendants().OfType<Button>().Single(b => b.Content as string == content);
}
