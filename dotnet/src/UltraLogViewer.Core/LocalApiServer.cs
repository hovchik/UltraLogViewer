using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UltraLogViewer.Core;

/// <summary>An open tab as the local API lists it.</summary>
public sealed record ApiTab(int Id, string Title, IReadOnlyList<string> Paths, long Total, long Shown, string Filter, bool Ready, bool Active);

/// <summary>Outcome of an API call that changes something: an HTTP status, an error text, and the tab it concerns.</summary>
public sealed record ApiResult(int Status, string? Error = null, int? Tab = null)
{
    public static ApiResult Ok(int tab) => new(200, null, tab);
}

/// <summary>What the app does for the local API (called on a pool thread; the app moves to its UI thread itself).</summary>
public interface ILocalApiHost
{
    Task<ApiResult> OpenAsync(OpenRequest request);
    Task<IReadOnlyList<ApiTab>> TabsAsync();
    /// <summary>Applies the filter, levels, search and "go to" of <paramref name="request"/> to an open tab.</summary>
    Task<ApiResult> UpdateAsync(int tab, OpenRequest request);
    /// <summary>Rows of the tab's current view; null when there is no such tab.</summary>
    Task<IReadOnlyList<RowData>?> RowsAsync(int tab, int from, int count);
}

/// <summary>
/// The local HTTP API, off unless the user turns it on. It listens on 127.0.0.1 only and every request needs the
/// token (<c>Authorization: Bearer …</c> or <c>?token=</c>). A web page cannot use it: requests whose Host is not
/// 127.0.0.1/localhost (DNS rebinding) and requests with a foreign Origin are refused, and there are no CORS headers,
/// so a browser never lets a page read an answer. A deliberately small HTTP/1.1 server (one request per connection)
/// so this is all in plain sight and needs no URL reservation on Windows.
/// </summary>
public sealed partial class LocalApiServer : IDisposable
{
    public const int DefaultPort = 7700;
    public const int MaxRows = 5000;
    private const int MaxHeader = 16 * 1024, MaxBody = 1 << 20;

    private readonly ILocalApiHost _host;
    private readonly byte[] _token;
    private readonly Func<long> _now;
    private TcpListener? _listener;
    private readonly CancellationTokenSource _cts = new();

    public int Port { get; private set; }

    public LocalApiServer(ILocalApiHost host, string token, Func<long>? now = null)
    {
        if (token.Length < 16) throw new ArgumentException("The API token is too short", nameof(token));
        _host = host;
        _token = Encoding.UTF8.GetBytes(token);
        _now = now ?? (() => DateTimeOffset.Now.ToUnixTimeMilliseconds());
    }

    /// <summary>A new random token (256 bits, URL-safe).</summary>
    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    /// <summary>Listens on <paramref name="port"/>, or the next free one of the 9 after it, or any free port. Returns the port.</summary>
    public int Start(int port = DefaultPort)
    {
        var tries = port is > 0 and < 65536 ? Enumerable.Range(port, 10).Where(p => p < 65536).Append(0) : [0];
        foreach (var p in tries)
        {
            var l = new TcpListener(IPAddress.Loopback, p);
            try
            {
                l.Start();
            }
            catch (SocketException)
            {
                continue;
            }
            _listener = l;
            Port = ((IPEndPoint)l.LocalEndpoint).Port;
            _ = Task.Run(AcceptLoop);
            return Port;
        }
        throw new IOException("No free port for the local API");
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener?.Stop();
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(_cts.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private sealed record Request(string Method, string Path, Dictionary<string, string> Query, Dictionary<string, string> Headers, string Body);
    private sealed record Response(int Status, string Body, string ContentType = "application/json; charset=utf-8", string? Extra = null);

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                cts.CancelAfter(TimeSpan.FromSeconds(30));
                var stream = client.GetStream();
                var (req, bad) = await ReadRequestAsync(stream, cts.Token);
                var res = bad ?? await HandleAsync(req!);
                await WriteAsync(stream, res, cts.Token);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or SocketException or ObjectDisposedException)
            {
                // the client went away or was too slow
            }
        }
    }

    private static Response Error(int status, string message, string? extra = null) =>
        new(status, JsonSerializer.Serialize(new Dictionary<string, string> { ["error"] = message }), Extra: extra);

    private static async Task<(Request?, Response?)> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var buf = new byte[MaxHeader];
        var n = 0;
        int end;
        while ((end = IndexOfHeaderEnd(buf, n)) < 0)
        {
            if (n == buf.Length) return (null, Error(431, "Headers too large"));
            var r = await stream.ReadAsync(buf.AsMemory(n), ct);
            if (r == 0) throw new IOException("closed");
            n += r;
        }
        var head = Encoding.ASCII.GetString(buf, 0, end).Split("\r\n");
        var first = head[0].Split(' ');
        if (first.Length != 3 || !first[2].StartsWith("HTTP/1.", StringComparison.Ordinal)) return (null, Error(400, "Bad request line"));
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in head.Skip(1))
        {
            var c = line.IndexOf(':');
            if (c <= 0) return (null, Error(400, "Bad header"));
            var key = line[..c].Trim();
            // A repeated Host or Origin could smuggle a second value past the checks.
            if (headers.ContainsKey(key) && key.ToLowerInvariant() is "host" or "origin" or "authorization") return (null, Error(400, $"Repeated {key} header"));
            headers[key] = line[(c + 1)..].Trim();
        }
        if (headers.ContainsKey("Transfer-Encoding")) return (null, Error(411, "Send a Content-Length"));
        var length = 0;
        if (headers.TryGetValue("Content-Length", out var cl) && (!int.TryParse(cl, NumberStyles.None, CultureInfo.InvariantCulture, out length) || length > MaxBody))
            return (null, Error(413, "Body too large"));
        var body = new byte[length];
        var have = Math.Min(length, n - end - 4);
        Array.Copy(buf, end + 4, body, 0, have);
        if (have < length && headers.TryGetValue("Expect", out var expect) && expect.Equals("100-continue", StringComparison.OrdinalIgnoreCase))
            await stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), ct);
        while (have < length)
        {
            var r = await stream.ReadAsync(body.AsMemory(have), ct);
            if (r == 0) throw new IOException("closed");
            have += r;
        }
        var target = first[1];
        var q = target.IndexOf('?');
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (q >= 0)
            foreach (var part in target[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=');
                try
                {
                    var k = Uri.UnescapeDataString(eq < 0 ? part : part[..eq]);
                    query[k] = eq < 0 ? "" : Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
                }
                catch (UriFormatException)
                {
                    return (null, Error(400, "Bad query string"));
                }
            }
        return (new Request(first[0].ToUpperInvariant(), q < 0 ? target : target[..q], query, headers, Encoding.UTF8.GetString(body)), null);
    }

    private static int IndexOfHeaderEnd(byte[] buf, int n)
    {
        for (var i = 3; i < n; i++)
            if (buf[i] == '\n' && buf[i - 1] == '\r' && buf[i - 2] == '\n' && buf[i - 3] == '\r') return i - 3;
        return -1;
    }

    private static async Task WriteAsync(NetworkStream stream, Response res, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(res.Body);
        var reason = res.Status switch
        {
            200 => "OK", 400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed",
            411 => "Length Required", 413 => "Payload Too Large", 422 => "Unprocessable Entity", 431 => "Request Header Fields Too Large", _ => "Error",
        };
        var head = $"HTTP/1.1 {res.Status} {reason}\r\nContent-Type: {res.ContentType}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\n" +
                   $"X-Content-Type-Options: nosniff\r\nConnection: close\r\n{res.Extra}\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }

    // ================================================================== checks

    /// <summary>Host must name the loopback address: a DNS-rebinding page's requests carry its own host name.</summary>
    internal static bool HostAllowed(string? host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        var name = host.StartsWith('[') ? host[..(host.IndexOf(']') + 1)] : host.Split(':')[0];
        return name.Equals("127.0.0.1", StringComparison.Ordinal) || name.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>No Origin (curl, scripts) or our own; a browser adds the page's origin to everything it sends.</summary>
    internal bool OriginAllowed(string? origin) =>
        origin is null || origin == $"http://127.0.0.1:{Port}" || origin.Equals($"http://localhost:{Port}", StringComparison.OrdinalIgnoreCase);

    private bool TokenOk(Request r)
    {
        string? given = null;
        if (r.Headers.TryGetValue("Authorization", out var auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) given = auth[7..].Trim();
        else if (r.Query.TryGetValue("token", out var t)) given = t;
        return given is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), _token);
    }

    [GeneratedRegex(@"^/tabs/(\d{1,9})/(filter|goto|rows)$")]
    private static partial Regex TabRoute();

    private async Task<Response> HandleAsync(Request r)
    {
        if (!HostAllowed(r.Headers.GetValueOrDefault("Host"))) return Error(403, "Host must be 127.0.0.1 or localhost");
        if (!OriginAllowed(r.Headers.GetValueOrDefault("Origin"))
            || r.Headers.TryGetValue("Sec-Fetch-Site", out var site) && site is "cross-site" or "same-site")
            return Error(403, "Requests from web pages are not allowed");
        if (!TokenOk(r)) return Error(401, "Missing or wrong token", "WWW-Authenticate: Bearer\r\n");
        try
        {
            switch (r.Method, r.Path)
            {
                case ("GET", "/health"):
                    return new(200, """{"ok":true,"app":"UltraLogViewer"}""");
                case ("POST", "/open"):
                {
                    var req = OpenRequest.FromJson(r.Body, _now());
                    if (!req.IsValid) return Error(400, string.Join("; ", req.Errors));
                    if (req.Paths.Count == 0) return Error(400, "No paths");
                    return Reply(await _host.OpenAsync(req));
                }
                case ("GET", "/tabs"):
                    return new(200, JsonSerializer.Serialize(await _host.TabsAsync(), JsonOpts));
                case (_, "/open" or "/tabs" or "/health"):
                    return Error(405, "Method not allowed");
            }
            if (TabRoute().Match(r.Path) is { Success: true } m)
            {
                var id = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                switch (m.Groups[2].Value, r.Method)
                {
                    case ("rows", "GET"):
                    {
                        var from = Math.Max(0, IntParam(r, "from", 0));
                        var count = Math.Clamp(IntParam(r, "count", 100), 0, MaxRows);
                        var rows = await _host.RowsAsync(id, from, count);
                        if (rows is null) return Error(404, $"No tab {id}");
                        var sb = new StringBuilder();
                        foreach (var row in rows) sb.Append(RowJson(row)).Append('\n');
                        return new(200, sb.ToString(), "application/x-ndjson; charset=utf-8");
                    }
                    case ("filter" or "goto", "POST"):
                    {
                        var req = OpenRequest.FromJson(r.Body, _now());
                        if (!req.IsValid) return Error(400, string.Join("; ", req.Errors));
                        if (m.Groups[2].Value == "goto" && req.GoTo is null) return Error(400, "Give \"goto\": a time, a line or \"file:line\"");
                        if (m.Groups[2].Value == "filter") req.GoTo = null;
                        else (req.Filter, req.Search, req.Levels) = (null, null, null);
                        return Reply(await _host.UpdateAsync(id, req));
                    }
                }
                return Error(405, "Method not allowed");
            }
            return Error(404, "No such endpoint");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return Error(500, e.Message);
        }
    }

    private static int IntParam(Request r, string key, int fallback) =>
        r.Query.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;

    private static Response Reply(ApiResult res) =>
        res.Error is not null ? Error(res.Status, res.Error) : new(res.Status, JsonSerializer.Serialize(new { ok = true, tab = res.Tab }, JsonOpts));

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>One row as a JSON line: view position, file, line, time (ISO, UTC), level, fields and the text.</summary>
    public static string RowJson(RowData row) => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["row"] = row.Row,
        ["source"] = row.Source,
        ["line"] = row.Line,
        ["time"] = row.Time is long t ? DateTimeOffset.FromUnixTimeMilliseconds(t).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) : null,
        ["level"] = LevelNames.Labels[(int)row.Level],
        ["fields"] = row.Fields,
        ["text"] = row.Text,
    });

    /// <summary>A ready-to-run example for "Copy API example".</summary>
    public static string CurlExample(int port, string token, string? path)
    {
        var body = new OpenRequest { Paths = [path ?? (OperatingSystem.IsWindows() ? @"C:\logs\app.log" : "/var/log/app.log")], Filter = "level:error", Window = new OpenWindow(Last: 3_600_000) }.ToJson();
        var q = OperatingSystem.IsWindows() ? "\"" : "'";
        var b = OperatingSystem.IsWindows() ? body.Replace("\"", "\\\"") : body.Replace("'", "'\\''");
        return $"curl -H \"Authorization: Bearer {token}\" -X POST http://127.0.0.1:{port}/open -d {q}{b}{q}\n" +
               $"curl -H \"Authorization: Bearer {token}\" http://127.0.0.1:{port}/tabs";
    }
}
