using System.Text;
using System.Threading.Channels;

namespace UltraLogViewer.Core;

/// <summary>Something that keeps writing to a capture file shown in a live tab: a network receiver or an event log follower.</summary>
public interface ILiveCapture : IDisposable
{
    string Path { get; }
    /// <summary>One or two lines for the Sources list, e.g. "Listening on udp://127.0.0.1:5514\n12 messages · 1 client".</summary>
    string Status { get; }
    bool Running { get; }
    void Stop();
}

/// <summary>
/// The local file a receiver writes what it receives to, one line per message. The file is opened as a normal live
/// tab, so indexing, filters and tailing work unchanged (as for standard input). Writes happen on a background task
/// behind a queue, so bursts and a slow disk never hold up the sockets or the UI; when more than
/// <see cref="MaxPendingBytes"/> wait, new lines are dropped and counted. At <c>capBytes</c> the file is rotated:
/// it becomes "name.1.log" (replacing the previous one) and a new file starts under the same name.
/// </summary>
public sealed class CaptureFile : IDisposable
{
    public const long MaxPendingBytes = 64L * 1024 * 1024;

    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly long _cap;
    private readonly Task _writer;
    private FileStream _out;
    private long _size;
    private long _pending;
    private long _lines;
    private long _written;
    private long _dropped;

    private CaptureFile(string path, long cap)
    {
        Path = path;
        _cap = Math.Max(64 * 1024, cap);
        _out = CreateNew(path);
        _writer = Task.Run(WriteLoop);
    }

    public string Path { get; }
    public string RotatedPath => System.IO.Path.ChangeExtension(Path, null) + ".1.log";
    /// <summary>Lines accepted, lines on disk, and lines dropped because the queue was full.</summary>
    public long Lines => Interlocked.Read(ref _lines);
    public long Written => Interlocked.Read(ref _written);
    public long Dropped => Interlocked.Read(ref _dropped);
    public int Rotations { get; private set; }
    /// <summary>The last write error (disk full, …), or null.</summary>
    public string? Error { get; private set; }

    /// <summary>The per-user folder captures go to: next to the settings, readable by this user only.</summary>
    public static string DefaultFolder
    {
        get
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(root)) root = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(root)) root = System.IO.Path.GetTempPath();
            return System.IO.Path.Combine(root, "UltraLogViewer", "receivers");
        }
    }

    /// <summary>Creates the folder with owner-only permissions (and tightens them if it already exists).</summary>
    public static string EnsureFolder(string dir)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir); // under the user's profile, which only they can read
        else
        {
            Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return dir;
    }

    /// <summary>Starts a new capture "prefix-yyyyMMdd-HHmmss-random.log" in <paramref name="dir"/>.</summary>
    public static CaptureFile Create(string? dir, string prefix, long capBytes)
    {
        dir = EnsureFolder(dir ?? DefaultFolder);
        var name = $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}-{Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(3)).ToLowerInvariant()}.log";
        return new CaptureFile(System.IO.Path.Combine(dir, name), capBytes);
    }

    private static FileStream CreateNew(string path)
    {
        // Created new (never through a planted link) and readable by this user only: received logs can hold anything.
        var o = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.ReadWrite | FileShare.Delete, BufferSize = 64 * 1024 };
        if (!OperatingSystem.IsWindows()) o.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, o);
    }

    /// <summary>Queues one line (a line break inside it must already be removed). Never blocks; false when dropped.</summary>
    public bool WriteLine(string line)
    {
        var bytes = line.Length * 2L + 1;
        if (Interlocked.Add(ref _pending, bytes) > MaxPendingBytes || !_queue.Writer.TryWrite(line))
        {
            Interlocked.Add(ref _pending, -bytes);
            Interlocked.Increment(ref _dropped);
            return false;
        }
        Interlocked.Increment(ref _lines);
        return true;
    }

    private async Task WriteLoop()
    {
        var buf = new byte[64 * 1024];
        try
        {
            while (await _queue.Reader.WaitToReadAsync())
            {
                while (_queue.Reader.TryRead(out var line))
                {
                    Interlocked.Add(ref _pending, -(line.Length * 2L + 1));
                    var max = Encoding.UTF8.GetMaxByteCount(line.Length) + 1;
                    if (buf.Length < max) buf = new byte[max];
                    var n = Encoding.UTF8.GetBytes(line, buf);
                    buf[n++] = (byte)'\n';
                    try
                    {
                        if (_size > 0 && _size + n > _cap) Rotate();
                        _out.Write(buf, 0, n);
                        _size += n;
                        Interlocked.Increment(ref _written);
                    }
                    catch (IOException e)
                    {
                        Error = e.Message; // disk full or the folder went away: keep receiving, count it as dropped
                        Interlocked.Increment(ref _dropped);
                    }
                }
                try
                {
                    _out.Flush(); // after each burst, so the live tail sees it at once
                }
                catch (IOException e)
                {
                    Error = e.Message;
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // closed while writing
        }
        finally
        {
            try { _out.Dispose(); } catch (IOException) { }
        }
    }

    private void Rotate()
    {
        _out.Dispose();
        try
        {
            File.Move(Path, RotatedPath, overwrite: true);
            _out = CreateNew(Path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // could not keep the old part (locked, or a file appeared under the new name): start the file over
            _out = new FileStream(Path, FileMode.Truncate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 64 * 1024);
        }
        _size = 0;
        Rotations++;
    }

    /// <summary>Waits until everything queued so far is on disk (for tests and for a clean stop).</summary>
    public async Task FlushAsync(TimeSpan? timeout = null)
    {
        var until = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (Written + Dropped < Lines && !_writer.IsCompleted && DateTime.UtcNow < until) await Task.Delay(10);
    }

    /// <summary>
    /// Stops taking lines; what is queued is still written and the file closed in the background (this waits a moment
    /// for that, never long: it runs when a tab closes, on the UI thread).
    /// </summary>
    public void Dispose()
    {
        if (_queue.Writer.TryComplete()) _writer.Wait(TimeSpan.FromMilliseconds(300));
    }
}
