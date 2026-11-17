using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal enum SqliteStoreDurability
{
    Strict,
    Relaxed
}

internal static class SqliteStoreConnectionPolicy
{
    internal const int DefaultBusyTimeoutMilliseconds = 30_000;
    internal const long JournalSizeLimitBytes = 67_108_864;
    internal static event Action<string, string>? ConnectionOpenedForDiagnostics;

    // The caller owns the returned connection; initialization failures release it here.
    internal static SqliteConnection Open(
        string databasePath,
        bool readOnly,
        SqliteStoreDurability durability,
        bool foreignKeys,
        string operationTag,
        int busyTimeoutMilliseconds = DefaultBusyTimeoutMilliseconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationTag);
        ArgumentOutOfRangeException.ThrowIfNegative(busyTimeoutMilliseconds);
        var synchronous = durability switch
        {
            SqliteStoreDurability.Strict => "FULL",
            SqliteStoreDurability.Relaxed => "NORMAL",
            _ => throw new ArgumentOutOfRangeException(nameof(durability))
        };

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        };
        var connection = new SqliteConnection(builder.ConnectionString);
        try
        {
            connection.Open();
            ExecuteNonQuery(connection, "PRAGMA busy_timeout=" + busyTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture));
            if (!readOnly)
            {
                ExecuteNonQuery(connection, "PRAGMA synchronous=" + synchronous);
                ExecuteNonQuery(connection, "PRAGMA journal_size_limit=" + JournalSizeLimitBytes.ToString(CultureInfo.InvariantCulture));
                if (foreignKeys)
                    ExecuteNonQuery(connection, "PRAGMA foreign_keys=ON");
            }

            ConnectionOpenedForDiagnostics?.Invoke(operationTag, Path.GetFullPath(databasePath));
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
