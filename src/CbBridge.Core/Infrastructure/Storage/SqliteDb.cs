using static CbBridge.Core.Infrastructure.Storage.SqliteNative;

namespace CbBridge.Core.Infrastructure.Storage;

/// <summary>One row of a query result, read by column name.</summary>
public sealed class SqliteRow(IReadOnlyDictionary<string, object?> values, object? first)
{
    /// <summary>Value of the first column (for COUNT(*) and similar).</summary>
    public object? First => first;
    public object? this[string name] => values.TryGetValue(name, out var v) ? v : null;
    public string? Str(string name) => this[name]?.ToString();
    public long Long(string name) => this[name] is { } v ? Convert.ToInt64(v) : 0;
    public long? LongOrNull(string name) => this[name] is { } v ? Convert.ToInt64(v) : null;
    public double Double(string name) => this[name] is { } v ? Convert.ToDouble(v) : 0;
    public bool Bool(string name) => Long(name) != 0;
    public DateTimeOffset? Date(string name) => DateTimeOffset.TryParse(Str(name), out var d) ? d : null;
}

/// <summary>
/// A small, thread-safe SQLite connection: one connection, every call serialised, WAL journal so readers
/// (e.g. a backup) don't block. Parameters are named "$name". Errors throw <see cref="SqliteException"/>.
/// </summary>
public sealed class SqliteDb : IDisposable
{
    private readonly object _gate = new();
    private IntPtr _db;

    public string Path { get; }
    public static string LibraryVersion => Text(sqlite3_libversion()) ?? "?";

    public SqliteDb(string path)
    {
        Path = path;
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var rc = sqlite3_open_v2(Utf8Z(path), out _db, OPEN_READWRITE | OPEN_CREATE | OPEN_FULLMUTEX, IntPtr.Zero);
        if (rc != OK)
        {
            var msg = Text(sqlite3_errmsg(_db));
            sqlite3_close_v2(_db);
            throw new SqliteException($"cannot open {path}: {msg} ({rc})");
        }
        sqlite3_busy_timeout(_db, 5000);
        ExecScript("PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA synchronous=NORMAL;");
    }

    /// <summary>Several statements, no parameters (schema, pragmas).</summary>
    public void ExecScript(string sql)
    {
        lock (_gate)
        {
            if (sqlite3_exec(_db, Utf8Z(sql), IntPtr.Zero, IntPtr.Zero, out var err) != OK)
            {
                var msg = Text(err);
                sqlite3_free(err);
                throw new SqliteException(msg ?? "exec failed");
            }
        }
    }

    /// <summary>One statement; returns the number of changed rows.</summary>
    public int Execute(string sql, params (string Name, object? Value)[] args)
    {
        lock (_gate)
        {
            var stmt = Prepare(sql, args);
            try
            {
                int rc;
                while ((rc = sqlite3_step(stmt)) == ROW) { }
                if (rc != DONE) throw Error("step", sql);
                return sqlite3_changes(_db);
            }
            finally { sqlite3_finalize(stmt); }
        }
    }

    public List<T> Query<T>(string sql, Func<SqliteRow, T> map, params (string Name, object? Value)[] args)
    {
        lock (_gate)
        {
            var stmt = Prepare(sql, args);
            try
            {
                var cols = sqlite3_column_count(stmt);
                var names = Enumerable.Range(0, cols).Select(i => Text(sqlite3_column_name(stmt, i)) ?? $"c{i}").ToArray();
                var list = new List<T>();
                int rc;
                while ((rc = sqlite3_step(stmt)) == ROW)
                {
                    var values = new Dictionary<string, object?>(cols, StringComparer.OrdinalIgnoreCase);
                    for (var i = 0; i < cols; i++)
                        values[names[i]] = sqlite3_column_type(stmt, i) switch
                        {
                            INTEGER => sqlite3_column_int64(stmt, i),
                            FLOAT => sqlite3_column_double(stmt, i),
                            NULL => null,
                            _ => ColumnText(stmt, i)
                        };
                    list.Add(map(new SqliteRow(values, cols > 0 ? values[names[0]] : null)));
                }
                if (rc != DONE) throw Error("step", sql);
                return list;
            }
            finally { sqlite3_finalize(stmt); }
        }
    }

    /// <summary>First column of the first row, or default.</summary>
    public T? Scalar<T>(string sql, params (string Name, object? Value)[] args) =>
        Query(sql, r => r.First, args).FirstOrDefault() is { } v
            ? (T)Convert.ChangeType(v, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T))
            : default;

    /// <summary>Runs <paramref name="work"/> in one transaction (all or nothing).</summary>
    public void InTransaction(Action work)
    {
        lock (_gate)   // re-entrant: Execute/Query inside take the same lock
        {
            ExecScript("BEGIN IMMEDIATE;");
            try { work(); ExecScript("COMMIT;"); }
            catch { try { ExecScript("ROLLBACK;"); } catch { /* keep the first error */ } throw; }
        }
    }

    private IntPtr Prepare(string sql, (string Name, object? Value)[] args)
    {
        if (sqlite3_prepare_v2(_db, Utf8Z(sql), -1, out var stmt, IntPtr.Zero) != OK)
            throw Error("prepare", sql);
        foreach (var (name, value) in args)
        {
            var index = sqlite3_bind_parameter_index(stmt, Utf8Z(name));
            if (index == 0) continue;   // parameter not used in this statement
            var rc = value switch
            {
                null => sqlite3_bind_null(stmt, index),
                bool b => sqlite3_bind_int64(stmt, index, b ? 1 : 0),
                int i => sqlite3_bind_int64(stmt, index, i),
                long l => sqlite3_bind_int64(stmt, index, l),
                double d => sqlite3_bind_double(stmt, index, d),
                float f => sqlite3_bind_double(stmt, index, f),
                DateTimeOffset dt => BindText(stmt, index, dt.ToUniversalTime().ToString("O")),
                DateTime dt => BindText(stmt, index, dt.ToUniversalTime().ToString("O")),
                Enum e => BindText(stmt, index, e.ToString()),
                _ => BindText(stmt, index, value.ToString() ?? "")
            };
            if (rc != OK) { sqlite3_finalize(stmt); throw Error($"bind {name}", sql); }
        }
        return stmt;
    }

    private SqliteException Error(string what, string sql) =>
        new($"{what}: {Text(sqlite3_errmsg(_db))} — {(sql.Length > 120 ? sql[..120] + "…" : sql)}");

    public void Dispose()
    {
        lock (_gate)
        {
            if (_db == IntPtr.Zero) return;
            sqlite3_close_v2(_db);
            _db = IntPtr.Zero;
        }
    }
}

public sealed class SqliteException(string message) : Exception(message);
