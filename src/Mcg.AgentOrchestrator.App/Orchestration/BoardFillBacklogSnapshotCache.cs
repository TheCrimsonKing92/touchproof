using System.Runtime.InteropServices;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class BoardFillBacklogSnapshotCache(
    string path, Action<string, SqliteConnection>? observe = null) : IDisposable
{
    private readonly object _gate = new();
    private CacheEntry? _entry;
    private bool _disposed;

    internal IReadOnlyList<BacklogItem> Read()
    {
        lock (_gate)
        {
            if (_disposed || !OperatingSystem.IsWindows())
                return ReadFull();

            // Disallow replacement during open and snapshot establishment. The
            // entry itself holds a delete-sharing handle, so the ID cannot be
            // recycled and the path can still be replaced between acquisitions.
            using var guard = TryOpenFile(FileShare.ReadWrite);
            if (guard is null || GetIdentity(guard) is not { } identity)
                return ReadFull();

            using var session = TryOpenSession();
            if (session is null)
                return ReadFull();

            var connection = session.Connection;
            var revision = BacklogSnapshotRevision.TryRead(connection);
            var current = TryOpenFile(FileShare.ReadWrite | FileShare.Delete);
            try
            {
                if (current is null || GetIdentity(current) != identity || GetDatabaseIdentity(connection) != identity)
                {
                    session.Dispose();
                    guard.Dispose();
                    return ReadFull();
                }

                if (revision is not { } proof)
                {
                    Evict();
                    return BoardFillBacklogSnapshot.ReadCore(connection);
                }

                if (_entry is { } entry && entry.Identity == identity && entry.Revision == proof)
                    return entry.Items;

                Evict();
                var items = BoardFillBacklogSnapshot.ReadCore(connection, retain: true);
                // Transfer ownership only after the entire graph has materialized.
                _entry = new(identity, proof, items, current);
                current = null;
                return items;
            }
            finally { current?.Dispose(); }
        }
    }

    private IReadOnlyList<BacklogItem> ReadFull()
    {
        Evict();
        return BoardFillBacklogSnapshot.Read(path, observe);
    }

    private SafeFileHandle? TryOpenFile(FileShare share)
    {
        try { return File.OpenHandle(path, FileMode.Open, FileAccess.Read, share); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private SqliteReadSession? TryOpenSession()
    {
        try { return new(path, observe); }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException) { return null; }
    }

    private void Evict()
    {
        _entry?.File.Dispose();
        _entry = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            Evict();
        }
    }

    private sealed record CacheEntry(FileIdentity Identity, BacklogSnapshotRevision Revision,
        IReadOnlyList<BacklogItem> Items, SafeFileHandle File);

    private static FileIdentity? GetIdentity(SafeFileHandle file) =>
        GetIdentity(file.DangerousGetHandle());

    private static FileIdentity? GetIdentity(nint file) =>
        GetFileInformationByHandleEx(file, FileIdInfo, out var identity, Marshal.SizeOf<FileIdentity>()) &&
        (identity.Low != 0 || identity.High != 0) ? identity : null;

    private static FileIdentity? GetDatabaseIdentity(SqliteConnection connection)
    {
        // Microsoft.Data.Sqlite in this application uses the bundled e_sqlite3
        // provider. Compare against the actual VFS handle, including for aliases.
        try
        {
            return SQLitePCL.raw.GetNativeLibraryName() == "e_sqlite3" &&
                GetWin32Handle(connection.Handle!, "main", SqliteWin32GetHandle, out var file) == 0
                ? GetIdentity(file) : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return null;
        }
    }

    private const int FileIdInfo = 18;
    private const int SqliteWin32GetHandle = 29;

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct FileIdentity(ulong Volume, ulong Low, ulong High);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(nint file, int informationClass,
        out FileIdentity information, int size);

    [DllImport("e_sqlite3", EntryPoint = "sqlite3_file_control", CallingConvention = CallingConvention.Cdecl)]
    private static extern int GetWin32Handle(SQLitePCL.sqlite3 database, [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        int operation, out nint file);
}
