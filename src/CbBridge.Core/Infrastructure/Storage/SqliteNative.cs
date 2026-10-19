using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace CbBridge.Core.Infrastructure.Storage;

/// <summary>
/// The few SQLite C functions the bridge needs, called on the SQLite library the operating system already has:
/// winsqlite3.dll (Windows 10+), libsqlite3.so.0 (Linux), libsqlite3.dylib (macOS). No NuGet package.
/// Database:SqliteLibrary can point to a specific file instead.
/// </summary>
internal static class SqliteNative
{
    private const string Lib = "cbbridge_sqlite3";

    public const int OK = 0, ROW = 100, DONE = 101;
    public const int INTEGER = 1, FLOAT = 2, TEXT = 3, BLOB = 4, NULL = 5;
    public const int OPEN_READWRITE = 0x2, OPEN_CREATE = 0x4, OPEN_FULLMUTEX = 0x10000;
    private static readonly IntPtr Transient = new(-1);

    /// <summary>Set before the first call; null = try the usual system names.</summary>
    public static string? ConfiguredPath { get; set; }

    static SqliteNative() => NativeLibrary.SetDllImportResolver(typeof(SqliteNative).Assembly, Resolve);

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (name != Lib) return IntPtr.Zero;
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(ConfiguredPath)) candidates.Add(ConfiguredPath!);
        if (OperatingSystem.IsWindows()) candidates.AddRange(new[] { "winsqlite3.dll", "sqlite3.dll", "e_sqlite3.dll" });
        else if (OperatingSystem.IsMacOS()) candidates.AddRange(new[] { "libsqlite3.dylib", "/usr/lib/libsqlite3.dylib" });
        else candidates.AddRange(new[] { "libsqlite3.so.0", "libsqlite3.so", "e_sqlite3" });

        foreach (var c in candidates)
            if (NativeLibrary.TryLoad(c, assembly, path, out var handle)) return handle;
        throw new DllNotFoundException(
            "No SQLite library found (tried " + string.Join(", ", candidates) + "). " +
            "Linux: install libsqlite3-0 (apt) / sqlite-libs (dnf). Or set Database:SqliteLibrary to the file.");
    }

    [DllImport(Lib)] public static extern IntPtr sqlite3_libversion();
    [DllImport(Lib)] public static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
    [DllImport(Lib)] public static extern int sqlite3_close_v2(IntPtr db);
    [DllImport(Lib)] public static extern int sqlite3_busy_timeout(IntPtr db, int ms);
    [DllImport(Lib)] public static extern IntPtr sqlite3_errmsg(IntPtr db);
    [DllImport(Lib)] public static extern int sqlite3_exec(IntPtr db, byte[] sql, IntPtr callback, IntPtr arg, out IntPtr errmsg);
    [DllImport(Lib)] public static extern void sqlite3_free(IntPtr p);
    [DllImport(Lib)] public static extern int sqlite3_changes(IntPtr db);
    [DllImport(Lib)] public static extern long sqlite3_last_insert_rowid(IntPtr db);

    [DllImport(Lib)] public static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int nByte, out IntPtr stmt, IntPtr tail);
    [DllImport(Lib)] public static extern int sqlite3_step(IntPtr stmt);
    [DllImport(Lib)] public static extern int sqlite3_finalize(IntPtr stmt);
    [DllImport(Lib)] public static extern int sqlite3_bind_parameter_index(IntPtr stmt, byte[] name);
    [DllImport(Lib)] public static extern int sqlite3_bind_null(IntPtr stmt, int index);
    [DllImport(Lib)] public static extern int sqlite3_bind_int64(IntPtr stmt, int index, long value);
    [DllImport(Lib)] public static extern int sqlite3_bind_double(IntPtr stmt, int index, double value);
    [DllImport(Lib)] private static extern int sqlite3_bind_text(IntPtr stmt, int index, byte[] value, int n, IntPtr destructor);

    [DllImport(Lib)] public static extern int sqlite3_column_count(IntPtr stmt);
    [DllImport(Lib)] public static extern IntPtr sqlite3_column_name(IntPtr stmt, int col);
    [DllImport(Lib)] public static extern int sqlite3_column_type(IntPtr stmt, int col);
    [DllImport(Lib)] public static extern long sqlite3_column_int64(IntPtr stmt, int col);
    [DllImport(Lib)] public static extern double sqlite3_column_double(IntPtr stmt, int col);
    [DllImport(Lib)] public static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);
    [DllImport(Lib)] public static extern int sqlite3_column_bytes(IntPtr stmt, int col);

    public static byte[] Utf8Z(string s) => Encoding.UTF8.GetBytes(s + "\0");

    public static int BindText(IntPtr stmt, int index, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return sqlite3_bind_text(stmt, index, bytes, bytes.Length, Transient);   // SQLite copies it
    }

    public static string? Text(IntPtr p) => p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);

    public static string ColumnText(IntPtr stmt, int col)
    {
        var p = sqlite3_column_text(stmt, col);
        var n = sqlite3_column_bytes(stmt, col);
        return p == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(p, n);
    }
}
