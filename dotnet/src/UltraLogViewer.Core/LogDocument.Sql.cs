namespace UltraLogViewer.Core;

/// <summary>What <see cref="LogSql"/> needs from a document: base positions, view rows and its queue.</summary>
public sealed partial class LogDocument
{
    /// <summary>
    /// Told (with the queue held) when the index changed: the first changed entry per source, or null when everything
    /// changed (re-parsed, files added). The SQL table rebuilds from there on its next query.
    /// </summary>
    internal event Action<int[]?>? IndexChanged;

    private void NotifyIndexChanged(int[]? firstChanged) => IndexChanged?.Invoke(firstChanged);

    /// <summary>Runs <paramref name="fn"/> on the thread pool in this document's queue: no index change or filter runs meanwhile.</summary>
    internal Task<T> InQueue<T>(Func<T> fn, CancellationToken ct) => Serial(() => Task.Run(fn, ct));

    internal bool MergedNow => _merge is not null;

    /// <summary>Base position of an entry in the merge this document is synced to.</summary>
    internal int BaseOfEntry(int src, int entry) => BaseForEntry(src, entry, _merge);

    /// <summary>How many entries of a source sit at base positions below <paramref name="limit"/>.</summary>
    internal int EntriesBelow(int src, int limit)
    {
        var count = _sources[src].EntryCount;
        if (_merge is not { } m) return Math.Min(count, limit);
        var pos = m.BasePos[src];
        return Search.LowerBound(0, Math.Min(count, pos.Length), i => pos[i] >= limit);
    }

    /// <summary>View row of a base position, or null when the current view does not show it.</summary>
    internal long? ViewRowOfBase(long b)
    {
        var v = CurView;
        if (v is null) return b >= 0 && b < BaseCount ? b : null;
        if (b < 0 || b > int.MaxValue) return null;
        var i = Array.BinarySearch(v.Rows, (int)b);
        return i >= 0 ? i : null;
    }
}
