namespace UltraLogViewer.Core;

/// <summary>
/// The ordered pipeline behind filtering, search, grouping, patterns, field stats and export (indexing uses the same
/// idea with pooled buffers, see FileSource.IndexFrom): numbered work items run on the thread pool, a few per core at a
/// time, and their results are consumed on the calling thread strictly in order. Whatever the consumer builds therefore
/// comes out exactly as a sequential pass would build it, only faster.
/// </summary>
public static class InOrder
{
    /// <summary>
    /// Runs <paramref name="work"/>(i) for i in [0, count) in parallel and <paramref name="commit"/>(i, result) on this
    /// thread in order of i; commit returns false to stop. <paramref name="work"/> must be thread safe. With
    /// <paramref name="parallel"/> false everything runs on this thread (the reference the tests compare against).
    /// </summary>
    public static void Run<T>(int count, Func<int, T> work, Func<int, T, bool> commit, CancellationToken ct = default, bool parallel = true)
    {
        if (count <= 1 || !parallel || Environment.ProcessorCount == 1)
        {
            // Not worth a thread hop (e.g. a few new entries in tail mode).
            for (var i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (!commit(i, work(i))) return;
            }
            return;
        }
        var maxInFlight = Math.Max(2, Environment.ProcessorCount * 2);
        var inFlight = new Queue<Task<T>>();
        try
        {
            var next = 0;
            while (next < count || inFlight.Count > 0)
            {
                while (next < count && inFlight.Count < maxInFlight)
                {
                    ct.ThrowIfCancellationRequested();
                    var i = next++;
                    inFlight.Enqueue(Task.Run(() => work(i), ct));
                }
                var done = next - inFlight.Count;
                if (!commit(done, inFlight.Dequeue().GetAwaiter().GetResult())) return;
            }
        }
        finally
        {
            // Stopped, cancelled or failed: let the items still running finish first, so none of them outlives the
            // call (their results are dropped).
            foreach (var task in inFlight)
            {
                try { task.Wait(CancellationToken.None); } catch (AggregateException) { }
            }
        }
    }
}
