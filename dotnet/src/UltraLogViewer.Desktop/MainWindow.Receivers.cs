using System.Globalization;
using System.Net.Sockets;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using UltraLogViewer.Core;

namespace UltraLogViewer.Desktop;

/// <summary>What the Listen… dialog remembers between uses.</summary>
public sealed class ReceiverSettings
{
    public ReceiverProtocol Protocol { get; set; } = ReceiverProtocol.SyslogUdp;
    public string Bind { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 5514;
    public int MaxMessageKb { get; set; } = 64;
    /// <summary>Size at which a capture file is rotated, in MB.</summary>
    public long CapMb { get; set; } = 1024;
}

/// <summary>
/// Network receivers and the Windows event log. Each one writes to a capture file in the receivers folder that is
/// opened as a live tab; the tab owns it, so closing the tab stops the listener and frees the port.
/// </summary>
public sealed partial class MainWindow
{
    private DispatcherTimer? _captureTimer;

    /// <summary>Runs wevtutil; tests put a fake here.</summary>
    internal IWevtutil Wevtutil { get; set; } = new WevtutilProcess();

    internal string CaptureStatusForTest => _sidebar.CaptureStatusForTest;
    internal Button? CaptureStopForTest => _sidebar.CaptureStopForTest;

    private static MenuItem WindowsOnly(MenuItem m)
    {
        m.IsVisible = m.IsEnabled = OperatingSystem.IsWindows();
        return m;
    }

    // ============================================================ Listen…

    internal const string ListenTitle = "Listen for logs";

    internal async Task ListenAsync()
    {
        var o = await AskReceiver();
        if (o is not null) await StartReceiver(o);
    }

    /// <summary>Starts a receiver and opens its capture as a live tab. Returns the tab, or null when it could not listen.</summary>
    internal async Task<DocumentTab?> StartReceiver(ReceiverOptions o)
    {
        LogReceiver r;
        try
        {
            r = LogReceiver.Start(o, _settings.ReceiversDir);
        }
        catch (Exception e) when (e is SocketException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            var why = e is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse } ? "the port is already in use" : e.Message;
            Toast($"Could not listen on {o.Scheme}://{o.Bind}:{o.Port}: {why}", true);
            return null;
        }
        // Syslog is always written as RFC 5424 and XML events as JSON; plain lines are detected once the first arrive.
        var format = o.Protocol switch { ReceiverProtocol.SyslogUdp or ReceiverProtocol.SyslogTcp => "syslog5424", ReceiverProtocol.Log4jXml => "json", _ => "" };
        var tab = await OpenCapture(r, format);
        if (tab is not null) Toast($"Listening on {r.Endpoint}");
        return tab;
    }

    private async Task<DocumentTab?> OpenCapture(ILiveCapture capture, string format)
    {
        var tab = await OpenTab([capture.Path], "auto", null, t =>
        {
            t.Capture = capture;
            t.Follow = true;
            t.FormatId = format;
            t.DetectOnData = format.Length == 0;
        });
        if (tab is null) capture.Dispose();
        else EnsureCaptureTimer();
        return tab;
    }

    private void EnsureCaptureTimer()
    {
        if (_captureTimer is not null) return;
        _captureTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _captureTimer.Tick += (_, _) => CaptureTick();
        _captureTimer.Start();
    }

    /// <summary>Once a second: the live counts in the Sources list, and format detection once lines arrived.</summary>
    private void CaptureTick()
    {
        if (_active is { Capture: not null } a) _sidebar.UpdateCapture(a);
        foreach (var t in _tabs)
        {
            if (!t.DetectOnData || !t.Ready || t.BusyProgress is not null || t.Info.Sources.Count == 0 || t.Info.Sources[0].Entries == 0) continue;
            t.DetectOnData = false;
            // The file was empty when it was opened, so it was taken for plain text: look again now that there is data.
            if (t.FormatId.Length == 0 && t.Info.Sources[0].FormatId == "plain") Run(() => Reformat(t, null));
        }
    }

    internal void StopCapture(DocumentTab tab)
    {
        if (tab.Capture is not { Running: true } c) return;
        c.Stop();
        _sidebar.UpdateCapture(tab);
        Toast("Stopped: the capture stays open");
    }

    private async Task<ReceiverOptions?> AskReceiver()
    {
        var s = _settings.Receiver;
        var protocol = new ComboBox { ItemsSource = ReceiverOptions.Protocols.Select(p => p.Label).ToList(), SelectedIndex = Math.Max(0, Array.FindIndex(ReceiverOptions.Protocols, p => p.P == s.Protocol)), HorizontalAlignment = HorizontalAlignment.Stretch };
        var port = new TextBox { Text = s.Port.ToString(CultureInfo.InvariantCulture), Name = "receiver-port" };
        var bind = new TextBox { Text = s.Bind, Name = "receiver-bind" };
        var max = new TextBox { Text = s.MaxMessageKb.ToString(CultureInfo.InvariantCulture), Name = "receiver-max" };
        var cap = new TextBox { Text = s.CapMb.ToString(CultureInfo.InvariantCulture), Name = "receiver-cap" };
        var warning = new TextBlock
        {
            Name = "receiver-warning", TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#E8590C")),
            Text = "⚠ Not a loopback address: other computers can send to this port. Use it only on a network you trust, and check your firewall.",
        };
        void SyncWarning() => warning.IsVisible = !LogReceiver.IsLoopback(bind.Text ?? "");
        bind.TextChanged += (_, _) => SyncWarning();
        SyncWarning();
        var error = new TextBlock { Foreground = Brushes.IndianRed, FontSize = 12, IsVisible = false, TextWrapping = TextWrapping.Wrap };
        var ok = new Button { Content = "Listen", IsDefault = true, Classes = { "accent" } };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        static Control Row(string label, Control c) => new DockPanel
        {
            Children = { new TextBlock { Text = label, Width = 150, VerticalAlignment = VerticalAlignment.Center, [DockPanel.DockProperty] = Dock.Left }, c },
        };
        var panel = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Row("Protocol", protocol), Row("Port", port), Row("Bind address", bind), warning,
                Row("Max message (KB)", max), Row("Rotate capture at (MB)", cap),
                new TextBlock
                {
                    Classes = { "hint" }, TextWrapping = TextWrapping.Wrap, FontSize = 12,
                    Text = $"What arrives is written to a capture file in {_settings.ReceiversDir} (readable only by you) and opened as a live tab. Closing the tab stops listening.",
                },
                error, Dialogs.Buttons(ok, cancel),
            },
        };
        var w = Dialogs.Shell(ListenTitle, panel, 520);
        ok.Click += (_, _) =>
        {
            string? Check()
            {
                if (!int.TryParse(port.Text, out var p) || p is < 1 or > 65535) return "Enter a port from 1 to 65535";
                if (LogReceiver.ParseBind(bind.Text ?? "") is null) return "Enter an IP address to listen on, such as 127.0.0.1 (this computer only) or 0.0.0.0 (all networks)";
                if (!int.TryParse(max.Text, out var m) || m is < 1 or > 16384) return "Enter a maximum message size from 1 to 16384 KB";
                if (!long.TryParse(cap.Text, out var c) || c < 1) return "Enter the size at which to rotate the capture, in MB";
                return null;
            }
            if (Check() is { } msg)
            {
                error.Text = msg;
                error.IsVisible = true;
                return;
            }
            s.Protocol = ReceiverOptions.Protocols[Math.Max(0, protocol.SelectedIndex)].P;
            s.Port = int.Parse(port.Text!, CultureInfo.InvariantCulture);
            s.Bind = bind.Text!.Trim();
            s.MaxMessageKb = int.Parse(max.Text!, CultureInfo.InvariantCulture);
            s.CapMb = long.Parse(cap.Text!, CultureInfo.InvariantCulture);
            _settings.Save();
            w.Close(new ReceiverOptions(s.Protocol, s.Bind, s.Port, s.MaxMessageKb * 1024, s.CapMb * 1024 * 1024));
        };
        cancel.Click += (_, _) => w.Close(null);
        return await w.ShowDialog<ReceiverOptions?>(this);
    }

    // ================================================== Windows event log

    internal const string EventLogTitle = "Open Windows event log";

    internal async Task OpenEventLogAsync()
    {
        var channels = WinEventLog.CommonChannels.Append("Other channel…").ToList();
        var pick = new ComboBox { ItemsSource = channels, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var other = new TextBox { Watermark = "Microsoft-Windows-PowerShell/Operational", IsEnabled = false, Name = "eventlog-channel" };
        pick.SelectionChanged += (_, _) => other.IsEnabled = pick.SelectedIndex == channels.Count - 1;
        var from = new TextBox { Text = "-1h", Watermark = "2024-05-01 10:00, 10:30, -2h; empty = everything", Name = "eventlog-from" };
        var until = new TextBox { Watermark = "empty = now", Name = "eventlog-until" };
        var follow = new CheckBox { Content = "Keep following new events", IsChecked = true };
        until.TextChanged += (_, _) => follow.IsEnabled = string.IsNullOrWhiteSpace(until.Text);
        var error = new TextBlock { Foreground = Brushes.IndianRed, FontSize = 12, IsVisible = false, TextWrapping = TextWrapping.Wrap };
        var ok = new Button { Content = "Open", IsDefault = true, Classes = { "accent" } };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        static Control Row(string label, Control c) => new DockPanel
        {
            Children = { new TextBlock { Text = label, Width = 110, VerticalAlignment = VerticalAlignment.Center, [DockPanel.DockProperty] = Dock.Left }, c },
        };
        var panel = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Row("Channel", pick), Row("", other), Row("From", from), Row("Until", until), follow,
                new TextBlock
                {
                    Classes = { "hint" }, TextWrapping = TextWrapping.Wrap, FontSize = 12,
                    Text = "Events are exported with wevtutil to a capture file (one JSON line each) and opened as a tab. The Security log needs an administrator.",
                },
                error, Dialogs.Buttons(ok, cancel),
            },
        };
        var w = Dialogs.Shell(EventLogTitle, panel, 500);
        ok.Click += (_, _) =>
        {
            var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            var channel = pick.SelectedIndex == channels.Count - 1 ? (other.Text ?? "").Trim() : channels[pick.SelectedIndex];
            long? Point(TextBox b) => string.IsNullOrWhiteSpace(b.Text) ? null : TimeWindow.ParsePoint(b.Text, now);
            string? msg = !WinEventLog.ValidChannel(channel) ? "Enter a channel name, such as Application or Microsoft-Windows-PowerShell/Operational"
                : !string.IsNullOrWhiteSpace(from.Text) && Point(from) is null ? "From: enter a time such as 2024-05-01 10:00, 10:30 or -2h"
                : !string.IsNullOrWhiteSpace(until.Text) && Point(until) is null ? "Until: enter a time such as 2024-05-01 11:00"
                : null;
            if (msg is not null)
            {
                error.Text = msg;
                error.IsVisible = true;
                return;
            }
            w.Close((channel, Point(from), Point(until), follow.IsChecked == true && string.IsNullOrWhiteSpace(until.Text)));
        };
        cancel.Click += (_, _) => w.Close(null);
        var r = await w.ShowDialog<(string Channel, long? From, long? Until, bool Follow)?>(this);
        if (r is { } v) await StartEventLog(v.Channel, v.From, v.Until, v.Follow);
    }

    internal async Task<DocumentTab?> StartEventLog(string channel, long? from, long? until, bool follow)
    {
        WinEventLog log;
        try
        {
            log = WinEventLog.Start(Wevtutil, channel, from, until, follow, _settings.ReceiversDir, _settings.Receiver.CapMb * 1024 * 1024);
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            Toast($"Could not read the {channel} log: {e.Message}", true);
            return null;
        }
        var tab = await OpenCapture(log, "winevent");
        if (tab is null) return null;
        var error = await log.FirstExport;
        if (error is not null) Toast($"Could not read the {channel} log: {error}", true);
        if (IsOpen(tab)) _sidebar.UpdateCapture(tab);
        return tab;
    }
}
