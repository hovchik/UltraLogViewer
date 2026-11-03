using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UltraLogViewer.Core;

/// <summary>
/// One window per user: a second launch hands its command line to the running instance over a named pipe (a Unix
/// socket on Linux and macOS) that only the same user can connect to, then exits. Messages are length-prefixed UTF-8
/// and acknowledged with one byte, so the sender only exits once the running instance has the request.
/// </summary>
public static class SingleInstance
{
    private const int MaxMessage = 1 << 20;

    /// <summary>The pipe of this user (a hash, so the name has no characters a pipe name cannot have).</summary>
    public static string DefaultName
    {
        get
        {
            var who = $"{Environment.UserDomainName}\\{Environment.UserName}";
            return "UltraLogViewer-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(who)))[..16].ToLowerInvariant();
        }
    }

    /// <summary>A second launch's arguments with its working directory, for relative paths.</summary>
    public sealed record Handoff(string Cwd, string[] Args)
    {
        public string ToJson() => JsonSerializer.Serialize(new Dictionary<string, object> { ["cwd"] = Cwd, ["args"] = Args });

        public static Handoff? FromJson(string json)
        {
            try
            {
                using var d = JsonDocument.Parse(json);
                var cwd = d.RootElement.GetProperty("cwd").GetString() ?? "";
                var args = d.RootElement.GetProperty("args").EnumerateArray().Select(a => a.GetString() ?? "").ToArray();
                return new Handoff(cwd, args);
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                return null;
            }
        }
    }

    /// <summary>Sends a message to the running instance; false when none is listening (or it did not answer in time).</summary>
    public static bool TrySend(string name, string message, int timeoutMs = 500)
    {
        // Connect waits the whole timeout for a pipe that does not exist: check first, so a normal start is not slowed.
        if (!PipeExists(name)) return false;
        try
        {
            using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            pipe.Connect(timeoutMs);
            var body = Encoding.UTF8.GetBytes(message);
            pipe.Write(BitConverter.GetBytes(body.Length));
            pipe.Write(body);
            pipe.Flush();
            using var cts = new CancellationTokenSource(Math.Max(timeoutMs, 2000));
            var ack = new byte[1];
            return pipe.ReadAsync(ack, cts.Token).AsTask().GetAwaiter().GetResult() == 1 && ack[0] == (byte)'k';
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return false;
        }
    }

    private static string SocketPath(string name) => Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + name);

    internal static bool PipeExists(string name)
    {
        if (!OperatingSystem.IsWindows()) return File.Exists(SocketPath(name));
        try
        {
            return Directory.EnumerateFiles(@"\\.\pipe\").Any(p => string.Equals(Path.GetFileName(p), name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return true; // cannot tell: try to connect
        }
    }

    /// <summary>
    /// Listens for messages until disposed; <paramref name="onMessage"/> runs on a pool thread. Returns null when the
    /// pipe cannot be created (another instance owns it).
    /// </summary>
    public static IDisposable? Listen(string name, Action<string> onMessage)
    {
        NamedPipeServerStream First()
        {
            try
            {
                return Create(name);
            }
            catch (IOException) when (!OperatingSystem.IsWindows())
            {
                // a socket file left by an instance that crashed: nobody answers on it, so it can go
                var stale = SocketPath(name);
                if (File.Exists(stale) && !TrySend(name, "", 200)) File.Delete(stale);
                return Create(name);
            }
        }
        NamedPipeServerStream first;
        try
        {
            first = First();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        var cts = new CancellationTokenSource();
        _ = Task.Run(() => Loop(name, first, onMessage, cts.Token));
        return new Stop(cts);
    }

    private static NamedPipeServerStream Create(string name) =>
        new(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private static async Task Loop(string name, NamedPipeServerStream pipe, Action<string> onMessage, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await pipe.WaitForConnectionAsync(ct);
                var conn = pipe;
                _ = Task.Run(() => Serve(conn, onMessage), CancellationToken.None);
                pipe = Create(name);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                await pipe.DisposeAsync();
                try
                {
                    pipe = Create(name);
                }
                catch (IOException)
                {
                    return;
                }
            }
        }
        await pipe.DisposeAsync();
    }

    private static async Task Serve(NamedPipeServerStream pipe, Action<string> onMessage)
    {
        await using (pipe)
        {
            try
            {
                using var cts = new CancellationTokenSource(5000);
                var head = new byte[4];
                await pipe.ReadExactlyAsync(head, cts.Token);
                var len = BitConverter.ToInt32(head);
                if (len is < 0 or > MaxMessage) return;
                var body = new byte[len];
                await pipe.ReadExactlyAsync(body, cts.Token);
                await pipe.WriteAsync(new[] { (byte)'k' }, cts.Token);
                await pipe.FlushAsync(cts.Token);
                if (len > 0) onMessage(Encoding.UTF8.GetString(body)); // an empty message only checks that we are alive
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or EndOfStreamException)
            {
                // the sender went away
            }
        }
    }

    private sealed class Stop(CancellationTokenSource cts) : IDisposable
    {
        public void Dispose() => cts.Cancel();
    }
}
