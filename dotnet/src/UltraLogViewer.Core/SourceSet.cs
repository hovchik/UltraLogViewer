namespace UltraLogViewer.Core;

/// <summary>
/// The indexed files behind one or more documents. A tab and its duplicates or split panes are documents over the same
/// set: each has its own filter, view and search, while the files are indexed, merged and tailed once. The set is
/// reference counted by the documents attached to it and closes its files when the last one is disposed.
/// <para>
/// Anything that changes the index (tail refresh, re-parsing, adding files) takes the set's gate and then every
/// attached document's queue, in attach order, so it never runs while a document is reading entries for a filter, a
/// group or a search; the documents are told what changed and bring their verdicts up to date in their own queue.
/// </para>
/// </summary>
public sealed class SourceSet
{
    internal sealed record Merge(ushort[] Src, int[] Ent, int[][] BasePos);

    internal readonly List<FileSource> List = [];
    private readonly List<LogDocument> _docs = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _closed;
    private int _version;

    internal Merge? Merged { get; private set; }

    public IReadOnlyList<FileSource> Sources => List;
    public bool Indexing { get; internal set; }
    public double Progress { get; internal set; }
    /// <summary>Documents (tabs, split panes) reading these files.</summary>
    public int RefCount
    {
        get
        {
            lock (_docs) return _docs.Count;
        }
    }
    public bool Closed => _closed;
    /// <summary>
    /// Counts the changes to the index. A document remembers the version it last synced to, so one attached while a
    /// change was running (and so not told about it) still notices on its next sync. Bumped after the merge is rebuilt.
    /// </summary>
    internal int Version => Volatile.Read(ref _version);

    internal void Attach(LogDocument doc)
    {
        lock (_docs)
        {
            if (_closed) throw new ObjectDisposedException(nameof(SourceSet));
            _docs.Add(doc);
        }
    }

    /// <summary>Detaches a document; the last one out closes the files.</summary>
    internal void Detach(LogDocument doc)
    {
        lock (_docs)
        {
            if (!_docs.Remove(doc) || _docs.Count > 0) return;
            _closed = true;
        }
        FileSource[] all;
        lock (List) all = [.. List];
        foreach (var s in all) s.Dispose();
    }

    /// <summary>
    /// Runs <paramref name="change"/> with the index to itself: no attached document is scanning entries meanwhile.
    /// Then each document gets <paramref name="apply"/> (still under the lock), which tells it what changed. The version
    /// moves only when <paramref name="changed"/> says the files did change: a document that sees a new version starts
    /// over, so a quiet poll must not count.
    /// </summary>
    internal async Task<T> ExclusiveAsync<T>(Func<Task<T>> change, Action<LogDocument, T> apply, Func<T, bool>? changed = null)
    {
        await _gate.WaitAsync();
        LogDocument[] docs;
        lock (_docs) docs = [.. _docs];
        var held = new List<LogDocument>(docs.Length);
        try
        {
            foreach (var d in docs)
            {
                await d.Queue.WaitAsync();
                held.Add(d);
            }
            var r = await change();
            if (changed?.Invoke(r) ?? true) Interlocked.Increment(ref _version);
            foreach (var d in docs) apply(d, r);
            return r;
        }
        finally
        {
            foreach (var d in held) d.Queue.Release();
            _gate.Release();
        }
    }

    /// <summary>k-way merge of all sources by timestamp (stable: ties keep source order).</summary>
    internal void RebuildMerge()
    {
        if (List.Count < 2)
        {
            Merged = null;
            return;
        }
        var counts = List.Select(s => s.EntryCount).ToArray();
        var total = counts.Sum();
        var src = new ushort[total];
        var ent = new int[total];
        var basePos = counts.Select(c => new int[c]).ToArray();
        var cur = new int[List.Count];
        for (var n = 0; n < total; n++)
        {
            var best = -1;
            var bestT = long.MaxValue;
            for (var s = 0; s < List.Count; s++)
            {
                if (cur[s] >= counts[s]) continue;
                var t = List[s].TimeOf(cur[s]); // None (MinValue) sorts first
                if (best < 0 || t < bestT)
                {
                    best = s;
                    bestT = t;
                }
            }
            basePos[best][cur[best]] = n;
            src[n] = (ushort)best;
            ent[n] = cur[best]++;
        }
        Merged = new Merge(src, ent, basePos);
    }

    /// <summary>
    /// Polls every file once for appended or rotated data, for all documents over them. Returns the first changed entry
    /// per source (0 after a rotation, the entry count when unchanged), or null when nothing changed. Each document
    /// picks the change up with <see cref="LogDocument.SyncAsync"/>.
    /// </summary>
    public Task<int[]?> RefreshAsync()
    {
        if (Indexing || _closed) return Task.FromResult<int[]?>(null);
        return ExclusiveAsync<int[]?>(async () =>
        {
            if (_closed) return null;
            var first = new int[List.Count];
            var changed = false;
            for (var i = 0; i < List.Count; i++)
            {
                var r = await List[i].RefreshAsync();
                first[i] = !r.Changed ? List[i].EntryCount : r.Reset ? 0 : r.FirstChangedEntry;
                changed |= r.Changed;
            }
            if (!changed) return null;
            RebuildMerge();
            return first;
        }, (d, first) =>
        {
            if (first is not null) d.SourcesChanged(first);
        }, first => first is not null);
    }

    /// <summary>Re-parses every file with another format; every document filters again from scratch.</summary>
    internal Task ReformatAsync(string? formatId, IReadOnlyList<CustomFormat>? custom) => ExclusiveAsync(async () =>
    {
        foreach (var s in List) await s.SetFormatAsync(formatId, custom);
        RebuildMerge();
        return true;
    }, (d, _) => d.SourcesChanged(null));

    /// <summary>After files were added or left out: rebuilds the merge and makes every document filter again.</summary>
    internal Task RebuiltAsync() => ExclusiveAsync(() =>
    {
        RebuildMerge();
        return Task.FromResult(true);
    }, (d, _) => d.SourcesChanged(null));
}
