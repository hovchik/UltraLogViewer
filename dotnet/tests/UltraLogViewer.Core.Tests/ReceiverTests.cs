using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using UltraLogViewer.Core;
using Xunit;

namespace UltraLogViewer.Core.Tests;

public class SyslogParseTests
{
    [Fact]
    public void ParsesRfc5424WithStructuredDataEscapesAndBom()
    {
        var m = Syslog.Parse("<165>1 2003-10-11T22:14:15.003Z mymachine.example.com evntslog - ID47 [exampleSDID@32473 iut=\"3\" eventSource=\"Appli\\\"cation\\]\"][examplePriority@32473 class=\"high\"] ﻿An application event");
        Assert.Equal((5424, 165, 20, 5), (m.Rfc, m.Pri, m.Facility, m.Severity));
        Assert.Equal(("2003-10-11T22:14:15.003Z", "mymachine.example.com", "evntslog", "", "ID47"), (m.Time, m.Host, m.App, m.Pid, m.MsgId));
        Assert.Equal("An application event", m.Message);
        Assert.Equal(["exampleSDID@32473.iut=3", "exampleSDID@32473.eventSource=Appli\"cation]", "examplePriority@32473.class=high"], m.Params.Select(p => $"{p.Key}={p.Value}"));
        Assert.StartsWith("[exampleSDID@32473 ", m.StructuredData);

        var nil = Syslog.Parse("<34>1 - - - - - -");
        Assert.Equal((5424, "", "", ""), (nil.Rfc, nil.Time, nil.Host, nil.Message));
        // Malformed structured data: the rest is the message.
        var bad = Syslog.Parse("<14>1 2024-05-01T10:00:00Z h app 1 - [broken message");
        Assert.Equal((5424, "", "[broken message"), (bad.Rfc, bad.StructuredData, bad.Message));
    }

    [Fact]
    public void ParsesRfc3164AndWhatDevicesReallySend()
    {
        var m = Syslog.Parse("<34>Oct 11 22:14:15 mymachine su[230]: 'su root' failed for lonvick on /dev/pts/8");
        Assert.Equal((3164, 34, "Oct 11 22:14:15", "mymachine", "su", "230"), (m.Rfc, m.Pri, m.Time, m.Host, m.App, m.Pid));
        Assert.Equal("'su root' failed for lonvick on /dev/pts/8", m.Message);
        var noHost = Syslog.Parse("<13>Feb  5 17:32:18 sshd: Accepted publickey");
        Assert.Equal(("", "sshd", "Accepted publickey"), (noHost.Host, noHost.App, noHost.Message));
        var noTag = Syslog.Parse("<13>Feb  5 17:32:18 10.0.0.9 Use the BFG!");
        Assert.Equal(("10.0.0.9", "", "Use the BFG!"), (noTag.Host, noTag.App, noTag.Message));
        var iso = Syslog.Parse("<30>2024-05-01T10:00:00.123+02:00 web nginx[77]: started");
        Assert.Equal((3164, "2024-05-01T10:00:00.123+02:00", "web", "nginx"), (iso.Rfc, iso.Time, iso.Host, iso.App));
        var bare = Syslog.Parse("<189>123: *Mar  1 00:00:00.000: %SYS-5-CONFIG_I: Configured");
        Assert.Equal((0, 189, "123: *Mar  1 00:00:00.000: %SYS-5-CONFIG_I: Configured"), (bare.Rfc, bare.Pri, bare.Message));
        Assert.Equal(-1, Syslog.Parse("<192>Oct 11 22:14:15 h a: x").Pri);
        Assert.Equal(-1, Syslog.Parse("<013>x").Pri);
        Assert.Equal((-1, "no pri at all"), (Syslog.Parse("no pri at all").Pri, Syslog.Parse("no pri at all").Message));
        foreach (var junk in new[] { "", "<", "<1", "<>", "<1>", "<1>1", "<1>1 ", "<1>1 a b c d e [x", "<1>1 a b c d e [x y=\"", "\0\0\0" })
            _ = Syslog.Parse(junk); // never throws
    }

    [Fact]
    public void NormalisesEveryMessageToOneRfc5424Line()
    {
        var at = new DateTimeOffset(2024, 5, 1, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal("<165>1 2003-10-11T22:14:15.003Z mymachine evntslog - ID47 [a@1 b=\"c\"] hi",
            Syslog.ToLine("<165>1 2003-10-11T22:14:15.003Z mymachine evntslog - ID47 [a@1 b=\"c\"] hi", at, "10.0.0.1"));
        var bsd = Syslog.ToLine("<34>Oct 11 22:14:15 mymachine su[230]: failed\n", at, "10.0.0.1");
        Assert.Matches(@"^<34>1 2023-10-11T22:14:15\.000[+-]\d{2}:\d{2} mymachine su 230 - - failed $", bsd); // October is in the future in May: last year
        var none = Syslog.ToLine("just text\twith a tab\0and a NUL", at, "10.0.0.1");
        Assert.Equal("<13>1 " + Syslog.Iso(at) + " 10.0.0.1 - - - - just text\twith a tab" + "and a NUL", none);
        Assert.Matches(@"^<14>1 \S+ 10\.0\.0\.2 - - - - hello$", Syslog.ToLine("<14>hello", at, "10.0.0.2"));
        // the line it writes is read back by the RFC 5424 format with the same fields
        var f = FormatDetector.Detect([bsd]);
        Assert.Equal("syslog5424", f.Id);
        var p = f.Parse(bsd);
        Assert.Equal(("mymachine", "su", "230", "FATAL", "auth", "crit"), (p["host"], p["app"], p["pid"], p["level"], p["facility"], p["severity"]));
    }

    [Fact]
    public void CleansControlCharactersButKeepsTabs()
    {
        Assert.Equal("a b\tc d", Sanitize.Line("a\r\nb\tc\u0007\u001b\u009b\nd"));
        Assert.Equal("plain", Sanitize.Line("plain"));
    }

    [Fact]
    public void DetectsSyslogWithPriAndShowsFacilitySeverityAndStructuredData()
    {
        var f = FormatDetector.Detect(["<38>Oct  3 04:05:06 myhost sshd[123]: Accepted password", "<35>Oct  3 04:05:07 myhost sshd[123]: Failed password"], 2024);
        Assert.Equal("syslog", f.Id);
        Assert.Equal(Level.Error, f.Quick("<35>Oct  3 04:05:07 myhost sshd[123]: Failed password").Level);
        var p = f.Parse("<35>Oct  3 04:05:07 myhost sshd[123]: Failed password");
        Assert.Equal(("myhost", "sshd", "auth", "err", "Failed password"), (p["host"], p["app"], p["facility"], p["severity"], p["message"]));
        var f5 = FormatDetector.Detect(["<165>1 2003-10-11T22:14:15.003Z mymachine evntslog - ID47 [exampleSDID@32473 iut=\"3\"] An application event"]);
        var p5 = f5.Parse("<165>1 2003-10-11T22:14:15.003Z mymachine evntslog - ID47 [exampleSDID@32473 iut=\"3\"] An application event");
        Assert.Equal(("local4", "notice", "3", "An application event", "ID47"), (p5["facility"], p5["severity"], p5["exampleSDID@32473.iut"], p5["message"], p5["msgid"]));
    }
}

public class FramingTests
{
    private static List<string> Frame(bool syslog, int max, params byte[][] chunks)
    {
        var f = new StreamFramer(syslog, max);
        var list = new List<string>();
        foreach (var c in chunks) f.Feed(c, list.Add);
        f.End(list.Add);
        return list;
    }

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void SplitsOctetCountingAndNewlineFramesEvenByteByByte()
    {
        var stream = "18 <13>1 - - - - - hi12 <13>oct\nmore<14>newline framed\r\n<15>another\n\n";
        var expected = new[] { "<13>1 - - - - - hi", "<13>oct\nmore", "<14>newline framed", "<15>another" };
        Assert.Equal(expected, Frame(true, 1024, B(stream)));
        Assert.Equal(expected, Frame(true, 1024, B(stream).Select(b => new[] { b }).ToArray()));
    }

    [Fact]
    public void CutsLongMessagesAndRecoversFromBadCounts()
    {
        var f = new StreamFramer(true, 256);
        var list = new List<string>();
        f.Feed(B("300 " + new string('a', 300) + "5 <1>ok"), list.Add);
        Assert.Equal([new string('a', 256), "<1>ok"], list);
        Assert.Equal(1, f.Truncated);
        // digits not followed by a space are a line after all; a huge count is cut and its bytes skipped
        Assert.Equal(["12abc", "<1>x"], Frame(true, 256, B("12abc\n<1>x\n")));
        Assert.Equal(["0123456789x"], Frame(true, 256, B("0123456789x\n")));
        var lines = Frame(false, 256, B(new string('b', 600) + "\nshort\n  indented\n"));
        Assert.Equal([new string('b', 256), "short", "  indented"], lines);
        Assert.Equal(["unterminated"], Frame(false, 256, B("unterminated")));
    }

    [Fact]
    public void GrowsItsBufferOnlyAsFarAsMessagesNeed()
    {
        var big = new string('c', 10_000);
        Assert.Equal([big, "d"], Frame(false, 20_000, B(big + "\nd\n")));
        Assert.Equal([big], Frame(true, 20_000, B("10000 " + big)));
        Assert.Equal([new string('c', 6000)], Frame(false, 6000, B(big + "\n")));
        // 64 idle connections with a 16 MB maximum must not hold 1 GB
        var before = GC.GetAllocatedBytesForCurrentThread();
        _ = new StreamFramer(false, 16 << 20);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 64 * 1024);
    }
}

public class Log4jXmlTests
{
    private const string Log4j =
        "<log4j:event logger=\"com.acme.Db\" timestamp=\"1714557600123\" level=\"ERROR\" thread=\"worker-1\">" +
        "<log4j:message><![CDATA[Query failed: <timeout>]]></log4j:message>" +
        "<log4j:NDC><![CDATA[req-7]]></log4j:NDC>" +
        "<log4j:throwable><![CDATA[java.sql.SQLException: timeout\n\tat com.acme.Db.query(Db.java:42)\n]]></log4j:throwable>" +
        "<log4j:locationInfo class=\"com.acme.Db\" method=\"query\" file=\"Db.java\" line=\"42\"/>" +
        "<log4j:properties><log4j:data name=\"user\" value=\"bob\"/><log4j:data name=\"tenant\" value=\"t1\"/></log4j:properties>" +
        "</log4j:event>\r\n";

    private const string Log4net =
        "<log4net:event logger=\"Shop.Checkout\" timestamp=\"2024-05-01T10:00:01.5+02:00\" level=\"WARN\" thread=\"7\" domain=\"Shop.exe\" username=\"CORP\\bob\">" +
        "<log4net:message>Slow payment &amp; retry</log4net:message>" +
        "<log4net:properties><log4net:data name=\"log4net:HostName\" value=\"web1\" /></log4net:properties>" +
        "<log4net:exception>System.TimeoutException: late</log4net:exception></log4net:event>";

    [Fact]
    public void ConvertsLog4jAndLog4netEventsToJsonLines()
    {
        var j = JsonDocument.Parse(Log4jXml.ToJson(Log4j.Trim())!).RootElement;
        Assert.Equal(("ERROR", "com.acme.Db", "worker-1", "Query failed: <timeout>"), (j.GetProperty("level").GetString(), j.GetProperty("logger").GetString(), j.GetProperty("thread").GetString(), j.GetProperty("message").GetString()));
        Assert.Equal(1714557600123, Timestamps.Parse(j.GetProperty("time").GetString()));
        Assert.Equal("java.sql.SQLException: timeout\n\tat com.acme.Db.query(Db.java:42)", j.GetProperty("exception").GetString());
        Assert.Equal(("req-7", "query", "42", "bob"), (j.GetProperty("ndc").GetString(), j.GetProperty("method").GetString(), j.GetProperty("line").GetString(), j.GetProperty("properties").GetProperty("user").GetString()));

        var n = JsonDocument.Parse(Log4jXml.ToJson(Log4net)!).RootElement;
        Assert.Equal(("WARN", "Slow payment & retry", "System.TimeoutException: late", "Shop.exe", "web1"),
            (n.GetProperty("level").GetString(), n.GetProperty("message").GetString(), n.GetProperty("exception").GetString(), n.GetProperty("domain").GetString(), n.GetProperty("properties").GetProperty("log4net:HostName").GetString()));
        Assert.Equal("2024-05-01T10:00:01.5+02:00", n.GetProperty("time").GetString());

        // the JSON format reads the lines with level, time and the nested properties
        var line = Log4jXml.ToJson(Log4j.Trim())!;
        var f = FormatDetector.Detect([line]);
        Assert.Equal("json", f.Id);
        Assert.Equal(Level.Error, f.Quick(line).Level);
        Assert.Equal("bob", f.Parse(line)["properties.user"]);
        Assert.Null(Log4jXml.ToJson("<log4j:event level=\"INFO\"><log4j:message>unclosed</log4j:event>"));
        Assert.Null(Log4jXml.ToJson("<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><log4j:event>&e;</log4j:event>"));
    }

    [Fact]
    public void SplitsAStreamOfEventsAcrossChunks()
    {
        var all = Log4j + "  \n" + Log4net + Log4j;
        var whole = new Log4jXmlSplitter(1 << 20).Feed(all);
        Assert.Equal(3, whole.Count);
        var split = new Log4jXmlSplitter(1 << 20);
        var parts = new List<string>();
        for (var i = 0; i < all.Length; i += 7) parts.AddRange(split.Feed(all.Substring(i, Math.Min(7, all.Length - i))));
        Assert.Equal(whole, parts);
        // an event that never ends is dropped once it passes the limit, and the next one still comes through
        var small = new Log4jXmlSplitter(600);
        var got = small.Feed("<log4j:event level=\"INFO\">" + new string('x', 700) + Log4net);
        Assert.Single(got);
        Assert.Equal(1, small.Truncated);
    }
}

public class WinEventTests
{
    private static string Fixture()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "test", "fixtures", "winevents.xml"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir!.FullName, "test", "fixtures", "winevents.xml"));
    }

    private static List<JsonElement> Convert(string xml) =>
        new WinEventXml.Splitter().Feed(xml).Select(e => JsonDocument.Parse(WinEventXml.ToJson(e)!).RootElement).ToList();

    [Fact]
    public void ConvertsEventXmlToJsonLines()
    {
        var ev = Convert(Fixture());
        Assert.Equal(5, ev.Count);
        string S(int i, string k) => ev[i].GetProperty(k).GetString()!;
        Assert.Equal(("2024-05-01T10:00:00.1234567Z", "Error", "Application Error", "1000", "Application", "WS-042.corp.example", "48211"),
            (S(0, "time"), S(0, "level"), S(0, "provider"), S(0, "eventId"), S(0, "channel"), S(0, "computer"), S(0, "recordId")));
        Assert.StartsWith("Faulting application name: shop.exe", S(0, "message"));
        Assert.Equal(("shop.exe", "0xe0434352", "Application Crashing Events"), (ev[0].GetProperty("data").GetProperty("Data1").GetString(), ev[0].GetProperty("data").GetProperty("Data5").GetString(), S(0, "task")));
        Assert.Equal(("Information", "Windows Update", "770075006100750073007600"), (S(1, "level"), ev[1].GetProperty("data").GetProperty("param1").GetString(), ev[1].GetProperty("data").GetProperty("Binary").GetString()));
        Assert.Equal(("Warning", "Audit Failure", "administrator", "Logon"), (S(2, "level"), S(2, "keywords"), ev[2].GetProperty("data").GetProperty("TargetUserName").GetString(), S(2, "task")));
        Assert.Equal(("jdoe", "Application", "", "S-1-5-21-1004336348-1177238915-682003330-512"),
            (ev[3].GetProperty("data").GetProperty("SubjectUserName").GetString(), ev[3].GetProperty("data").GetProperty("Channel").GetString(), S(3, "message"), S(3, "user")));
        Assert.Equal(("Verbose", "if ($a -lt 3 && $b) { Write-Host \"<ok>\" }", "Microsoft-Windows-PowerShell/Operational"),
            (S(4, "level"), ev[4].GetProperty("data").GetProperty("ScriptBlockText").GetString(), S(4, "channel")));
        // one line per event, whatever line breaks the message holds
        Assert.All(new WinEventXml.Splitter().Feed(Fixture()), e => Assert.DoesNotContain('\n', WinEventXml.ToJson(e)!));
    }

    [Fact]
    public void SplitsAcrossChunksAndSkipsBrokenEvents()
    {
        var xml = Fixture();
        var sp = new WinEventXml.Splitter();
        var got = new List<string>();
        for (var i = 0; i < xml.Length; i += 100) got.AddRange(sp.Feed(xml.Substring(i, Math.Min(100, xml.Length - i))));
        Assert.Equal(new WinEventXml.Splitter().Feed(xml), got);
        Assert.Null(WinEventXml.ToJson("<Event><System><Level>2</System></Event>"));
        Assert.Null(WinEventXml.ToJson("<NotAnEvent/>"));
    }

    [Fact]
    public void ReadsTheExportWithTheWindowsEventFormat()
    {
        var lines = new WinEventXml.Splitter().Feed(Fixture()).Select(e => WinEventXml.ToJson(e)!).ToList();
        var f = FormatDetector.Detect(lines);
        Assert.Equal("winevent", f.Id);
        Assert.Equal([Level.Error, Level.Info, Level.Warn, Level.Info, Level.Trace], lines.Select(l => f.Quick(l).Level));
        Assert.Equal(DateTimeOffset.Parse("2024-05-01T10:00:00.123Z").ToUnixTimeMilliseconds(), f.Quick(lines[0]).Time);
        var p = f.Parse(lines[3]);
        Assert.Equal(("Microsoft-Windows-Eventlog", "104", "jdoe"), (p["provider"], p["eventId"], p["data.SubjectUserName"]));
        Assert.Contains("SubjectUserName=jdoe", p["message"]); // no rendered message: the data stands in
    }

    private sealed class FakeWevtutil(Func<int, IReadOnlyList<string>, string> output, int code = 0) : IWevtutil
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<(int Code, string Error)> RunAsync(IReadOnlyList<string> args, Action<string> onOutput, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add(args);
            var text = output(Calls.Count, args);
            for (var i = 0; i < text.Length; i += 50) onOutput(text.Substring(i, Math.Min(50, text.Length - i)));
            return Task.FromResult((code, code == 0 ? "" : "Access is denied."));
        }
    }

    [Fact]
    public async Task ExportsWithAnArgumentArrayAndFollowsByRecordId()
    {
        var events = new WinEventXml.Splitter().Feed(Fixture());
        var fake = new FakeWevtutil((n, _) => n == 1 ? events[0] + events[1] : n == 2 ? events[3] : "");
        var dir = Directory.CreateTempSubdirectory("ulv-ev-").FullName;
        var from = DateTimeOffset.Parse("2024-05-01T09:00:00Z").ToUnixTimeMilliseconds();
        using var log = WinEventLog.Start(fake, "System", from, null, follow: true, dir, poll: TimeSpan.FromMilliseconds(20));
        Assert.Null(await log.FirstExport);
        Assert.Equal(["qe", "System", "/f:RenderedXml", "/rd:false", "/q:*[System[TimeCreated[@SystemTime>='2024-05-01T09:00:00.000Z']]]"], fake.Calls[0]);
        for (var i = 0; i < 200 && fake.Calls.Count < 3; i++) await Task.Delay(10);
        Assert.Equal("/q:*[System[EventRecordID>91702]]", fake.Calls[1][4]);
        Assert.Equal("/q:*[System[EventRecordID>91703]]", fake.Calls[2][4]);
        await log.Capture.FlushAsync();
        var lines = File.ReadAllLines(log.Path);
        Assert.Equal(3, lines.Length);
        Assert.Contains("\"eventId\":\"104\"", lines[2]);
        Assert.Contains("following new events", log.Status);
        log.Stop();
        Assert.False(log.Running);
    }

    [Fact]
    public async Task ReportsErrorsAndRejectsChannelsThatLookLikeOptions()
    {
        var fake = new FakeWevtutil((_, _) => "", code: 5);
        using var log = WinEventLog.Start(fake, "Security", null, null, follow: true, Directory.CreateTempSubdirectory("ulv-ev-").FullName);
        Assert.Equal("Access is denied.", await log.FirstExport);
        Assert.False(log.Running);
        Assert.Equal(["qe", "Security", "/f:RenderedXml", "/rd:false", "/q:*"], fake.Calls[0]);
        Assert.True(WinEventLog.ValidChannel("Microsoft-Windows-PowerShell/Operational"));
        Assert.True(WinEventLog.ValidChannel("Windows PowerShell"));
        Assert.False(WinEventLog.ValidChannel("/f:text"));
        Assert.False(WinEventLog.ValidChannel("-x"));
        Assert.False(WinEventLog.ValidChannel("a\"b"));
        Assert.Throws<ArgumentException>(() => WinEventLog.Start(fake, "/q:*", null, null, false));
        Assert.Equal("*[System[TimeCreated[@SystemTime>='1970-01-01T00:00:01.000Z' and @SystemTime<='1970-01-01T00:00:02.000Z']]]", WinEventLog.TimeQuery(1000, 2000));
    }
}

public class ReceiverTests
{
    private static string Dir() => Directory.CreateTempSubdirectory("ulv-rcv-").FullName;

    private static async Task<List<string>> Entries(LogReceiver r, int expected, string format)
    {
        for (var i = 0; i < 300 && r.Messages < expected; i++) await Task.Delay(10);
        await r.Capture.FlushAsync();
        using var s = new FileSource(0, r.Path, new SourceOptions { Format = format });
        await s.OpenAsync();
        Assert.Equal(format, s.Format.Id);
        return s.ReadEntries(0, s.EntryCount).ToList();
    }

    private static bool PortFree(ProtocolType p, int port)
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, p == ProtocolType.Udp ? SocketType.Dgram : SocketType.Stream, p);
            s.Bind(new IPEndPoint(IPAddress.Loopback, port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    [Fact]
    public async Task ReceivesSyslogOverUdpAndFreesThePort()
    {
        var r = LogReceiver.Start(new ReceiverOptions(ReceiverProtocol.SyslogUdp, Port: 0, MaxMessage: 256), Dir());
        Assert.Matches(@"^udp://127\.0\.0\.1:\d+$", r.Endpoint);
        Assert.False(PortFree(ProtocolType.Udp, r.Port));
        using (var c = new UdpClient())
        {
            c.Connect(IPAddress.Loopback, r.Port);
            await c.SendAsync(Encoding.UTF8.GetBytes("<34>Oct 11 22:14:15 mymachine su[230]: 'su root' failed\n"));
            await c.SendAsync(Encoding.UTF8.GetBytes("<165>1 2024-05-01T10:00:00.000Z web app 1 ID1 [x@1 k=\"v\"] five-four-two-four"));
            await c.SendAsync(Encoding.UTF8.GetBytes("no header at all\u0007"));
            await c.SendAsync(Encoding.UTF8.GetBytes("<14>" + new string('z', 1000)));
            await c.SendAsync(new byte[] { 0xff, 0xfe, 0, 1, 2 }); // garbage
        }
        var e = await Entries(r, 5, "syslog5424");
        Assert.Equal(5, e.Count);
        Assert.Contains("mymachine su 230 - - 'su root' failed", e[0]);
        Assert.EndsWith("web app 1 ID1 [x@1 k=\"v\"] five-four-two-four", e[1]);
        Assert.Matches(@"^<13>1 \S+ 127\.0\.0\.1 - - - - no header at all$", e[2]);
        Assert.True(e[3].Length < 400);
        Assert.Equal(1, r.Truncated);
        Assert.StartsWith("Listening on udp://127.0.0.1:", r.Status);
        Assert.Contains("5 messages · 1 sender", r.Status);
        var port = r.Port;
        r.Dispose();
        Assert.True(PortFree(ProtocolType.Udp, port));
        using var again = LogReceiver.Start(new ReceiverOptions(ReceiverProtocol.SyslogUdp, Port: port), Dir());
        Assert.Equal(port, again.Port);
    }

    [Fact]
    public async Task ReceivesSyslogOverTcpWithBothFramingsAndRebindsAfterStop()
    {
        var r = LogReceiver.Start(new ReceiverOptions(ReceiverProtocol.SyslogTcp, Port: 0), Dir());
        using (var c = new TcpClient())
        {
            await c.ConnectAsync(IPAddress.Loopback, r.Port);
            var s = c.GetStream();
            var framed = "<165>1 2024-05-01T10:00:00.000Z h1 app - - - octet counted";
            await s.WriteAsync(Encoding.UTF8.GetBytes($"{Encoding.UTF8.GetByteCount(framed)} {framed}<13>Oct 11 22:14:15 h2 cron[9]: newline framed\n"));
            for (var i = 0; i < 200 && r.Clients == 0; i++) await Task.Delay(10);
            Assert.Equal(1, r.Clients);
        }
        var e = await Entries(r, 2, "syslog5424");
        Assert.Equal(2, e.Count);
        Assert.EndsWith("h1 app - - - octet counted", e[0]);
        Assert.Contains(" h2 cron 9 - - newline framed", e[1]);
        var port = r.Port;
        // a client still connected when the tab closes is disconnected, and the port can be bound again at once
        var hanging = new TcpClient();
        await hanging.ConnectAsync(IPAddress.Loopback, port);
        r.Stop();
        Assert.False(r.Running);
        using var again = LogReceiver.Start(new ReceiverOptions(ReceiverProtocol.SyslogTcp, Port: port), Dir());
        Assert.Equal(port, again.Port);
        hanging.Dispose();
    }

    [Fact]
    public async Task ReceivesTextLinesAndLog4jXmlOverTcp()
    {
        using var lines = LogReceiver.Start(new ReceiverOptions(ReceiverProtocol.TcpLines, Port: 0), Dir());
        using (var c = new TcpClient())
        {
            await c.ConnectAsync(IPAddress.Loopback, lines.Port);
            await c.GetStream().WriteAsync(Encoding.UTF8.GetBytes("{\"@t\":\"2024-05-01T10:00:00Z\",\"@l\":\"Warning\",\"@m\":\"from serilog\"}\r\n\n{\"@t\":\"2024-05-01T10:00:01Z\",\"@mt\":\"second\"}\n"));
        }
        var e = await Entries(lines, 2, "json");
        Assert.Equal(2, e.Count);
        Assert.Contains("from serilog", e[0]);

        using var xml = LogReceiver.Start(new ReceiverOptions(ReceiverProtocol.Log4jXml, Port: 0), Dir());
        using (var c = new TcpClient())
        {
            await c.ConnectAsync(IPAddress.Loopback, xml.Port);
            var ev = "<log4j:event logger=\"a.B\" timestamp=\"1714557600000\" level=\"ERROR\" thread=\"main\"><log4j:message>boom</log4j:message></log4j:event>";
            var bytes = Encoding.UTF8.GetBytes(ev + ev.Replace("boom", "again"));
            await c.GetStream().WriteAsync(bytes.AsMemory(0, 50));
            await Task.Delay(30);
            await c.GetStream().WriteAsync(bytes.AsMemory(50));
        }
        var x = await Entries(xml, 2, "json");
        Assert.Equal(2, x.Count);
        Assert.Equal("again", JsonDocument.Parse(x[1]).RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public void RefusesBadBindsAndTakenPorts()
    {
        Assert.True(LogReceiver.IsLoopback("127.0.0.1"));
        Assert.True(LogReceiver.IsLoopback("::1"));
        Assert.True(LogReceiver.IsLoopback("localhost"));
        Assert.False(LogReceiver.IsLoopback("0.0.0.0"));
        Assert.False(LogReceiver.IsLoopback("example.com"));
        Assert.Throws<ArgumentException>(() => LogReceiver.Start(new ReceiverOptions(ReceiverProtocol.TcpLines, Bind: "example.com"), Dir()));
        using var a = LogReceiver.Start(new ReceiverOptions(ReceiverProtocol.TcpLines, Port: 0), Dir());
        Assert.ThrowsAny<SocketException>(() => LogReceiver.Start(new ReceiverOptions(ReceiverProtocol.TcpLines, Port: a.Port), Dir()));
    }

    [Fact]
    public async Task CaptureFilesAreOwnerOnlyAndRotateAtTheCap()
    {
        var dir = Path.Combine(Dir(), "receivers");
        using var c = CaptureFile.Create(dir, "t", 64 * 1024);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(dir));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(c.Path));
        }
        var line = new string('x', 999);
        for (var i = 0; i < 100; i++) c.WriteLine(line);
        await c.FlushAsync();
        Assert.Equal(1, c.Rotations);
        Assert.Equal(100, File.ReadAllLines(c.Path).Length + File.ReadAllLines(c.RotatedPath).Length);
        Assert.True(new FileInfo(c.Path).Length <= 64 * 1024);
    }
}
