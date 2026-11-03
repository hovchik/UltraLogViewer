namespace UltraLogViewer.Core;

/// <summary>Several hosts, patterns and remote folders: what "Multi-host tail" and the remote browser need.</summary>
public static class RemoteFiles
{
    /// <summary>"web1, ops@web2:2222 web3" with one path: a target per host (user and port optional). Throws on a bad host.</summary>
    public static List<RemoteTarget> ForHosts(string hosts, string path)
    {
        var list = new List<RemoteTarget>();
        foreach (var h in hosts.Split(new[] { ',', ' ', '\t', '\n', '\r', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var at = h.LastIndexOf('@');
            var user = at >= 0 ? h[..at] : null;
            var hp = at >= 0 ? h[(at + 1)..] : h;
            int? port = null;
            var colon = hp.LastIndexOf(':');
            if (colon > hp.LastIndexOf(']'))
            {
                if (!int.TryParse(hp[(colon + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var p))
                    throw new FormatException($"Bad port in \"{h}\"");
                port = p;
                hp = hp[..colon];
            }
            var t = new RemoteTarget(user, hp, port, path);
            t.Validate();
            if (!list.Contains(t)) list.Add(t);
        }
        if (list.Count == 0) throw new FormatException("Enter at least one host");
        return list;
    }

    /// <summary>The files a path with * or ? matches on its host (expanded there by ls); a plain path as it is.</summary>
    public static async Task<List<RemoteTarget>> ExpandAsync(RemoteTarget pattern, RemoteOptions opts, CancellationToken ct = default)
    {
        pattern.Validate();
        if (!RemoteShell.HasGlob(pattern.Path)) return [pattern];
        var r = await Ssh.RunAsync(Ssh.Program(opts.SshProgram), pattern, RemoteShell.GlobCommand(pattern.Path), TimeSpan.FromSeconds(30), ct);
        if (r.ExitCode != 0) throw new IOException(Ssh.Explain(pattern, r.ExitCode, r.Error));
        var list = new List<RemoteTarget>();
        foreach (var line in r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = pattern.WithPath(line.TrimEnd('\r'));
            try
            {
                t.Validate();
                list.Add(t);
            }
            catch (FormatException)
            {
                // a name that cannot be passed on safely: left out
            }
        }
        if (list.Count == 0) throw new IOException($"{pattern.Host}: no file matches {pattern.Path}");
        return list;
    }

    /// <summary>The entries of a remote folder ("name", or "name/" for a folder), for the Browse button.</summary>
    public static async Task<List<string>> ListAsync(RemoteTarget dir, string? sshProgram, CancellationToken ct = default)
    {
        var r = await Ssh.RunAsync(Ssh.Program(sshProgram), dir, RemoteShell.ListCommand(dir.Path), TimeSpan.FromSeconds(30), ct);
        if (r.ExitCode != 0) throw new IOException(Ssh.Explain(dir, r.ExitCode, r.Error));
        return r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0 && !l.Any(char.IsControl)).ToList();
    }

    /// <summary>A folder and an entry name ("..": the parent folder).</summary>
    public static string Join(string dir, string name)
    {
        if (name == "..")
        {
            var t = dir.TrimEnd('/');
            var i = t.LastIndexOf('/');
            return i <= 0 ? "/" : t[..i];
        }
        return (dir.EndsWith('/') ? dir : dir + "/") + name.TrimEnd('/');
    }
}
