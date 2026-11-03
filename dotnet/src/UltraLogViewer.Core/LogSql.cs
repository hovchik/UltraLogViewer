using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace UltraLogViewer.Core;

/// <summary>
/// SQL over a document's entries: an in-memory SQLite database with a <c>logs</c> table (every entry) and a
/// <c>view</c> table (the entries the current filter shows).
/// <para>
/// The table is filled lazily, on the first query, and only with the columns queries name: row, line, source, time and
/// level come from the index without reading the file, while logger, thread, message, text and the parsed fields need
/// the entries read and parsed (on all cores, through <see cref="FileSource.ForEachEntryBatch"/>), and are added when a
/// query first names them. Big files load the first <see cref="Cap"/> entries unless told otherwise. Everything is
/// inserted in one transaction with a prepared statement; a cancelled load rolls back to the previous table. When the
/// file grows the new entries are appended; when it is re-parsed or rotated the table is rebuilt.
/// </para>
/// <para>
/// <c>row</c> is not stored: it is a function of the stored base position and the document's current view, so a new
/// filter changes <c>row</c> and <c>view</c> without touching the table.
/// </para>
/// Queries are read-only: <see cref="SqlSchema.Guard"/> first, then an authorizer that allows only reading and
/// functions (no ATTACH, no writes, no PRAGMA, no load_extension), with extensions off and no attached databases.
/// </summary>
public sealed class LogSql : IDisposable
{
    public const int DefaultCap = 2_000_000;
    /// <summary>Entries loaded per hold of the document's queue, so filters and tail updates can run in between.</summary>
    internal int Slice { get; set; } = 1 << 18;
    internal int Batch { get; set; } = 4096;
    /// <summary>Tests: skip <see cref="SqlSchema.Guard"/> to check that the engine blocks on its own.</summary>
    internal bool SkipGuard { get; set; }
    /// <summary>A query (not the load) running longer than this is interrupted: a recursive CTE or a cross join can run for ever.</summary>
    internal TimeSpan TimeLimit { get; set; } = SqlSchema.TimeLimit;

    private readonly LogDocument _doc;
    private readonly SqliteConnection _db;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly object _sync = new();
    private List<SqlColumn>? _schema;
    /// <summary>Optional columns in the table now (names).</summary>
    private HashSet<string> _loadedCols = new(StringComparer.OrdinalIgnoreCase);
    private bool _built;
    private int _builtCap;
    private int[] _loadedPerSource = [];
    private int _loaded;
    /// <summary>Index changes since the last load: null = none; firstChanged per source; <see cref="_dirtyAll"/> = everything.</summary>
    private int[]? _dirty;
    private bool _dirtyAll;
    /// <summary>Cancelled by <see cref="Dispose"/>: stops a running load or query before the database closes.</summary>
    private readonly CancellationTokenSource _life = new();

    public LogSql(LogDocument doc)
    {
        _doc = doc;
        _db = new SqliteConnection("Data Source=:memory:");
        _db.Open();
        var h = _db.Handle!;
        // Nothing may be attached, and sorting temp data stays in memory (never in a temp file). The journal stays on
        // (in memory): a cancelled load rolls back.
        raw.sqlite3_limit(h, raw.SQLITE_LIMIT_ATTACHED, 0);
        Exec("PRAGMA temp_store = MEMORY");
        _db.CreateFunction<long?, long?>("ulv_row", b => b is { } x ? _doc.ViewRowOfBase(x) : null, isDeterministic: false);
        _db.CreateFunction<string?, string?, bool?>("regexp", (pattern, value) => pattern is null || value is null ? null : Re(pattern).IsMatch(value), isDeterministic: true);
        _db.CreateFunction<string?, string?, string?>("regexp_extract", (value, pattern) => Extract(value, pattern, null), isDeterministic: true);
        _db.CreateFunction<string?, string?, long?, string?>("regexp_extract", (value, pattern, group) => Extract(value, pattern, group), isDeterministic: true);
        doc.IndexChanged += OnIndexChanged;
    }

    /// <summary>Load at most this many entries (the first ones); 0 loads everything.</summary>
    public int Cap { get; set; } = DefaultCap;
    public int Loaded => _loaded;
    public int Total => _doc.BaseCount;
    public bool Capped => _built && _builtCap > 0 && _loaded < Total && _loaded >= _builtCap;
    /// <summary>The columns loaded so far besides the always-present ones.</summary>
    public IReadOnlyCollection<string> LoadedColumns => _loadedCols;

    private void OnIndexChanged(int[]? first)
    {
        lock (_sync)
        {
            if (first is null) _dirtyAll = true;
            else if (_dirty is null) _dirty = (int[])first.Clone();
            else
            {
                var had = _dirty.Length;
                if (had < first.Length) Array.Resize(ref _dirty, first.Length);
                for (var i = 0; i < first.Length; i++) _dirty[i] = i < had ? Math.Min(_dirty[i], first[i]) : first[i];
            }
        }
    }

    // ---------------------------------------------------------------- schema

    /// <summary>The table's columns: built-in ones and one per parsed field (field keys sampled from the entries).</summary>
    public async Task<IReadOnlyList<SqlColumn>> SchemaAsync(CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _life.Token);
        ct = linked.Token;
        await _lock.WaitAsync(ct);
        try
        {
            return await SchemaNowAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<List<SqlColumn>> SchemaNowAsync(CancellationToken ct)
    {
        lock (_sync)
            if (_dirtyAll) _schema = null;
        if (_schema is { } s) return s;
        var keys = await _doc.InQueue(() => FieldKeys(_doc.Sources), ct);
        return _schema = SqlSchema.Build(keys);
    }

    /// <summary>
    /// Field keys in order of first appearance: the format's columns, then the keys of parsed entries (every entry of
    /// a small file; the first 1000 and 20 blocks of 50 spread over a big one).
    /// </summary>
    internal static List<string> FieldKeys(IReadOnlyList<FileSource> sources)
    {
        var keys = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string k)
        {
            if (seen.Add(k)) keys.Add(k);
        }
        foreach (var src in sources)
        {
            foreach (var c in src.Format.Columns) Add(c.Key);
            var n = src.EntryCount;
            var ranges = new List<(int, int)>();
            if (n <= 2000) ranges.Add((0, n));
            else
            {
                ranges.Add((0, 1000));
                for (var b = 0; b < 20; b++)
                {
                    var start = (int)(1000 + (long)(n - 1050) * b / 19);
                    ranges.Add((start, start + 50));
                }
            }
            foreach (var (a, z) in ranges)
                foreach (var text in src.ReadEntries(a, Math.Min(n, z)))
                    foreach (var k in src.Format.Parse(text).Keys)
                        Add(k);
        }
        return keys;
    }

    // ----------------------------------------------------------------- query

    /// <summary>
    /// Runs a query, loading (or bringing up to date) the table first. Returns at most <paramref name="maxRows"/> rows.
    /// SQL errors come back in <see cref="SqlResult.Error"/>; a cancelled load or query in <see cref="SqlResult.Cancelled"/>.
    /// </summary>
    public async Task<SqlResult> QueryAsync(string sql, IProgress<double>? progress = null, CancellationToken ct = default, int maxRows = 5000)
    {
        if (!SkipGuard && SqlSchema.Guard(sql) is { } denied) return SqlResult.Fail(denied) with { Loaded = _loaded, Total = Total };
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _life.Token);
        ct = linked.Token;
        try
        {
            await _lock.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return Cancelled(0);
        }
        var clock = Stopwatch.StartNew();
        double loadMs = 0;
        try
        {
            var schema = await SchemaNowAsync(ct);
            var named = SqlSchema.Mentioned(sql, out var star);
            var need = schema.Where(c => c.Name is not ("row" or "line" or "source" or "time" or "time_iso" or "level") && (star || named.Contains(c.Name.ToLowerInvariant())))
                .Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            await EnsureLoadedAsync(schema, need, progress, ct);
            loadMs = clock.Elapsed.TotalMilliseconds;
            clock.Restart();
            var r = await Task.Run(() => Run(sql, maxRows, ct), CancellationToken.None);
            return r with { QueryMs = clock.Elapsed.TotalMilliseconds, LoadMs = loadMs, Loaded = _loaded, Total = Total, Capped = Capped };
        }
        catch (OperationCanceledException)
        {
            return Cancelled(loadMs);
        }
        finally
        {
            _lock.Release();
        }
    }

    private SqlResult Cancelled(double loadMs) => new([], [], false, null, 0, loadMs, _loaded, Total, Capped, Cancelled: true);

    private SqlResult Run(string sql, int maxRows, CancellationToken ct)
    {
        var h = _db.Handle!;
        raw.sqlite3_set_authorizer(h, Authorizer, null);
        using var interrupt = ct.Register(() => raw.sqlite3_interrupt(h));
        using var limit = new CancellationTokenSource(TimeLimit);
        using var overtime = limit.Token.Register(() => raw.sqlite3_interrupt(h));
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = sql;
            using var r = cmd.ExecuteReader();
            var cols = Enumerable.Range(0, r.FieldCount).Select(r.GetName).ToArray();
            var rows = new List<object?[]>();
            var truncated = false;
            while (r.Read())
            {
                if (rows.Count >= maxRows)
                {
                    truncated = true;
                    break;
                }
                var row = new object?[cols.Length];
                for (var i = 0; i < cols.Length; i++) row[i] = r.IsDBNull(i) ? null : r.GetValue(i);
                rows.Add(row);
            }
            return new SqlResult(cols, rows, truncated, null, 0, 0, 0, 0, false);
        }
        catch (SqliteException e)
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            if (limit.IsCancellationRequested) return SqlResult.Fail(SqlSchema.TimeLimitText(TimeLimit));
            return SqlResult.Fail(CleanError(e.Message));
        }
        finally
        {
            raw.sqlite3_set_authorizer(h, (strdelegate_authorizer?)null, null);
        }
    }

    /// <summary>"SQLite Error 1: 'no such column: x'." → "no such column: x".</summary>
    internal static string CleanError(string message)
    {
        var m = Regex.Match(message, @"^SQLite Error \d+: '(.*)'\.?$", RegexOptions.Singleline);
        return m.Success ? m.Groups[1].Value : message;
    }

    private static readonly strdelegate_authorizer Authorizer = Authorize;

    /// <summary>The user's statements may read and call functions, nothing else (and never load_extension).</summary>
    private static int Authorize(object user, int action, string param0, string param1, string dbName, string trigger) => action switch
    {
        raw.SQLITE_READ or raw.SQLITE_SELECT or raw.SQLITE_RECURSIVE => raw.SQLITE_OK,
        raw.SQLITE_FUNCTION => string.Equals(param1, "load_extension", StringComparison.OrdinalIgnoreCase) ? raw.SQLITE_DENY : raw.SQLITE_OK,
        _ => raw.SQLITE_DENY,
    };

    // ------------------------------------------------------------------ load

    private async Task EnsureLoadedAsync(List<SqlColumn> schema, HashSet<string> need, IProgress<double>? progress, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            await _doc.SyncAsync(ct);
            int[]? dirty;
            bool dirtyAll;
            lock (_sync)
            {
                dirty = _dirty;
                dirtyAll = _dirtyAll;
                _dirty = null;
                _dirtyAll = false;
            }
            var sources = _doc.Sources.Count;
            var full = !_built || dirtyAll || _builtCap != Cap || !need.IsSubsetOf(_loadedCols) || _loadedPerSource.Length != sources
                || dirty is not null && _doc.MergedNow;
            if (dirtyAll)
            {
                // Re-parsed (format change) or rotated: the fields may have changed.
                _schema = null;
                schema = await SchemaNowAsync(ct);
            }
            try
            {
                if (full)
                    await LoadAsync(schema, need.Concat(_loadedCols).Where(n => schema.Any(c => c.Name.Equals(n, StringComparison.OrdinalIgnoreCase))).ToHashSet(StringComparer.OrdinalIgnoreCase), null, progress, ct);
                else if (dirty is not null) await LoadAsync(schema, _loadedCols, dirty, progress, ct);
                else return;
            }
            catch
            {
                // The load rolled back: the table is as it was, so the changes still need loading next time.
                if (dirtyAll) lock (_sync) _dirtyAll = true;
                if (dirty is not null) OnIndexChanged(dirty);
                throw;
            }
            // The file changed while loading: catch up (a few times at most, a live file never stops growing).
            lock (_sync)
                if ((_dirty is null && !_dirtyAll) || attempt >= 2) return;
        }
    }

    /// <summary>
    /// Fills the table: from scratch (<paramref name="from"/> null) or from the first changed entry of a single source.
    /// One transaction; cancelling rolls back to the table as it was.
    /// </summary>
    private async Task LoadAsync(List<SqlColumn> schema, HashSet<string> cols, int[]? from, IProgress<double>? progress, CancellationToken ct)
    {
        var optional = schema.Where(c => cols.Contains(c.Name)).ToList();
        var cap = Cap;
        var total = _doc.BaseCount;
        var limit = cap > 0 ? Math.Min(cap, total) : total;
        var sources = _doc.Sources;
        var counts = new int[sources.Count];
        var starts = new int[sources.Count];
        for (var s = 0; s < sources.Count; s++)
        {
            counts[s] = _doc.EntriesBelow(s, limit);
            starts[s] = from is null ? 0 : Math.Min(s < from.Length ? from[s] : 0, Math.Min(counts[s], s < _loadedPerSource.Length ? _loadedPerSource[s] : 0));
        }
        var work = counts.Select((c, s) => (long)Math.Max(0, c - starts[s])).Sum();
        long done = 0;
        Exec("BEGIN");
        var committed = false;
        try
        {
            if (from is null)
            {
                Exec("DROP VIEW IF EXISTS view; DROP VIEW IF EXISTS logs; DROP TABLE IF EXISTS logs_data");
                var defs = new StringBuilder("base INTEGER PRIMARY KEY, src INTEGER, line INTEGER, time INTEGER, lvl INTEGER");
                foreach (var c in optional) defs.Append(", ").Append(Quote(c.Name)).Append(c.Field is null ? " TEXT" : "");
                Exec($"CREATE TABLE logs_data ({defs})");
                Exec(ViewSql(optional, sources));
                Exec("CREATE VIEW view AS SELECT * FROM logs WHERE row IS NOT NULL");
            }
            else
            {
                for (var s = 0; s < sources.Count; s++)
                    if (starts[s] < (s < _loadedPerSource.Length ? _loadedPerSource[s] : 0))
                        Exec($"DELETE FROM logs_data WHERE src = {s} AND base >= {_doc.BaseOfEntry(s, starts[s])}");
            }
            var insert = Prepare($"INSERT INTO logs_data VALUES ({string.Join(", ", Enumerable.Repeat("?", 5 + optional.Count))})");
            try
            {
                for (var s = 0; s < sources.Count; s++)
                {
                    for (var a = starts[s]; a < counts[s]; a += Slice)
                    {
                        var z = Math.Min(counts[s], a + Slice);
                        var si = s;
                        var a0 = a;
                        await _doc.InQueue(() =>
                        {
                            LoadRange(insert, si, a0, z, optional, n =>
                            {
                                done += n;
                                progress?.Report(work > 0 ? (double)done / work : 1);
                            }, ct);
                            return true;
                        }, ct);
                    }
                }
            }
            finally
            {
                insert.Dispose();
            }
            Exec("COMMIT");
            committed = true;
        }
        finally
        {
            if (!committed) Exec("ROLLBACK");
        }
        _built = true;
        _builtCap = cap;
        _loadedCols = optional.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _loadedPerSource = counts;
        _loaded = (int)Scalar("SELECT count(*) FROM logs_data");
    }

    /// <summary>The <c>logs</c> view: computed row, source name, ISO time and level name over the stored columns.</summary>
    private static string ViewSql(List<SqlColumn> optional, IReadOnlyList<FileSource> sources)
    {
        static string Lit(string s) => "'" + s.Replace("'", "''") + "'";
        var source = sources.Count == 1
            ? Lit(sources[0].Info().DisplayName)
            : "CASE src " + string.Concat(sources.Select((s, i) => $"WHEN {i} THEN {Lit(s.Info().DisplayName)} ")) + "END";
        var level = "CASE lvl " + string.Concat(Enumerable.Range(1, LevelNames.Count - 1).Select(l => $"WHEN {l} THEN '{LevelNames.Labels[l]}' ")) + "END";
        var sb = new StringBuilder("CREATE VIEW logs AS SELECT ulv_row(base) AS row, line, ").Append(source).Append(" AS source, time, ")
            .Append("CASE WHEN time IS NULL THEN NULL ELSE strftime('%Y-%m-%dT%H:%M:%fZ', time / 1000.0, 'unixepoch') END AS time_iso, ")
            .Append(level).Append(" AS level");
        foreach (var c in optional) sb.Append(", ").Append(Quote(c.Name));
        return sb.Append(" FROM logs_data").ToString();
    }

    private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    /// <summary>Inserts entries [a, z) of one source. Runs in the document's queue.</summary>
    private void LoadRange(sqlite3_stmt insert, int s, int a, int z, List<SqlColumn> optional, Action<int> advanced, CancellationToken ct)
    {
        var src = _doc.Sources[s];
        if (optional.Count == 0)
        {
            // Everything comes from the index: no need to read the file.
            for (var e = a; e < z; e++)
            {
                if ((e & 0xffff) == 0) ct.ThrowIfCancellationRequested();
                InsertRow(insert, s, e, src, null, 0);
                if (((e - a) & 0xfff) == 0xfff) advanced(0x1000);
            }
            advanced((z - a) & 0xfff);
            return;
        }
        var needParse = optional.Any(c => c.Name is not "text");
        src.ForEachEntryBatch(a, z, (first, texts) =>
        {
            // Thread pool: parse and pick the values; the commit below inserts them in entry order.
            var values = new object?[texts.Length * optional.Count];
            for (var i = 0; i < texts.Length; i++)
            {
                var text = texts[i];
                var parsed = needParse ? src.Format.Parse(text) : null;
                for (var c = 0; c < optional.Count; c++)
                    values[i * optional.Count + c] = ValueOf(optional[c], text, parsed);
            }
            return values;
        }, (first, n, values) =>
        {
            for (var i = 0; i < n; i++) InsertRow(insert, s, first + i, src, values, i * optional.Count);
            advanced(n);
            return true;
        }, ct, Batch);
    }

    private static object? ValueOf(SqlColumn c, string text, Dictionary<string, string>? parsed)
    {
        string? v;
        switch (c.Name)
        {
            case "text" when c.BuiltIn: return text;
            case "message" when c.BuiltIn: return Patterns.MessageOf(text, parsed!);
            case "logger" or "thread" when c.BuiltIn:
                return parsed!.TryGetValue(c.Name, out v) && v.Length > 0 ? v : null;
            default:
                return SqlSchema.Value(parsed!.TryGetValue(c.Field!, out v) ? v : null);
        }
    }

    private void InsertRow(sqlite3_stmt st, int s, int e, FileSource src, object?[]? values, int offset)
    {
        raw.sqlite3_bind_int64(st, 1, _doc.BaseOfEntry(s, e));
        raw.sqlite3_bind_int64(st, 2, s);
        raw.sqlite3_bind_int64(st, 3, src.EntryLines(e).First + 1);
        var t = src.TimeOf(e);
        if (t == Timestamps.None) raw.sqlite3_bind_null(st, 4);
        else raw.sqlite3_bind_int64(st, 4, t);
        var l = (int)src.LevelOf(e);
        if (l == 0) raw.sqlite3_bind_null(st, 5);
        else raw.sqlite3_bind_int64(st, 5, l);
        if (values is not null)
        {
            var n = raw.sqlite3_bind_parameter_count(st) - 5;
            for (var c = 0; c < n; c++)
            {
                var i = 6 + c;
                switch (values[offset + c])
                {
                    case null: raw.sqlite3_bind_null(st, i); break;
                    case long x: raw.sqlite3_bind_int64(st, i, x); break;
                    case double d: raw.sqlite3_bind_double(st, i, d); break;
                    case string str: raw.sqlite3_bind_text(st, i, str); break;
                }
            }
        }
        var rc = raw.sqlite3_step(st);
        raw.sqlite3_reset(st);
        if (rc != raw.SQLITE_DONE) throw new InvalidOperationException(raw.sqlite3_errmsg(_db.Handle).utf8_to_string());
    }

    // --------------------------------------------------------------- helpers

    private void Exec(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private long Scalar(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private sqlite3_stmt Prepare(string sql)
    {
        var rc = raw.sqlite3_prepare_v2(_db.Handle, sql, out var st);
        if (rc != raw.SQLITE_OK) throw new InvalidOperationException(raw.sqlite3_errmsg(_db.Handle).utf8_to_string());
        return st;
    }

    [ThreadStatic] private static Dictionary<string, Regex>? _regexCache;

    private static Regex Re(string pattern)
    {
        var cache = _regexCache ??= new Dictionary<string, Regex>();
        if (cache.TryGetValue(pattern, out var re)) return re;
        if (cache.Count > 64) cache.Clear();
        return cache[pattern] = new Regex(pattern, RegexOptions.CultureInvariant);
    }

    /// <summary>regexp_extract(value, pattern[, group]): the group's text (group 1 when the pattern has groups, else the match), or null.</summary>
    private static string? Extract(string? value, string? pattern, long? group)
    {
        if (value is null || pattern is null) return null;
        var re = Re(pattern);
        var m = re.Match(value);
        if (!m.Success) return null;
        var g = (int)(group ?? (re.GetGroupNumbers().Length > 1 ? 1 : 0));
        return g < m.Groups.Count && m.Groups[g].Success ? m.Groups[g].Value : null;
    }

    /// <summary>Stops what runs and closes the database once it has stopped.</summary>
    public void Dispose()
    {
        if (_life.IsCancellationRequested) return;
        _life.Cancel();
        _doc.IndexChanged -= OnIndexChanged;
        _ = Task.Run(async () =>
        {
            await _lock.WaitAsync();
            _db.Dispose();
        });
    }
}
