namespace UltraLogViewer.Core;

/// <summary>
/// "UltraLogViewer -": copies standard input (or any stream) into a growing temp file in the background. The file is
/// opened like any log, so the normal tail picks up new lines live; it is deleted when the app exits.
/// </summary>
public sealed class StdinCapture : IDisposable
{
    private readonly Stream _input;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _written;
    private long _firstData;

    private readonly FileStream _output;

    private StdinCapture(Stream input, string path, FileStream output)
    {
        _input = input;
        _output = output;
        Path = path;
    }

    public string Path { get; }
    public long Written => Interlocked.Read(ref _written);
    /// <summary>Completes when the input has ended (or failed).</summary>
    public Task Completion { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Starts copying and returns once there is enough to detect the format: 64 KB, the end of the input, half a second
    /// after the first data, or <paramref name="maxWait"/> without any data.
    /// </summary>
    public static async Task<StdinCapture> StartAsync(Stream input, string? dir = null, TimeSpan? maxWait = null)
    {
        dir ??= System.IO.Path.GetTempPath();
        Directory.CreateDirectory(dir);
        // The temp folder may be shared with other users: a name nobody can guess, created new (never through a link
        // someone planted there) and readable by this user only, since piped input can hold anything.
        var path = System.IO.Path.Combine(dir, $"stdin-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}-{Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(5)).ToLowerInvariant()}.log");
        var create = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.ReadWrite | FileShare.Delete, BufferSize = 0 };
        if (!OperatingSystem.IsWindows()) create.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var c = new StdinCapture(input, path, new FileStream(path, create));
        c.Completion = Task.Run(c.Copy);
        var deadline = DateTime.UtcNow + (maxWait ?? TimeSpan.FromSeconds(3));
        while (!c._ready.Task.IsCompleted && DateTime.UtcNow < deadline)
        {
            var first = Interlocked.Read(ref c._firstData);
            if (first != 0 && Environment.TickCount64 - first > 500) break;
            await Task.WhenAny(c._ready.Task, Task.Delay(20));
        }
        return c;
    }

    private void Copy()
    {
        try
        {
            using var output = _output;
            var buf = new byte[64 * 1024];
            int n;
            while ((n = _input.Read(buf, 0, buf.Length)) > 0)
            {
                output.Write(buf, 0, n); // unbuffered: every read reaches the file at once, for the live tail
                if (Interlocked.Read(ref _firstData) == 0) Interlocked.Exchange(ref _firstData, Environment.TickCount64);
                if (Interlocked.Add(ref _written, n) >= 64 * 1024) _ready.TrySetResult();
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // the pipe broke or the app is closing: keep what arrived
        }
        finally
        {
            _ready.TrySetResult();
        }
    }

    public void Dispose()
    {
        try { File.Delete(Path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
