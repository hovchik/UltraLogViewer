using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using UltraLogViewer.Core;

namespace UltraLogViewer.Desktop;

/// <summary>The receiver or event log behind a capture tab, at the top of the Sources list: what it listens on, its live counts and Stop.</summary>
public sealed partial class Sidebar
{
    private TextBlock? _captureInfo;
    private Button? _captureStop;

    private Control CaptureBox(DocumentTab tab, ILiveCapture capture)
    {
        _captureInfo = new TextBlock { Text = capture.Status, TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 12, Name = "capture-status" };
        _captureStop = new Button { Content = "Stop", Classes = { "mini" }, IsEnabled = capture.Running, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 0, 0, 0) };
        _captureStop.Click += (_, _) => _host.StopCapture(tab);
        var dp = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(_captureStop, Dock.Right);
        dp.Children.Add(_captureStop);
        dp.Children.Add(_captureInfo);
        return dp;
    }

    public void UpdateCapture(DocumentTab tab)
    {
        if (tab != _tab || tab.Capture is not { } c || _captureInfo is null) return;
        _captureInfo.Text = c.Status;
        if (_captureStop is not null) _captureStop.IsEnabled = c.Running;
    }

    internal string CaptureStatusForTest => _captureInfo?.Text ?? "";
    internal Button? CaptureStopForTest => _captureStop;
}
