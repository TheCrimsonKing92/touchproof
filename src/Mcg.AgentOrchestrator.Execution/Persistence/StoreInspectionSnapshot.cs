using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Inspect schema metadata, not every data page. Live WAL stores use SQLite's read
// snapshot; pinned existing sidecars prevent a read from creating source files.
// Quiet files are copied under SQLite's database lock, then released before any
// inspection query. Denying write access with FileShare would make SQLite silently
// downgrade a newly opened writer to read-only, even after inspection finishes.
internal sealed class StoreInspectionSnapshot : IDisposable
{
    private readonly string? _directory;
    private readonly List<FileStream> _handles = [];
    // SQLite's PENDING, RESERVED and SHARED byte range (os.h). Holding it
    // exclusively prevents a new transaction while a quiet database is copied.
    private const long LockOffset = 0x40000000;
    private const long LockLength = 512;
    private readonly bool _ownsDirectory;
    internal SqliteConnection Connection { get; }
    internal static event Action<SqliteConnection, string>? ConnectionOpenedForDiagnostics;

    internal StoreInspectionSnapshot(string source, TimeProvider? timeProvider = null,
        Action<TimeSpan>? waitForLockRetry = null, string? stagingDirectory = null,
        Func<string, FileStream>? createStagedFile = null, Action<string>? prepareStagedDatabase = null,
        int busyTimeoutMilliseconds = StateDbConnectionFactory.DefaultBusyTimeoutMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(busyTimeoutMilliseconds);
        _ownsDirectory = stagingDirectory is null;
        try
        {
            if (TryOpenLiveWal(source, busyTimeoutMilliseconds, out var live))
            {
                Connection = live!;
                return;
            }

            var clock = timeProvider ?? TimeProvider.System;
            var started = clock.GetTimestamp();
            var budget = TimeSpan.FromMilliseconds(StateDbConnectionFactory.DefaultBusyTimeoutMilliseconds);
            FileStream sourceHandle;
            while (true)
            {
                // Windows byte locks are exclusive even on a read handle. On Unix a
                // read/write handle is needed for an exclusive fcntl lock; no writes occur.
                sourceHandle = new FileStream(source, FileMode.Open,
                    OperatingSystem.IsWindows() ? FileAccess.Read : FileAccess.ReadWrite, FileShare.ReadWrite);
                _handles.Add(sourceHandle);
                try
                {
                    sourceHandle.Lock(LockOffset, LockLength);
                    break;
                }
                catch (IOException)
                {
                    ReleaseHandles();
                    // A writer may have activated WAL since the initial observation.
                    if (TryOpenLiveWal(source, busyTimeoutMilliseconds, out live))
                    {
                        Connection = live!;
                        return;
                    }
                    // Another quiet inspection or SQLite transaction can hold this
                    // range without sidecars. Honor the SQLite busy budget rather
                    // than making one failed byte-lock attempt terminal.
                    var remaining = budget - clock.GetElapsedTime(started);
                    if (remaining <= TimeSpan.Zero) throw;
                    var delay = TimeSpan.FromMilliseconds(Math.Min(50, remaining.TotalMilliseconds));
                    if (waitForLockRetry is null) Thread.Sleep(delay);
                    else waitForLockRetry(delay);
                }
            }
            try
            {
                _directory = stagingDirectory ??
                    Path.Combine(OrchestratorTempRoot.GetPurposeDirectory("store-inspection"), Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_directory);
                // The database lock also prevents an orphaned WAL from being adopted
                // during copying. Preserve a crash journal so SQLite refuses an
                // unrecovered read rather than treating uncommitted pages as current.
                foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
                {
                    var path = source + suffix;
                    if (!StoreSetupRunner.TargetExists(path)) continue;
                    _handles.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
                }
                foreach (var handle in _handles)
                {
                    var path = Path.Combine(_directory, Path.GetFileName(handle.Name));
                    using var destination = createStagedFile?.Invoke(path) ??
                        new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    handle.CopyTo(destination);
                }
            }
            finally { sourceHandle.Unlock(LockOffset, LockLength); }
            ReleaseHandles();
            var stagedDatabase = Path.Combine(_directory, Path.GetFileName(source));
            prepareStagedDatabase?.Invoke(stagedDatabase);
            Connection = Open(stagedDatabase, busyTimeoutMilliseconds);
            ConnectionOpenedForDiagnostics?.Invoke(Connection, source);
        }
        catch
        {
            try { Connection?.Dispose(); }
            finally { ReleaseSnapshotFiles(); }
            throw;
        }
    }

    internal string ReadJournalMode()
    {
        using var command = Connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode";
        return command.ExecuteScalar()?.ToString() ?? string.Empty;
    }

    private bool TryOpenLiveWal(string source, int busyTimeoutMilliseconds, out SqliteConnection? connection)
    {
        connection = null;
        if (!StoreSetupRunner.TargetExists(source + "-wal") || !StoreSetupRunner.TargetExists(source + "-shm"))
            return false;
        try
        {
            // Pin existing sidecars without denying writer opens. If the last
            // connection removes them before they are pinned, use the quiet path.
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                _handles.Add(new FileStream(source + suffix, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        }
        catch (FileNotFoundException)
        {
            ReleaseHandles();
            return false;
        }
        connection = Open(source, busyTimeoutMilliseconds);
        try
        {
            using var begin = connection.CreateCommand();
            begin.CommandText = "BEGIN";
            begin.ExecuteNonQuery();
            ConnectionOpenedForDiagnostics?.Invoke(connection, source);
        }
        catch { connection.Dispose(); throw; }
        return true;
    }

    private static SqliteConnection Open(string source, int busyTimeoutMilliseconds)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = source,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            // Microsoft.Data.Sqlite retries BUSY independently. Its timeout is in whole seconds,
            // and zero would make those retries unbounded.
            DefaultTimeout = Math.Max(1, (int)Math.Ceiling(busyTimeoutMilliseconds / 1_000d))
        }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA busy_timeout=" + busyTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture);
            command.ExecuteNonQuery();
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    private void ReleaseHandles()
    {
        foreach (var handle in _handles) handle.Dispose();
        _handles.Clear();
    }

    public void Dispose()
    {
        try { Connection.Dispose(); }
        finally { ReleaseSnapshotFiles(); }
    }

    private void ReleaseSnapshotFiles()
    {
        ReleaseHandles();
        if (_ownsDirectory && _directory is not null) Directory.Delete(_directory, recursive: true);
    }
}
