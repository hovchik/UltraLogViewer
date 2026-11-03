using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using UltraLogViewer.Core;

namespace UltraLogViewer.Desktop;

/// <summary>
/// Open source location: stack-frame references (Foo.java:42, File.cs:line 42, file.ts:10:5, File "x.py", line 3) in
/// the details pane and in expanded rows open in the configured editor (VS Code, Rider, IntelliJ or a custom command),
/// after resolving relative paths under the source roots.
/// </summary>
public sealed partial class MainWindow
{
    private readonly WrapPanel _detailLinks = new() { Margin = new Thickness(8, 0, 8, 4), IsVisible = false };
    private List<SourceRef> _detailRefs = [];

    /// <summary>Test hook: receives what would be launched (a URL, or a program and its arguments) instead of launching it.</summary>
    internal Action<string, IReadOnlyList<string>>? LaunchForTest { get; set; }

    public void OpenSource(SourceRef r)
    {
        var name = $"{Path.GetFileName(r.Path.Replace('\\', '/'))}:{r.Line}";
        if (SourceLinks.Resolve(r, _settings.SourceRoots) is not { } file)
        {
            Toast(_settings.SourceRoots.Count == 0
                ? $"{name} is not on this computer as written. {SourceLinks.NoMatchHint}"
                : $"{name} was not found under your source roots. {SourceLinks.NoMatchHint}", true);
            return;
        }
        var template = SourceLinks.TemplateFor(_settings.Editor, _settings.EditorCommand);
        if (string.IsNullOrWhiteSpace(template))
        {
            Toast("Set the editor command under Edit › Editor and source roots…", true);
            return;
        }
        try
        {
            if (SourceLinks.IsUrl(template))
            {
                var url = SourceLinks.ExpandUrl(template, file, r.Line, r.Column);
                if (LaunchForTest is { } hook) hook(url, []);
                else Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            else
            {
                var (program, args) = SourceLinks.ExpandCommand(template, file, r.Line, r.Column);
                if (LaunchForTest is { } hook) hook(program, args);
                else
                {
                    var psi = new ProcessStartInfo(program) { UseShellExecute = false };
                    foreach (var a in args) psi.ArgumentList.Add(a);
                    Process.Start(psi);
                }
            }
            Toast($"Opening {name}");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Toast($"Could not start the editor: {e.Message}", true);
        }
    }

    /// <summary>The source locations of the entry in the details pane, as links under its fields.</summary>
    private void RenderDetailLinks(string text)
    {
        _detailRefs = SourceLinks.Find(text);
        _detailLinks.Children.Clear();
        var shown = _detailRefs.DistinctBy(r => (r.Path, r.Line)).Take(12).ToList();
        _detailLinks.IsVisible = shown.Count > 0;
        if (shown.Count == 0) return;
        var p = Palette.Current;
        _detailLinks.Children.Add(new TextBlock { Text = "Open in editor:", FontSize = 12, Foreground = p.Fg2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        foreach (var r in shown)
        {
            var label = new TextBlock { Text = $"{Path.GetFileName(r.Path.Replace('\\', '/'))}:{r.Line}", FontFamily = Fonts.Mono, FontSize = 12, Foreground = p.Accent, TextDecorations = TextDecorations.Underline };
            var link = new Button { Content = label, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(4, 1), MinHeight = 0, Cursor = new Cursor(StandardCursorType.Hand) };
            ToolTip.SetTip(link, r.Path + (r.Package is { } pkg ? $" (package {pkg})" : ""));
            link.Click += (_, _) => OpenSource(r);
            _detailLinks.Children.Add(link);
        }
    }

    /// <summary>Ctrl+click on a source location inside the entry text of the details pane opens it too.</summary>
    private void WireDetailLinks()
    {
        _detail.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (!(e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)) || e.InitialPressMouseButton != MouseButton.Left) return;
            var at = _detail.CaretIndex;
            if (_detailRefs.FirstOrDefault(r => at >= r.Start && at <= r.Start + r.Length) is { } hit) OpenSource(hit);
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private async Task EditSourceSettings()
    {
        var editors = SourceLinks.Editors;
        var pick = new ComboBox { ItemsSource = editors.Select(e => e.Label).ToList(), SelectedIndex = Math.Max(0, Array.FindIndex(editors, e => e.Id == _settings.Editor)), MinWidth = 200 };
        var command = new TextBox { Text = _settings.EditorCommand, Watermark = "code --goto \"{path}:{line}:{col}\"  or  subl://open?url=file://{path}&line={line}", FontFamily = Fonts.Mono, FontSize = 12 };
        void SyncCommand() => command.IsEnabled = editors[Math.Max(0, pick.SelectedIndex)].Id == "custom";
        pick.SelectionChanged += (_, _) => SyncCommand();
        SyncCommand();
        var roots = new TextBox { Text = string.Join("\n", _settings.SourceRoots), AcceptsReturn = true, Height = 110, FontFamily = Fonts.Mono, FontSize = 12, Watermark = "One folder per line, e.g. C:\\src\\shop" };
        var add = new Button { Content = "Add folder…" };
        add.Click += async (_, _) =>
        {
            var dirs = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Source root", AllowMultiple = true });
            var more = dirs.Select(d => d.TryGetLocalPath()).OfType<string>().ToList();
            if (more.Count > 0) roots.Text = string.Join("\n", (roots.Text ?? "").Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).Concat(more));
        };
        var ok = new Button { Content = "Save", IsDefault = true, Classes = { "accent" } };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var hint = (string s) => new TextBlock { Text = s, Classes = { "hint" }, TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "Open source locations in" },
                pick,
                hint("Custom: a URL or a command line; {path}, {line} and {col} are filled in."),
                command,
                new TextBlock { Text = "Source roots", Margin = new Thickness(0, 8, 0, 0) },
                hint("Relative paths in stack traces (and paths from the build machine) are looked up under these folders, in order; the first file that exists wins. For Java, add the folder that holds the packages (src/main/java)."),
                roots,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { add } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0), Children = { ok, cancel } },
            },
        };
        var w = new Window
        {
            Title = "Editor and source roots",
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Border { Padding = new Thickness(18, 14), Child = panel },
        };
        ok.Click += (_, _) => w.Close(true);
        cancel.Click += (_, _) => w.Close(false);
        if (!await w.ShowDialog<bool>(this)) return;
        _settings.Editor = editors[Math.Max(0, pick.SelectedIndex)].Id;
        _settings.EditorCommand = command.Text?.Trim() ?? "";
        _settings.SourceRoots = (roots.Text ?? "").Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).Distinct().ToList();
        _settings.Save();
        Toast(_settings.SourceRoots.Count == 0 ? "Saved. Without source roots only absolute paths open." : $"Saved {_settings.SourceRoots.Count} source root(s)");
    }

    internal IReadOnlyList<string> DetailLinksForTest => _detailLinks.IsVisible ? _detailLinks.Children.OfType<Button>().Select(b => ((TextBlock)b.Content!).Text ?? "").ToList() : [];
    internal void ClickDetailLinkForTest(int i) => _detailLinks.Children.OfType<Button>().ElementAt(i).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
}
