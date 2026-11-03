using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace UltraLogViewer.Core;

/// <summary>
/// A file on another machine, reached with the system OpenSSH client: <c>ssh://[user@]host[:port]/path</c> or the scp
/// form <c>[user@]host:/path</c>. The user's ~/.ssh/config, keys, agent and known_hosts apply, as with lnav.
/// </summary>
public sealed partial record RemoteTarget(string? User, string Host, int? Port, string Path)
{
    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.-]*$")]
    private static partial Regex HostName();

    [GeneratedRegex(@"^\[[0-9A-Fa-f:.%]+\]$")]
    private static partial Regex Ipv6();

    [GeneratedRegex(@"^(?:([^@\s/]+)@)?([A-Za-z0-9_][A-Za-z0-9_.-]*|\[[0-9A-Fa-f:.%]+\]):(/.*)$", RegexOptions.Singleline)]
    private static partial Regex ScpForm();

    /// <summary>True for text meant as a remote file (it may still be invalid: see <see cref="ParseOrThrow"/>).</summary>
    public static bool LooksRemote(string? s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        if (s.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)) return true;
        var m = ScpForm().Match(s);
        // "C:/logs/app.log" is a Windows path, not host "C"; an existing local file always wins.
        return m.Success && (m.Groups[1].Success || m.Groups[2].Value.Length > 1) && !File.Exists(s);
    }

    public static RemoteTarget? Parse(string? s)
    {
        try
        {
            return s is null || !LooksRemote(s) ? null : ParseOrThrow(s);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Parses and validates a remote file; the message of the exception says what is wrong.</summary>
    public static RemoteTarget ParseOrThrow(string s)
    {
        s = s.Trim();
        string? user;
        string host;
        int? port = null;
        string path;
        if (s.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
        {
            var rest = s[6..];
            var slash = rest.IndexOf('/');
            if (slash < 0) throw new FormatException("Add the path of the file after the host, e.g. ssh://host/var/log/app.log");
            var auth = rest[..slash];
            try
            {
                path = Uri.UnescapeDataString(rest[slash..]);
            }
            catch (UriFormatException)
            {
                throw new FormatException("The path has a bad %-escape");
            }
            var at = auth.LastIndexOf('@');
            user = at >= 0 ? Uri.UnescapeDataString(auth[..at]) : null;
            var hp = at >= 0 ? auth[(at + 1)..] : auth;
            var close = hp.LastIndexOf(']');
            var colon = hp.LastIndexOf(':');
            if (colon > close)
            {
                if (!int.TryParse(hp[(colon + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var p) || p is < 1 or > 65535)
                    throw new FormatException($"Bad port \"{hp[(colon + 1)..]}\"");
                port = p;
                hp = hp[..colon];
            }
            host = hp;
        }
        else
        {
            var m = ScpForm().Match(s);
            if (!m.Success) throw new FormatException("Write a remote file as ssh://user@host/path or user@host:/path");
            user = m.Groups[1].Success ? m.Groups[1].Value : null;
            host = m.Groups[2].Value;
            path = m.Groups[3].Value;
        }
        var t = new RemoteTarget(user, host, port, path);
        t.Validate();
        return t;
    }

    /// <summary>Rejects what could be read as an ssh option or break the remote command.</summary>
    public void Validate()
    {
        if (!HostName().IsMatch(Host) && !Ipv6().IsMatch(Host)) throw new FormatException($"Bad host name \"{Host}\"");
        if (User is not null && (User.Length == 0 || User.StartsWith('-') || User.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c is '/' or ':' or '@')))
            throw new FormatException($"Bad user name \"{User}\"");
        if (Port is < 1 or > 65535) throw new FormatException($"Bad port {Port}");
        if (!Path.StartsWith('/')) throw new FormatException("The remote path must be absolute (start with /)");
        if (Path.Length > 4096) throw new FormatException("The remote path is too long");
        RemoteShell.Check(Path);
    }

    /// <summary>The ssh destination: [user@]host (an IPv6 address without its brackets).</summary>
    public string Destination => (User is null ? "" : User + "@") + Host.Trim('[', ']');

    /// <summary>"host:path", the name shown for the file.</summary>
    public string Display => $"{Host}:{Path}";

    /// <summary>The canonical ssh:// URL (kept in recent files and workspaces).</summary>
    public string Url => "ssh://" + (User is null ? "" : Uri.EscapeDataString(User) + "@") + Host + (Port is null ? "" : ":" + Port)
        + Path.Replace("%", "%25", StringComparison.Ordinal).Replace(" ", "%20", StringComparison.Ordinal); // readable: only what the parser needs escaped

    public RemoteTarget WithPath(string path) => this with { Path = path };

    public override string ToString() => Url;
}

/// <summary>Building remote commands for the POSIX shell that sshd runs them with.</summary>
public static class RemoteShell
{
    /// <summary>Control characters (a newline would end the command) are refused, never escaped.</summary>
    public static void Check(string s)
    {
        foreach (var c in s)
            if (c < 0x20 || c == 0x7f) throw new FormatException("The remote path has a control character (such as a newline)");
    }

    /// <summary>Single-quotes a word: inside '…' nothing is special except the quote itself, written as '\''.</summary>
    public static string Quote(string s)
    {
        Check(s);
        return "'" + s.Replace("'", @"'\''", StringComparison.Ordinal) + "'";
    }

    public static bool HasGlob(string s) => s.IndexOfAny(['*', '?']) >= 0;

    /// <summary>Quotes everything except * and ?, so the remote shell expands only those.</summary>
    public static string QuoteGlob(string s)
    {
        Check(s);
        var sb = new StringBuilder();
        var start = 0;
        for (var i = 0; i <= s.Length; i++)
        {
            if (i < s.Length && s[i] is not ('*' or '?')) continue;
            if (i > start) sb.Append(Quote(s[start..i]));
            if (i < s.Length) sb.Append(s[i]);
            start = i + 1;
        }
        return sb.ToString();
    }

    /// <summary>Size in bytes: GNU stat, BSD stat, then wc.</summary>
    public static string SizeCommand(string path)
    {
        var q = Quote(path);
        return $"stat -L -c %s -- {q} 2>/dev/null || stat -L -f %z -- {q} 2>/dev/null || wc -c < {q}";
    }

    /// <summary>The file from byte <paramref name="offset"/> on, then every byte appended, across rotation (-F).</summary>
    public static string TailCommand(string path, long offset) => $"tail -c +{offset + 1} -F -- {Quote(path)}";

    /// <summary>The files a pattern such as /var/log/app/*.log matches, one per line.</summary>
    public static string GlobCommand(string pattern) => $"ls -1d -- {QuoteGlob(pattern)}";

    /// <summary>A folder's entries, one per line, folders ending in "/".</summary>
    public static string ListCommand(string dir) => $"ls -1Ap -- {Quote(dir)}";
}

public sealed record SshResult(int ExitCode, string Output, string Error);

/// <summary>Runs the system ssh client: always with an argument list (never a shell), in batch mode.</summary>
public static class Ssh
{
    /// <summary>Environment variable with the ssh program to use (tests point it at a fake one).</summary>
    public const string EnvVar = "ULV_SSH";

    /// <summary>The ssh program: the setting, else <see cref="EnvVar"/>, else "ssh" from the PATH.</summary>
    public static string Program(string? setting) =>
        !string.IsNullOrWhiteSpace(setting) ? setting.Trim() : Environment.GetEnvironmentVariable(EnvVar) is { Length: > 0 } e ? e : "ssh";

    /// <summary>
    /// The arguments for one remote command. BatchMode: never prompt (there is no terminal), fail instead; keepalives so
    /// a dead connection is noticed within a minute; "--" so nothing after it is read as an option.
    /// </summary>
    public static List<string> Args(RemoteTarget t, string command)
    {
        t.Validate();
        var a = new List<string> { "-n", "-T", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10", "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=3" };
        if (t.Port is { } p) a.AddRange(["-p", p.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        a.AddRange(["--", t.Destination, command]);
        return a;
    }

    public static ProcessStartInfo StartInfo(string program, RemoteTarget t, string command)
    {
        var psi = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = false, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in Args(t, command)) psi.ArgumentList.Add(a);
        return psi;
    }

    /// <summary>Runs a short command and collects its output (killed after <paramref name="timeout"/>).</summary>
    public static async Task<SshResult> RunAsync(string program, RemoteTarget t, string command, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        using var p = new Process { StartInfo = StartInfo(program, t, command) };
        try
        {
            p.Start();
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new IOException($"Could not start {program}: {e.Message}. Install the OpenSSH client or set its path in the settings.");
        }
        RemoteFile.Track(p);
        try
        {
            var o = p.StandardOutput.ReadToEndAsync(ct);
            var e = p.StandardError.ReadToEndAsync(ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
            try
            {
                await p.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                RemoteFile.Kill(p);
                if (ct.IsCancellationRequested) throw;
                throw new IOException($"{t.Host}: no answer in time");
            }
            return new SshResult(p.ExitCode, await o, await e);
        }
        finally
        {
            RemoteFile.Kill(p);
            RemoteFile.Untrack(p);
        }
    }

    /// <summary>A message for a failed command: what went wrong, and what to do about a login that needs a person.</summary>
    public static string Explain(RemoteTarget t, int exitCode, string stderr)
    {
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.StartsWith("Warning: Permanently added", StringComparison.Ordinal)).ToList();
        var text = string.Join(" ", lines);
        if (text.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) || text.Contains("passphrase", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Too many authentication failures", StringComparison.OrdinalIgnoreCase))
            return $"{t.Host}: ssh needs a password or passphrase, which it cannot ask for here. Load your key into ssh-agent (ssh-add) or set up key login for {t.Destination}, then try again.";
        if (text.Contains("Host key verification failed", StringComparison.OrdinalIgnoreCase) || text.Contains("REMOTE HOST IDENTIFICATION HAS CHANGED", StringComparison.Ordinal))
            return $"{t.Host}: the host key is unknown or has changed. Connect once in a terminal (ssh {t.Destination}) to check and accept it.";
        if (text.Length == 0) return exitCode == 255 ? $"{t.Host}: the connection failed" : $"{t.Host}: the command failed (exit code {exitCode})";
        return $"{t.Host}: {(text.Length > 300 ? text[..300] + "…" : text)}";
    }
}

public enum RemoteState
{
    Connecting,
    Copying,
    Live,
    Reconnecting,
    Failed,
    Closed,
}

/// <summary>What the Sources list shows for a remote file.</summary>
public sealed record RemoteStatus(string Url, string Display, RemoteState State, string Error, long Received, long Expected, int Attempt)
{
    /// <summary>"remote · live", "remote · copying 45%", "remote · reconnecting (3): …".</summary>
    public string Text => "remote · " + State switch
    {
        RemoteState.Copying => Expected > 0 ? $"copying {Math.Min(100, Received * 100 / Expected)}%" : "copying",
        RemoteState.Live => "live",
        RemoteState.Connecting => "connecting",
        RemoteState.Reconnecting => $"reconnecting ({Attempt})" + (Error.Length > 0 ? ": " + Error : ""),
        RemoteState.Failed => "failed" + (Error.Length > 0 ? ": " + Error : ""),
        _ => "closed",
    };
}

public sealed class RemoteOptions
{
    /// <summary>The ssh program (null: see <see cref="Ssh.Program"/>).</summary>
    public string? SshProgram { get; init; }
    /// <summary>Folder of the local copies (per user, not the shared temp folder).</summary>
    public required string CacheDir { get; init; }
    /// <summary>First wait before reconnecting; it doubles up to <see cref="MaxRetryMs"/>.</summary>
    public int RetryMs { get; init; } = 1000;
    public int MaxRetryMs { get; init; } = 30_000;
}

/// <summary>
/// A remote file copied into a local file and kept up to date: <c>tail -c +1 -F</c> streams the whole file and then what
/// is appended, so the normal live tail of the local copy shows new lines. When the connection drops it reconnects
/// with backoff and goes on from the bytes it has (from 0 if the file got smaller); when the remote file is rotated or
/// truncated it starts over from 0. All sessions are listed so a closed tab or the app's exit kills their processes.
/// </summary>
public sealed class RemoteFile : IDisposable
{
    private static readonly ConcurrentDictionary<string, RemoteFile> ByLocal = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<Process, byte> Running = new();
    private static int _exitHooked;

    private readonly RemoteOptions _opts;
    private readonly string _program;
    private readonly CancellationTokenSource _cts = new();
    private FileStream? _out;
    private Process? _proc;
    private volatile bool _rotated;
    private long _received;
    private long _expected;
    private int _lastExit;
    private string _error = "";
    private Task _loop = Task.CompletedTask;

    private RemoteFile(RemoteTarget target, RemoteOptions opts)
    {
        Target = target;
        _opts = opts;
        _program = Ssh.Program(opts.SshProgram);
        LocalPath = LocalPathFor(target, opts.CacheDir);
    }

    public RemoteTarget Target { get; }
    /// <summary>The local copy that the viewer opens.</summary>
    public string LocalPath { get; }
    public RemoteState State { get; private set; } = RemoteState.Connecting;
    public long Received => Interlocked.Read(ref _received);
    public int Attempt { get; private set; }
    /// <summary>Ssh processes started so far (tests: a reconnect starts another).</summary>
    public int Starts { get; private set; }
    public RemoteStatus Status => new(Target.Url, Target.Display, State, _error, Received, Interlocked.Read(ref _expected), Attempt);

    /// <summary>The same local name for the same remote file every time, so bookmarks and pins find it again.</summary>
    public static string LocalPathFor(RemoteTarget t, string dir)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(t.Url)))[..12].ToLowerInvariant();
        var name = new string(System.IO.Path.GetFileName(t.Path).Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray());
        return System.IO.Path.Combine(dir, $"{SafeHost(t.Host)}-{hash}-{(name.Length == 0 ? "file" : name)}");
    }

    private static string SafeHost(string h) => new(h.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '_').ToArray());

    /// <summary>The session whose local copy is <paramref name="localPath"/>, if any.</summary>
    public static RemoteFile? Find(string localPath) => ByLocal.TryGetValue(localPath, out var r) ? r : null;

    public static IReadOnlyCollection<RemoteFile> All => ByLocal.Values.ToList();

    /// <summary>"host:path" for a local copy, else null.</summary>
    public static string? DisplayNameOf(string localPath) => ByLocal.TryGetValue(localPath, out var r) ? r.Target.Display : null;

    /// <summary>
    /// Starts copying a remote file (or joins the session already copying it) and returns once there is enough to
    /// detect the format: the whole file when it is small, else its first 256 KB. Throws when the file cannot be read.
    /// </summary>
    public static async Task<RemoteFile> OpenAsync(RemoteTarget target, RemoteOptions opts, CancellationToken ct = default)
    {
        HookExit();
        var local = LocalPathFor(target, opts.CacheDir);
        if (ByLocal.TryGetValue(local, out var existing) && existing.State is not (RemoteState.Closed or RemoteState.Failed)) return existing;
        var r = new RemoteFile(target, opts);
        await r.StartAsync(ct);
        return r;
    }

    private async Task StartAsync(CancellationToken ct)
    {
        _expected = await ProbeSizeAsync(ct);
        Directory.CreateDirectory(_opts.CacheDir);
        var create = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.ReadWrite | FileShare.Delete, BufferSize = 0 };
        if (!OperatingSystem.IsWindows()) create.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        _out = new FileStream(LocalPath, create);
        if (ByLocal.TryGetValue(LocalPath, out var old)) old.Dispose(); // a failed session of the same file
        ByLocal[LocalPath] = this;
        State = _expected == 0 ? RemoteState.Live : RemoteState.Copying;
        _loop = Task.Run(Loop);
        var want = Math.Min(_expected, 256 * 1024);
        var deadline = Environment.TickCount64 + 20_000;
        while (Received < want && State is RemoteState.Copying or RemoteState.Connecting && Environment.TickCount64 < deadline)
            await Task.Delay(15, ct);
        if (State == RemoteState.Failed) throw new IOException(_error);
    }

    private async Task<long> ProbeSizeAsync(CancellationToken ct)
    {
        var r = await Ssh.RunAsync(_program, Target, RemoteShell.SizeCommand(Target.Path), TimeSpan.FromSeconds(30), ct);
        if (r.ExitCode != 0 || !long.TryParse(r.Output.Trim(), out var size))
            throw new IOException(Ssh.Explain(Target, r.ExitCode, r.Error.Length > 0 ? r.Error : r.Output));
        return size;
    }

    private async Task Loop()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            var stderr = new StringBuilder();
            var started = Environment.TickCount64;
            try
            {
                await StreamOnceAsync(stderr, ct);
            }
            catch (OperationCanceledException)
            {
                break; // closed
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                stderr.Append(e.Message);
            }
            if (ct.IsCancellationRequested) break;
            if (_rotated)
            {
                // Rotated or truncated: the local copy starts over, so the viewer re-reads it from the top.
                _rotated = false;
                Restart();
                continue;
            }
            if (Environment.TickCount64 - started > 10_000) Attempt = 0; // it ran for a while: a fresh drop
            Attempt++;
            _error = Ssh.Explain(Target, _lastExit, stderr.ToString());
            State = RemoteState.Reconnecting;
            while (!ct.IsCancellationRequested)
            {
                var delay = (int)Math.Min(_opts.MaxRetryMs, _opts.RetryMs * Math.Pow(2, Math.Min(Attempt - 1, 16)));
                try
                {
                    await Task.Delay(delay, ct);
                    var size = await ProbeSizeAsync(ct);
                    Interlocked.Exchange(ref _expected, size);
                    if (size < Received) Restart(); // replaced by a smaller file while we were away
                    break;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException e)
                {
                    Attempt++;
                    _error = e.Message;
                }
            }
        }
    }

    private void Restart()
    {
        _out!.SetLength(0);
        _out.Position = 0;
        Interlocked.Exchange(ref _received, 0);
        Interlocked.Exchange(ref _expected, 0); // the new file's size is not known: no copy progress
    }

    private async Task StreamOnceAsync(StringBuilder stderr, CancellationToken ct)
    {
        var p = new Process { StartInfo = Ssh.StartInfo(_program, Target, RemoteShell.TailCommand(Target.Path, Received)) };
        _lastExit = 255;
        p.Start();
        Starts++;
        _proc = p;
        Track(p);
        try
        {
            // Still running after a moment: connected (a failed login or a missing host ends ssh sooner).
            _ = Task.Delay(1500, ct).ContinueWith(_ =>
            {
                if (_proc == p && !_cts.IsCancellationRequested && State == RemoteState.Reconnecting && IsRunning(p)) State = Received < Interlocked.Read(ref _expected) ? RemoteState.Copying : RemoteState.Live;
            }, TaskScheduler.Default);
            var errTask = Task.Run(async () =>
            {
                while (await p.StandardError.ReadLineAsync(ct) is { } line)
                {
                    lock (stderr) stderr.AppendLine(line);
                    if (IsRotation(line))
                    {
                        _rotated = true;
                        Kill(p);
                    }
                }
            }, ct);
            var buf = new byte[64 * 1024];
            var stdout = p.StandardOutput.BaseStream;
            int n;
            while ((n = await stdout.ReadAsync(buf, ct)) > 0)
            {
                if (_rotated) break; // what follows belongs to the new file: it is fetched again from 0
                _out!.Write(buf, 0, n); // unbuffered, so the live tail of the local copy sees it at once
                var got = Interlocked.Add(ref _received, n);
                State = got < Interlocked.Read(ref _expected) ? RemoteState.Copying : RemoteState.Live;
            }
            Kill(p);
            await p.WaitForExitAsync(CancellationToken.None);
            _lastExit = p.ExitCode;
            try
            {
                await errTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
        finally
        {
            Kill(p); // also when the session was closed while reading: never leave ssh running
            Untrack(p);
            p.Dispose();
        }
    }

    /// <summary>GNU and busybox tail -F: the followed name now has another (or a shorter) file.</summary>
    public static bool IsRotation(string stderrLine) =>
        stderrLine.StartsWith("tail:", StringComparison.Ordinal)
        && (stderrLine.Contains("truncated", StringComparison.Ordinal) || stderrLine.Contains("has been replaced", StringComparison.Ordinal) || stderrLine.Contains("has appeared", StringComparison.Ordinal));

    internal static void Track(Process p) => Running[p] = 0;
    internal static void Untrack(Process p) => Running.TryRemove(p, out _);

    /// <summary>Ssh processes still running (tests: none after a tab is closed).</summary>
    public static int RunningCount => Running.Keys.Count(IsRunning);

    private static bool IsRunning(Process p)
    {
        try
        {
            return !p.HasExited;
        }
        catch (InvalidOperationException)
        {
            return false; // disposed
        }
    }

    internal static void Kill(Process p)
    {
        try
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // already gone
        }
    }

    private static void HookExit()
    {
        if (Interlocked.Exchange(ref _exitHooked, 1) == 0) AppDomain.CurrentDomain.ProcessExit += (_, _) => CloseAll();
    }

    /// <summary>Closes the sessions whose local copies no open tab uses any more.</summary>
    public static void Prune(IEnumerable<string> inUse)
    {
        var used = new HashSet<string>(inUse, StringComparer.Ordinal);
        foreach (var r in ByLocal.Values)
            if (!used.Contains(r.LocalPath)) r.Dispose();
    }

    /// <summary>The app is closing: every ssh process goes.</summary>
    public static void CloseAll()
    {
        foreach (var r in ByLocal.Values) r.Dispose();
        foreach (var p in Running.Keys) Kill(p);
    }

    /// <summary>Waits until the background loop has stopped (tests).</summary>
    public Task Stopped => _loop;

    public void Dispose()
    {
        if (State == RemoteState.Closed) return;
        State = RemoteState.Closed;
        ByLocal.TryRemove(new KeyValuePair<string, RemoteFile>(LocalPath, this));
        _cts.Cancel();
        if (_proc is { } p) Kill(p);
        try
        {
            _loop.Wait(2000);
        }
        catch (AggregateException)
        {
        }
        _out?.Dispose();
        try
        {
            File.Delete(LocalPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
