using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;

namespace Mcg.AgentOrchestrator.Infrastructure;

// A token is comparable only within this observer's retained, read-only connection.
// No read transaction survives Observe. Unknown identity/availability disables reuse.
public sealed class SqliteStoreChangeObservation(string path) : IDisposable
{
    private readonly object _sync = new();
    private readonly string _path = Path.GetFullPath(path);
    private SqliteConnection? _connection;
    private SafeFileHandle? _file;
    private string? _identity;
    private string _epoch = "";
    private bool _disposed;

    public string? Observe()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            try
            {
                // Windows is the console host. Other hosts conservatively read until
                // their file-identity contract is implemented; timestamps are not identity.
                if (!OperatingSystem.IsWindows()) return null;
                // Pin the path only for this observation/open, preventing a path swap
                // between identifying the file and SQLite opening its own handle.
                using var current = File.OpenHandle(_path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite);
                var identity = Identity(current);
                if (identity is null) { Reset(); return null; }
                if (_connection is null || identity != _identity)
                {
                    Reset();
                    _file = File.OpenHandle(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (Identity(_file) != identity) { Reset(); return null; }
                    _connection = new(new SqliteConnectionStringBuilder
                    {
                        DataSource = _path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1
                    }.ToString());
                    _connection.Open();
                    _identity = identity;
                    _epoch = Guid.NewGuid().ToString("N");
                }
                using var command = _connection.CreateCommand();
                command.CommandText = "PRAGMA data_version";
                var version = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
                using var after = File.OpenHandle(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (Identity(after) != _identity) { Reset(); return null; }
                return _epoch + ":" + version.ToString(CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
            {
                Reset();
                // Absence is rechecked every observation; it cannot hide later creation.
                return ex is FileNotFoundException or DirectoryNotFoundException ? "missing:" + _path : null;
            }
        }
    }

    private void Reset()
    {
        _connection?.Dispose();
        _connection = null;
        _file?.Dispose();
        _file = null;
        _identity = null;
    }

    public void Dispose()
    {
        lock (_sync) { if (_disposed) return; _disposed = true; Reset(); }
    }

    private static string? Identity(SafeFileHandle handle) => GetFileInformationByHandle(handle, out var info) &&
        (info.FileIndexHigh != 0 || info.FileIndexLow != 0)
        ? $"{info.VolumeSerialNumber:x8}:{info.FileIndexHigh:x8}:{info.FileIndexLow:x8}" : null;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
