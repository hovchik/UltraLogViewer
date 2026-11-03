using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using UltraLogViewer.Core;

namespace UltraLogViewer.Desktop.Tests;

/// <summary>File › Listen…: the dialog, a live TCP receiver tab with its counts and Stop, and closing the tab freeing the port; the event log tab with a fake wevtutil.</summary>
public sealed class ReceiverUiTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ulv-rcv-ui-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private MainWindow NewWindow()
    {
        var w = new MainWindow(new AppSettings { FilePath = Path.Combine(_dir, "settings.json") }, []) { Width = 1200, Height = 800 };
        w.Show();
        return w;
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

    private static async Task<Window> Dialog(MainWindow w, string title)
    {
        await Until(() => w.OwnedWindows.Any(o => o.Title == title));
        return w.OwnedWindows.Single(o => o.Title == title);
    }

    private static T Named<T>(Window d, string name) where T : Control => d.GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);

    private static void Click(Window dialog, string content) =>
        dialog.GetLogicalDescendants().OfType<Button>().Single(b => b.Content as string == content).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private static bool PortFree(int port)
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            s.Bind(new IPEndPoint(IPAddress.Loopback, port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    [AvaloniaFact]
    public async Task Listen_opens_a_live_tab_that_shows_received_lines_and_closing_it_frees_the_port()
    {
        var w = NewWindow();
        var listen = w.PaletteActions().Single(a => a.Label == "File › Listen…");
        Assert.Equal(OperatingSystem.IsWindows(), w.PaletteActions().Any(a => a.Label == "File › Open Windows event log…"));
        listen.Item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        var d = await Dialog(w, MainWindow.ListenTitle);
        d.GetLogicalDescendants().OfType<ComboBox>().First().SelectedIndex = 2; // text lines over TCP
        var port = FreePort();
        Named<TextBox>(d, "receiver-port").Text = port.ToString();
        var bind = Named<TextBox>(d, "receiver-bind");
        Assert.False(Named<TextBlock>(d, "receiver-warning").IsVisible);
        bind.Text = "0.0.0.0";
        Dispatcher.UIThread.RunJobs();
        Assert.True(Named<TextBlock>(d, "receiver-warning").IsVisible);
        bind.Text = "127.0.0.1";
        Dispatcher.UIThread.RunJobs();
        Assert.False(Named<TextBlock>(d, "receiver-warning").IsVisible);
        Click(d, "Listen");
        await Until(() => w.ActiveTab is { Ready: true, Capture: not null });
        var tab = w.ActiveTab!;
        Assert.StartsWith(Path.Combine(_dir, "receivers"), tab.Paths[0]);
        await Until(() => w.CaptureStatusForTest.StartsWith($"Listening on tcp://127.0.0.1:{port}"));
        Assert.False(PortFree(port));

        using (var c = new TcpClient())
        {
            await c.ConnectAsync(IPAddress.Loopback, port);
            await c.GetStream().WriteAsync(Encoding.UTF8.GetBytes(
                "{\"@t\":\"2024-05-01T10:00:00Z\",\"@l\":\"Error\",\"@m\":\"payment failed\"}\n{\"@t\":\"2024-05-01T10:00:01Z\",\"@m\":\"retrying\"}\n"));
            await Until(() => w.CaptureStatusForTest.Contains("2 messages · 1 client"));
        }
        // the empty capture was taken for plain text; with lines in it the format is detected again
        await Until(() => tab.Info.ViewCount == 2 && tab.Info.Sources[0].FormatId == "json", 15_000);
        Assert.Equal("payment failed", tab.Doc.GetRows(0, 1)[0].Fields["message"]);

        Assert.True(w.CaptureStopForTest!.IsEnabled);
        w.CaptureStopForTest.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.StartsWith("Stopped", w.CaptureStatusForTest);
        Assert.True(PortFree(port));

        // a new receiver on the same port, closed with its tab
        var again = await w.StartReceiver(new ReceiverOptions(ReceiverProtocol.SyslogTcp, Port: port));
        Assert.NotNull(again);
        Assert.Equal("syslog5424", again!.FormatId);
        Assert.False(PortFree(port));
        w.CloseTab(again);
        Assert.True(PortFree(port));
        // a port in use is reported, and no tab is left behind
        using var busy = new TcpListener(IPAddress.Loopback, port);
        busy.Start();
        var tabs = w.Tabs.Count;
        Assert.Null(await w.StartReceiver(new ReceiverOptions(ReceiverProtocol.TcpLines, Port: port)));
        Assert.Contains("already in use", w.ToastForTest);
        Assert.Equal(tabs, w.Tabs.Count);
    }

    private sealed class FakeWevtutil(string xml) : IWevtutil
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<(int Code, string Error)> RunAsync(IReadOnlyList<string> args, Action<string> output, CancellationToken ct)
        {
            Calls.Add(args);
            if (Calls.Count == 1) output(xml);
            return Task.FromResult((0, ""));
        }
    }

    [AvaloniaFact]
    public async Task Event_log_export_opens_with_the_windows_event_format()
    {
        var w = NewWindow();
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir!.FullName, "test", "fixtures", "winevents.xml"))) dir = dir.Parent;
        var fake = new FakeWevtutil(File.ReadAllText(Path.Combine(dir.FullName, "test", "fixtures", "winevents.xml")));
        w.Wevtutil = fake;
        var tab = await w.StartEventLog("System", null, null, follow: false);
        Assert.NotNull(tab);
        await Until(() => tab!.Info.ViewCount == 5);
        Assert.Equal("winevent", tab!.Info.Sources[0].FormatId);
        Assert.Contains(tab.Info.Columns, c => c.Key == "eventId");
        Assert.Equal("1000", tab.Doc.GetRows(0, 1)[0].Fields["eventId"]);
        await Until(() => w.CaptureStatusForTest.Contains("5 events · done"));
        Assert.Equal(["qe", "System", "/f:RenderedXml", "/rd:false", "/q:*"], fake.Calls[0]);
    }
}
