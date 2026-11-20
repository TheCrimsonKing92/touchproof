using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public enum BacklogIntakeReservationKind
{
    Acquired,
    ExistingGoal,
    InProgress
}

public sealed record BacklogIntakeRecord(
    string SourceBacklogItemId,
    string Heading,
    string Status,
    string? GoalId,
    DateTimeOffset StartedAt,
    DateTimeOffset LastHeartbeatAt,
    int? OwnerProcessId,
    string? StdoutPath,
    string? StderrPath);

public sealed record BacklogIntakeReservation(
    BacklogIntakeReservationKind Kind,
    BacklogIntakeRecord Record);

internal enum BacklogContinuationPhase
{
    Prepared,
    Transferred,
    Rearmed
}

internal sealed record BacklogIntakeContinuationReservation(
    string ItemId,
    string PredecessorId,
    long ClaimVersion,
    string SuccessorId,
    BacklogContinuationPhase Phase,
    BacklogIntakeRecord? ExpectedIntake,
    string? RequestKey,
    string? RequestFingerprint);

public sealed class BacklogIntakeRecordStore
{
    public static readonly TimeSpan DefaultStaleAfter = TimeSpan.FromMinutes(30);

    private readonly string _dbPath;

    // The connection factory already waits for the full state-store budget. Retrying here would
    // multiply that wait for the same write.
    private const int MaxBusyRetries = 1;

    public BacklogIntakeRecordStore(string dbPath)
    {
        if (string.IsNullOrWhiteSpace(dbPath))
            throw new ArgumentException("Value cannot be empty.", nameof(dbPath));

        _dbPath = dbPath;
    }

    public BacklogIntakeReservation Reserve(
        string sourceBacklogItemId,
        string heading,
        bool forceReclaim = false,
        TimeSpan? staleAfter = null)
    {
        if (string.IsNullOrWhiteSpace(sourceBacklogItemId))
            throw new ArgumentException("Value cannot be empty.", nameof(sourceBacklogItemId));

        var now = DateTimeOffset.UtcNow;
        var stdoutPath = Environment.GetEnvironmentVariable("MCG_ORCHESTRATOR_STDOUT_LOG_PATH");
        var stderrPath = Environment.GetEnvironmentVariable("MCG_ORCHESTRATOR_STDERR_LOG_PATH");
        using var conn = BeginWrite();
        try
        {
            var existing = LoadRecord(conn, sourceBacklogItemId);
            if (existing?.GoalId is null && ReadContinuation(conn, sourceBacklogItemId) is not null)
                throw new InvalidOperationException($"BACKLOG_INTAKE_CONTINUATION_IN_PROGRESS backlogItem={sourceBacklogItemId}");
            if (existing is null)
            {
                var inserted = new BacklogIntakeRecord(
                    sourceBacklogItemId,
                    heading,
                    "InProgress",
                    GoalId: null,
                    StartedAt: now,
                    LastHeartbeatAt: now,
                    OwnerProcessId: Environment.ProcessId,
                    StdoutPath: NormalizeLogPath(stdoutPath),
                    StderrPath: NormalizeLogPath(stderrPath));
                InsertRecord(conn, inserted);
                RunNonQuery(conn, "COMMIT");
                return new BacklogIntakeReservation(BacklogIntakeReservationKind.Acquired, inserted);
            }

            if (!string.IsNullOrWhiteSpace(existing.GoalId))
            {
                RunNonQuery(conn, "COMMIT");
                return new BacklogIntakeReservation(BacklogIntakeReservationKind.ExistingGoal, existing);
            }

            if (forceReclaim || IsStale(existing, now, staleAfter ?? DefaultStaleAfter))
            {
                var reclaimed = existing with
                {
                    Heading = string.IsNullOrWhiteSpace(heading) ? existing.Heading : heading,
                    Status = "InProgress",
                    StartedAt = now,
                    LastHeartbeatAt = now,
                    OwnerProcessId = Environment.ProcessId,
                    StdoutPath = NormalizeLogPath(stdoutPath) ?? existing.StdoutPath,
                    StderrPath = NormalizeLogPath(stderrPath) ?? existing.StderrPath
                };
                UpdateRecord(conn, reclaimed);
                RunNonQuery(conn, "COMMIT");
                return new BacklogIntakeReservation(BacklogIntakeReservationKind.Acquired, reclaimed);
            }

            RunNonQuery(conn, "COMMIT");
            return new BacklogIntakeReservation(BacklogIntakeReservationKind.InProgress, existing);
        }
        catch
        {
            try { RunNonQuery(conn, "ROLLBACK"); } catch { }
            throw;
        }
    }

    public void MarkGoalCreated(string sourceBacklogItemId, string goalId)
    {
        if (string.IsNullOrWhiteSpace(sourceBacklogItemId))
            throw new ArgumentException("Value cannot be empty.", nameof(sourceBacklogItemId));
        if (string.IsNullOrWhiteSpace(goalId))
            throw new ArgumentException("Value cannot be empty.", nameof(goalId));

        if (StateDbWriteSession.TryExecute(
                _dbPath,
                connection => MarkGoalCreated(connection, sourceBacklogItemId, goalId)))
        {
            return;
        }

        WithBusyRetry(() =>
        {
            using var conn = OpenConnection();
            MarkGoalCreated(conn, sourceBacklogItemId, goalId);
            return true;
        });
    }

    private static void MarkGoalCreated(
        SqliteConnection connection,
        string sourceBacklogItemId,
        string goalId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE backlog_intake_records
            SET goal_id = $goal_id,
                status = 'GoalCreated',
                last_heartbeat_at = $last_heartbeat_at
            WHERE source_backlog_item_id = $source_backlog_item_id
              AND goal_id IS NULL
            """;
        command.Parameters.AddWithValue("$goal_id", goalId);
        command.Parameters.AddWithValue("$last_heartbeat_at", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$source_backlog_item_id", sourceBacklogItemId);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException(
                $"BACKLOG_INTAKE_INVALID_TRANSITION source={sourceBacklogItemId} goal={goalId}");
        }
    }

    public BacklogIntakeRecord? Get(string sourceBacklogItemId)
    {
        if (string.IsNullOrWhiteSpace(sourceBacklogItemId))
            return null;

        BacklogIntakeRecord? current = null;
        if (StateDbWriteSession.TryExecute(_dbPath, connection => current = LoadRecord(connection, sourceBacklogItemId)))
            return current;

        if (!File.Exists(_dbPath))
            return null;

        try
        {
            using var conn = OpenReadConnection();
            return LoadRecord(conn, sourceBacklogItemId);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            return null;
        }
    }

    internal BacklogIntakeContinuationReservation? GetContinuation(string itemId)
    {
        BacklogIntakeContinuationReservation? result = null;
        if (StateDbWriteSession.TryExecute(_dbPath, connection => result = ReadContinuation(connection, itemId)))
            return result;
        if (!File.Exists(_dbPath))
            return null;
        using var connection = OpenReadConnection();
        return ReadContinuation(connection, itemId);
    }

    internal bool HasPendingContinuation(string requestKey)
    {
        using var connection = OpenReadConnection();
        if (!HasContinuationTable(connection))
            return false;
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM backlog_intake_continuations WHERE request_key = $key LIMIT 1";
        command.Parameters.AddWithValue("$key", requestKey);
        return command.ExecuteScalar() is not null;
    }

    internal void PrepareContinuation(BacklogIntakeContinuationReservation reservation)
    {
        WriteContinuation(connection =>
        {
            if (LoadRecord(connection, reservation.ItemId) != reservation.ExpectedIntake ||
                ReadContinuation(connection, reservation.ItemId) is not null)
                throw InvalidContinuationTransition(reservation);

            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS backlog_intake_continuations (
                    item_id TEXT PRIMARY KEY,
                    predecessor_id TEXT NOT NULL,
                    claim_version INTEGER NOT NULL,
                    successor_id TEXT NOT NULL UNIQUE,
                    phase TEXT NOT NULL,
                    expected_intake TEXT NOT NULL,
                    request_key TEXT,
                    request_fingerprint TEXT
                );
                INSERT INTO backlog_intake_continuations
                    (item_id, predecessor_id, claim_version, successor_id, phase,
                     expected_intake, request_key, request_fingerprint)
                VALUES ($item, $predecessor, $version, $successor, $phase,
                        $intake, $request_key, $request_fingerprint);
                """;
            AddContinuationIdentity(command, reservation);
            command.Parameters.AddWithValue("$intake", JsonSerializer.Serialize(reservation.ExpectedIntake));
            command.Parameters.AddWithValue("$request_key", (object?)reservation.RequestKey ?? DBNull.Value);
            command.Parameters.AddWithValue("$request_fingerprint", (object?)reservation.RequestFingerprint ?? DBNull.Value);
            command.ExecuteNonQuery();
        });
    }

    internal void MarkContinuationTransferred(BacklogIntakeContinuationReservation expected) =>
        AdvanceContinuation(expected, BacklogContinuationPhase.Prepared, BacklogContinuationPhase.Transferred,
            (_, intake) => intake);

    internal void RearmContinuation(BacklogIntakeContinuationReservation expected, string heading) =>
        AdvanceContinuation(expected, BacklogContinuationPhase.Transferred, BacklogContinuationPhase.Rearmed,
            (connection, previous) =>
            {
                var now = DateTimeOffset.UtcNow;
                var record = new BacklogIntakeRecord(expected.ItemId, heading, "InProgress",
                    GoalId: null, now, now, Environment.ProcessId, StdoutPath: null, StderrPath: null);
                if (previous is null)
                    InsertRecord(connection, record);
                else
                    UpdateRecord(connection, record);
                return record;
            });

    internal void CompleteContinuation(BacklogIntakeContinuationReservation expected)
    {
        WriteContinuation(connection =>
        {
            ValidateContinuation(connection, expected, BacklogContinuationPhase.Rearmed);
            MarkGoalCreated(connection, expected.ItemId, expected.SuccessorId);
            using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM backlog_intake_continuations
                WHERE item_id = $item AND predecessor_id = $predecessor AND claim_version = $version
                  AND successor_id = $successor AND phase = $phase
                """;
            AddContinuationIdentity(command, expected);
            if (command.ExecuteNonQuery() != 1)
                throw InvalidContinuationTransition(expected);
        });
    }

    private void AdvanceContinuation(
        BacklogIntakeContinuationReservation expected, BacklogContinuationPhase from, BacklogContinuationPhase to,
        Func<SqliteConnection, BacklogIntakeRecord?, BacklogIntakeRecord?> updateIntake)
    {
        WriteContinuation(connection =>
        {
            ValidateContinuation(connection, expected, from);
            var intake = updateIntake(connection, expected.ExpectedIntake);
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE backlog_intake_continuations SET phase = $next, expected_intake = $intake
                WHERE item_id = $item AND predecessor_id = $predecessor AND claim_version = $version
                  AND successor_id = $successor AND phase = $phase
                """;
            AddContinuationIdentity(command, expected);
            command.Parameters.AddWithValue("$next", to.ToString());
            command.Parameters.AddWithValue("$intake", JsonSerializer.Serialize(intake));
            if (command.ExecuteNonQuery() != 1)
                throw InvalidContinuationTransition(expected);
        });
    }

    private void WriteContinuation(Action<SqliteConnection> action)
    {
        if (!StateDbWriteSession.TryExecute(_dbPath, action))
            throw new InvalidOperationException("Backlog continuation writes must join the active state transaction.");
    }

    private static void ValidateContinuation(SqliteConnection connection,
        BacklogIntakeContinuationReservation expected, BacklogContinuationPhase phase)
    {
        // The snapshot comparison and the record write share the same SQLite writer.
        if (expected.Phase != phase || ReadContinuation(connection, expected.ItemId) != expected ||
            LoadRecord(connection, expected.ItemId) != expected.ExpectedIntake)
            throw InvalidContinuationTransition(expected);
    }

    private static InvalidOperationException InvalidContinuationTransition(BacklogIntakeContinuationReservation expected) =>
        new($"BACKLOG_INTAKE_CONTINUATION_REFUSED reason=SnapshotChanged backlogItem={expected.ItemId} phase={expected.Phase}");

    private static void AddContinuationIdentity(SqliteCommand command, BacklogIntakeContinuationReservation reservation)
    {
        command.Parameters.AddWithValue("$item", reservation.ItemId);
        command.Parameters.AddWithValue("$predecessor", reservation.PredecessorId);
        command.Parameters.AddWithValue("$version", reservation.ClaimVersion);
        command.Parameters.AddWithValue("$successor", reservation.SuccessorId);
        command.Parameters.AddWithValue("$phase", reservation.Phase.ToString());
    }

    private static BacklogIntakeContinuationReservation? ReadContinuation(SqliteConnection connection, string itemId)
    {
        if (!HasContinuationTable(connection))
            return null;
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT predecessor_id, claim_version, successor_id, phase, expected_intake, request_key, request_fingerprint
            FROM backlog_intake_continuations WHERE item_id = $item
            """;
        command.Parameters.AddWithValue("$item", itemId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        return new(itemId, reader.GetString(0), reader.GetInt64(1), reader.GetString(2),
            Enum.Parse<BacklogContinuationPhase>(reader.GetString(3)),
            JsonSerializer.Deserialize<BacklogIntakeRecord>(reader.GetString(4)),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6));
    }

    private static bool HasContinuationTable(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_schema WHERE name = 'backlog_intake_continuations' AND type = 'table'";
        return command.ExecuteScalar() is not null;
    }

    private SqliteConnection OpenConnection() =>
        StateDbConnectionFactory.Open(_dbPath, StateDbConnectionProfile.ReadWrite);

    private SqliteConnection OpenReadConnection() =>
        StateDbConnectionFactory.Open(_dbPath, StateDbConnectionProfile.QueryOnlyRead);

    private SqliteConnection BeginWrite()
    {
        return WithBusyRetry(() =>
        {
            var conn = OpenConnection();
            try
            {
                RunNonQuery(conn, "BEGIN IMMEDIATE");
                return conn;
            }
            catch
            {
                conn.Dispose();
                throw;
            }
        });
    }

    private static T WithBusyRetry<T>(Func<T> operation)
    {
        var delayMs = 50;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return operation();
            }
            catch (SqliteException ex) when (attempt < MaxBusyRetries && IsTransientLock(ex))
            {
                Thread.Sleep(delayMs);
                delayMs = Math.Min(delayMs * 2, 1000);
            }
        }
    }

    private static bool IsTransientLock(SqliteException ex) =>
        ex.SqliteErrorCode == 5 /* SQLITE_BUSY */ || ex.SqliteErrorCode == 6 /* SQLITE_LOCKED */;

    private static BacklogIntakeRecord? LoadRecord(SqliteConnection conn, string sourceBacklogItemId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT source_backlog_item_id, heading, status, goal_id, started_at, last_heartbeat_at,
                   owner_process_id, stdout_path, stderr_path
            FROM backlog_intake_records
            WHERE source_backlog_item_id = $source_backlog_item_id
            """;
        cmd.Parameters.AddWithValue("$source_backlog_item_id", sourceBacklogItemId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        return ReadRecord(reader);
    }

    private static void InsertRecord(SqliteConnection conn, BacklogIntakeRecord record)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO backlog_intake_records (
                source_backlog_item_id, heading, status, goal_id, started_at, last_heartbeat_at,
                owner_process_id, stdout_path, stderr_path)
            VALUES (
                $source_backlog_item_id, $heading, $status, $goal_id, $started_at, $last_heartbeat_at,
                $owner_process_id, $stdout_path, $stderr_path)
            """;
        AddRecordParameters(cmd, record);
        cmd.ExecuteNonQuery();
    }

    private static void UpdateRecord(SqliteConnection conn, BacklogIntakeRecord record)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE backlog_intake_records
            SET heading = $heading,
                status = $status,
                goal_id = $goal_id,
                started_at = $started_at,
                last_heartbeat_at = $last_heartbeat_at,
                owner_process_id = $owner_process_id,
                stdout_path = $stdout_path,
                stderr_path = $stderr_path
            WHERE source_backlog_item_id = $source_backlog_item_id
            """;
        AddRecordParameters(cmd, record);
        cmd.ExecuteNonQuery();
    }

    private static void AddRecordParameters(SqliteCommand cmd, BacklogIntakeRecord record)
    {
        cmd.Parameters.AddWithValue("$source_backlog_item_id", record.SourceBacklogItemId);
        cmd.Parameters.AddWithValue("$heading", record.Heading);
        cmd.Parameters.AddWithValue("$status", record.Status);
        cmd.Parameters.AddWithValue("$goal_id", (object?)record.GoalId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$started_at", record.StartedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$last_heartbeat_at", record.LastHeartbeatAt.ToString("O"));
        cmd.Parameters.AddWithValue("$owner_process_id", (object?)record.OwnerProcessId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$stdout_path", (object?)record.StdoutPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$stderr_path", (object?)record.StderrPath ?? DBNull.Value);
    }

    private static BacklogIntakeRecord ReadRecord(SqliteDataReader reader)
    {
        return new BacklogIntakeRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            DateTimeOffset.Parse(reader.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind),
            DateTimeOffset.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind),
            reader.IsDBNull(6) ? null : reader.GetInt32(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8));
    }

    private static bool IsStale(BacklogIntakeRecord record, DateTimeOffset now, TimeSpan staleAfter)
    {
        if (now - record.LastHeartbeatAt >= staleAfter)
            return true;

        return record.OwnerProcessId is int pid && !IsProcessAlive(pid);
    }

    private static bool IsProcessAlive(int pid)
        => IsProcessAlive(pid, ReadHasExited);

    internal static bool IsProcessAlive(int pid, Func<int, bool> readHasExited)
    {
        try
        {
            return !readHasExited(pid);
        }
        catch (Exception exception) when (ProcessProbeFailure.IsNotLive(exception))
        {
            return false;
        }
    }

    private static bool ReadHasExited(int pid)
    {
        using var process = System.Diagnostics.Process.GetProcessById(pid);
        return process.HasExited;
    }

    private static void RunNonQuery(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string? NormalizeLogPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        return Path.GetFullPath(path);
    }
}
