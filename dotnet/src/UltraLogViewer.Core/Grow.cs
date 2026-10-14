namespace UltraLogViewer.Core;

/// <summary>
/// Growable array for the index of a multi-gigabyte file (tens of millions of numbers).
/// Writers copy before publishing a larger count, so a reader that reads <see cref="Count"/>
/// first always sees valid data for every index below it.
/// </summary>
public sealed class Grow<T> where T : struct
{
    private T[] _data;
    private int _count;

    public Grow(int capacity = 4096) => _data = new T[Math.Max(16, capacity)];

    public int Count => Volatile.Read(ref _count);

    public T this[int i]
    {
        get => _data[i];
        set => _data[i] = value;
    }

    public void Add(T v)
    {
        if (_count == _data.Length)
        {
            var next = new T[_data.Length * 2];
            Array.Copy(_data, next, _count);
            _data = next;
        }
        _data[_count] = v;
        Volatile.Write(ref _count, _count + 1);
    }

    public void Truncate(int n)
    {
        if (n < _count) Volatile.Write(ref _count, n);
    }

    public T Last => _data[_count - 1];

    /// <summary>The used part, without copying.</summary>
    public ReadOnlySpan<T> Span => _data.AsSpan(0, Count);
}

public static class Search
{
    /// <summary>First index in [lo, hi) where <paramref name="pred"/> is true, assuming it is monotone.</summary>
    public static int LowerBound(int lo, int hi, Func<int, bool> pred)
    {
        while (lo < hi)
        {
            var mid = (int)((uint)(lo + hi) >> 1);
            if (pred(mid)) hi = mid;
            else lo = mid + 1;
        }
        return lo;
    }
}
