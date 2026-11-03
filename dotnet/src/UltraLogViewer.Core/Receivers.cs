using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace UltraLogViewer.Core;

public enum ReceiverProtocol { SyslogUdp, SyslogTcp, TcpLines, Log4jXml }

/// <param name="MaxMessage">Longest message or line kept, in bytes; the rest is cut off.</param>
/// <param name="CapBytes">Size at which the capture file is rotated.</param>
public sealed record ReceiverOptions(ReceiverProtocol Protocol, string Bind = "127.0.0.1", int Port = 5514, int MaxMessage = 64 * 1024, long CapBytes = 1L << 30)
{
    public static readonly (ReceiverProtocol P, string Label)[] Protocols =
    [
        (ReceiverProtocol.SyslogUdp, "Syslog over UDP"),
        (ReceiverProtocol.SyslogTcp, "Syslog over TCP (octet counting or newline)"),
        (ReceiverProtocol.TcpLines, "Text lines over TCP (Serilog, NLog, log4j2 JSON or pattern)"),
        (ReceiverProtocol.Log4jXml, "log4j / log4net XML over TCP"),
    ];

    public string Scheme => Protocol == ReceiverProtocol.SyslogUdp ? "udp" : "tcp";
}

/// <summary>
/// Splits a TCP byte stream into messages. <see cref="Syslog"/> mode follows RFC 6587 frame by frame: a frame that
/// starts with a digit is octet-counted ("LEN SP MSG"), anything else ends at a line feed. Lines mode splits at line
/// feeds only. Messages longer than <c>max</c> bytes are cut off (and counted); malformed counts fall back to a line.
/// </summary>
public sealed class StreamFramer(bool syslog, int max)
{
    private enum State { Start, Count, Octets, Line, SkipLine }

    private readonly byte[] _buf = new byte[Math.Max(16, max)];
    private int _len;
    private State _state = State.Start;
    private long _count;
    private int _digits;
    private long _remaining;

    public int Truncated { get; private set; }

    public void Feed(ReadOnlySpan<byte> data, Action<string> emit)
    {
        foreach (var b in data)
        {
            switch (_state)
            {
                case State.Start:
                    if (syslog && b is (byte)'\n' or (byte)'\r' or (byte)' ' or 0) continue; // padding between frames
                    if (syslog && b is >= (byte)'0' and <= (byte)'9')
                    {
                        _state = State.Count;
                        _count = b - '0';
                        _digits = 1;
                        Add(b);
                        continue;
                    }
                    _state = State.Line;
                    goto case State.Line;
                case State.Count:
                    if (b is >= (byte)'0' and <= (byte)'9' && _digits < 9)
                    {
                        _count = _count * 10 + (b - '0');
                        _digits++;
                        Add(b);
                    }
                    else if (b == ' ' && _count > 0)
                    {
                        _len = 0;
                        _remaining = _count;
                        _state = State.Octets;
                    }
                    else
                    {
                        _state = State.Line; // not a count after all: the digits start a line
                        goto case State.Line;
                    }
                    continue;
                case State.Octets:
                    if (_len < _buf.Length) _buf[_len++] = b;
                    if (--_remaining == 0)
                    {
                        if (_count > _buf.Length) Truncated++;
                        Emit(emit);
                    }
                    continue;
                case State.Line:
                    if (b == '\n')
                    {
                        if (_len > 0 && _buf[_len - 1] == '\r') _len--;
                        Emit(emit);
                    }
                    else if (_len < _buf.Length) _buf[_len++] = b;
                    else
                    {
                        Truncated++;
                        Emit(emit);
                        _state = State.SkipLine;
                    }
                    continue;
                case State.SkipLine:
                    if (b == '\n') _state = State.Start;
                    continue;
            }
        }
    }

    /// <summary>The connection ended: a partial line still counts as a message.</summary>
    public void End(Action<string> emit)
    {
        if (_state is State.Line or State.Count && _len > 0) Emit(emit);
        _state = State.Start;
    }

    private void Add(byte b)
    {
        if (_len < _buf.Length) _buf[_len++] = b;
    }

    private void Emit(Action<string> emit)
    {
        emit(Encoding.UTF8.GetString(_buf, 0, _len));
        _len = 0;
        _state = State.Start;
    }
}

/// <summary>
/// A network listener that writes what it receives to a <see cref="CaptureFile"/>: syslog over UDP or TCP (each
/// message normalised to one RFC 5424 line), plain text lines over TCP, or log4j/log4net XML events (one JSON line
/// each). Receiving never waits for the disk; stopping (or disposing, when the tab closes) frees the port at once.
/// </summary>
public sealed class LogReceiver : ILiveCapture
{
    public const int MaxClients = 64;

    private readonly ReceiverOptions _o;
    private readonly CaptureFile _capture;
    private readonly CancellationTokenSource _cts = new();
    private readonly Socket _socket;
    private readonly ConcurrentDictionary<Socket, byte> _clients = new();
    private readonly ConcurrentDictionary<string, byte> _senders = new();
    private long _messages;
    private long _truncated;
    private int _stopped;

    private LogReceiver(ReceiverOptions o, CaptureFile capture, Socket socket)
    {
        _o = o;
        _capture = capture;
        _socket = socket;
        var ep = (IPEndPoint)socket.LocalEndPoint!;
        Port = ep.Port;
        Endpoint = $"{o.Scheme}://{(ep.Address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{ep.Address}]" : ep.Address.ToString())}:{ep.Port}";
    }

    public string Path => _capture.Path;
    public CaptureFile Capture => _capture;
    public ReceiverOptions Options => _o;
    public int Port { get; }
    /// <summary>"udp://127.0.0.1:5514".</summary>
    public string Endpoint { get; }
    public long Messages => Interlocked.Read(ref _messages);
    /// <summary>Connected TCP clients, or for UDP the number of different senders seen.</summary>
    public int Clients => _o.Protocol == ReceiverProtocol.SyslogUdp ? _senders.Count : _clients.Count;
    public long Truncated => Interlocked.Read(ref _truncated);
    public bool Running => _stopped == 0;

    public string Status
    {
        get
        {
            var n = Messages;
            var c = Clients;
            var who = _o.Protocol == ReceiverProtocol.SyslogUdp ? (c == 1 ? "sender" : "senders") : (c == 1 ? "client" : "clients");
            var s = $"{n.ToString("N0", CultureInfo.CurrentCulture)} {(n == 1 ? "message" : "messages")} · {c} {who}";
            if (Truncated > 0) s += $" · {Truncated:N0} cut to {_o.MaxMessage:N0} bytes";
            if (_capture.Dropped > 0) s += $" · {_capture.Dropped:N0} dropped";
            if (_capture.Error is { } e) s += $" · write error: {e}";
            return (Running ? $"Listening on {Endpoint}" : $"Stopped ({Endpoint})") + "\n" + s;
        }
    }

    /// <summary>True for 127.0.0.0/8, ::1 and "localhost": the only binds that need no confirmation.</summary>
    public static bool IsLoopback(string bind) =>
        bind.Trim().Equals("localhost", StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(bind.Trim(), out var a) && IPAddress.IsLoopback(a));

    /// <summary>An IP address to bind to, or null. Host names other than localhost are not accepted.</summary>
    public static IPAddress? ParseBind(string bind)
    {
        var b = bind.Trim();
        if (b.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return IPAddress.Loopback;
        if (b.StartsWith('[') && b.EndsWith(']')) b = b[1..^1];
        return IPAddress.TryParse(b, out var a) ? a : null;
    }

    /// <summary>Binds (throws <see cref="SocketException"/> when the port is taken) and starts receiving.</summary>
    public static LogReceiver Start(ReceiverOptions o, string? dir = null)
    {
        var addr = ParseBind(o.Bind) ?? throw new ArgumentException($"Not an IP address: {o.Bind}");
        if (o.Port is < 0 or > 65535) throw new ArgumentException($"Not a port: {o.Port}");
        if (o.MaxMessage is < 256 or > 16 * 1024 * 1024) throw new ArgumentException("The maximum message size must be between 256 bytes and 16 MB");
        var udp = o.Protocol == ReceiverProtocol.SyslogUdp;
        var socket = new Socket(addr.AddressFamily, udp ? SocketType.Dgram : SocketType.Stream, udp ? ProtocolType.Udp : ProtocolType.Tcp);
        try
        {
            if (udp) socket.ReceiveBufferSize = 4 * 1024 * 1024; // absorbs bursts while the loop hands datagrams to the queue
            socket.Bind(new IPEndPoint(addr, o.Port));
            if (!udp) socket.Listen(32);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        var prefix = o.Protocol switch
        {
            ReceiverProtocol.SyslogUdp => "syslog-udp",
            ReceiverProtocol.SyslogTcp => "syslog-tcp",
            ReceiverProtocol.Log4jXml => "log4j-xml",
            _ => "tcp-lines",
        } + "-" + ((IPEndPoint)socket.LocalEndPoint!).Port;
        CaptureFile capture;
        try
        {
            capture = CaptureFile.Create(dir, prefix, o.CapBytes);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        var r = new LogReceiver(o, capture, socket);
        _ = Task.Run(udp ? r.UdpLoop : r.AcceptLoop);
        return r;
    }

    private void Add(string line)
    {
        if (line.Length == 0) return;
        if (_capture.WriteLine(line)) Interlocked.Increment(ref _messages);
    }

    private async Task UdpLoop()
    {
        var buf = new byte[65536];
        EndPoint any = new IPEndPoint(_socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        while (!_cts.IsCancellationRequested)
        {
            SocketReceiveFromResult res;
            try
            {
                res = await _socket.ReceiveFromAsync(buf, SocketFlags.None, any, _cts.Token);
            }
            catch (SocketException e) when (e.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize)
            {
                continue; // an ICMP error for an earlier datagram, or one larger than the buffer
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            var sender = res.RemoteEndPoint is IPEndPoint ip ? ip.Address.ToString() : "-";
            if (_senders.Count < 1000) _senders.TryAdd(sender, 0);
            var n = res.ReceivedBytes;
            if (n > _o.MaxMessage)
            {
                n = _o.MaxMessage;
                Interlocked.Increment(ref _truncated);
            }
            while (n > 0 && buf[n - 1] is (byte)'\n' or (byte)'\r' or 0) n--;
            if (n == 0) continue;
            Add(Syslog.ToLine(Encoding.UTF8.GetString(buf, 0, n), DateTimeOffset.Now, sender));
        }
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _socket.AcceptAsync(_cts.Token);
            }
            catch (SocketException e) when (e.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted)
            {
                continue;
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            if (_clients.Count >= MaxClients)
            {
                client.Dispose();
                continue;
            }
            _clients.TryAdd(client, 0);
            _ = Task.Run(() => ClientLoop(client));
        }
    }

    private async Task ClientLoop(Socket client)
    {
        var sender = (client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "-";
        var buf = new byte[64 * 1024];
        StreamFramer? framer = _o.Protocol == ReceiverProtocol.Log4jXml ? null : new StreamFramer(_o.Protocol == ReceiverProtocol.SyslogTcp, _o.MaxMessage);
        var xml = framer is null ? new Log4jXmlSplitter(_o.MaxMessage) : null;
        var decoder = new UTF8Encoding(false).GetDecoder();
        var chars = new char[buf.Length + 4];
        var counted = 0;
        void CountTruncated()
        {
            var t = (framer?.Truncated ?? 0) + (xml?.Truncated ?? 0);
            if (t != counted) Interlocked.Add(ref _truncated, t - counted);
            counted = t;
        }
        void OnMessage(string m)
        {
            if (_o.Protocol == ReceiverProtocol.SyslogTcp) Add(Syslog.ToLine(m, DateTimeOffset.Now, sender));
            else if (Sanitize.Line(m) is var line && line.Trim().Length > 0) Add(line);
        }
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var n = await client.ReceiveAsync(buf, SocketFlags.None, _cts.Token);
                if (n == 0) break;
                if (framer is not null)
                {
                    framer.Feed(buf.AsSpan(0, n), OnMessage);
                    CountTruncated();
                    continue;
                }
                var c = decoder.GetChars(buf, 0, n, chars, 0);
                foreach (var ev in xml!.Feed(new string(chars, 0, c)))
                    Add(Log4jXml.ToJson(ev) ?? Sanitize.Line(ev));
                CountTruncated();
            }
            framer?.End(OnMessage);
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // the client went away or we are stopping
        }
        finally
        {
            CountTruncated();
            _clients.TryRemove(client, out _);
            client.Dispose();
        }
    }

    /// <summary>Stops listening, disconnects the clients and closes the capture after writing what was received.</summary>
    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        _cts.Cancel();
        _socket.Dispose();
        foreach (var c in _clients.Keys)
        {
            try { c.Shutdown(SocketShutdown.Both); } catch (SocketException) { } catch (ObjectDisposedException) { }
            c.Dispose();
        }
        _capture.Dispose();
    }

    public void Dispose() => Stop();
}
