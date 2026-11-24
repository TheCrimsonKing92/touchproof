using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class SqliteOrchestratorStateRepository : IOrchestratorStateOutboxRepository, IProviderConnectionEvidenceStore
{
    public bool SupportsGoalCheckpointContainment => true;

    public const string CurrentSchemaVersion = "1";
    private const int GoalMetadataTitleMaxChars = 240;
    private const int MaxOptimisticConcurrencyRetries = 6;
    public static readonly TimeSpan OutboxProcessingLease = TimeSpan.FromMinutes(15);
    private readonly string _dbPath;
    private readonly Action<string>? _statementObserver;
    private readonly SqliteWriteTelemetry _writeTelemetry;
    private readonly Action? _beforeOutboxCommit;
    private readonly StateDbConnectionProfile _connectionProfile;
    private readonly Action<GoalSnapshotReadObservation>? _readObserver;
    private readonly ConcurrentDictionary<TerminalMetadataCacheKey, Lazy<TerminalGoalMetadataValues>> _terminalMetadataCache = new();
    private static readonly AsyncLocal<string?> CurrentWriteOperationTag = new();
    private static readonly string[] CoreSchemaTableNames =
    [
        "meta",
        "goals",
        "human_input_requests",
        "model_fit_history"
    ];
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private static readonly GoalSnapshotArchive SnapshotArchive = new(SerializerOptions);
    private static readonly GoalTaskHistoryStore TaskHistoryStore = new(SerializerOptions);
    internal static JsonSerializerOptions HistorySerializerOptions => SerializerOptions;
    private const string GoalSnapshotArchiveSchemaSql = """
        CREATE TABLE IF NOT EXISTS goal_snapshot_archive (
            goal_id TEXT PRIMARY KEY,
            snapshot_json TEXT NOT NULL,
            archived_at TEXT NOT NULL
        )
        """;

    public SqliteOrchestratorStateRepository(string dbPath)
        : this(dbPath, statementObserver: null, telemetryOptions: null)
    {
    }

    public static SqliteOrchestratorStateRepository OpenReadOnly(string dbPath) => OpenReadOnly(dbPath, null);

    /// <summary>Opens a stable offline copy with a private WAL index. The caller must exclude concurrent writers.</summary>
    public static SqliteOrchestratorStateRepository OpenReadOnlyOfflineCopy(string dbPath) =>
        new(dbPath, statementObserver: null, telemetryOptions: null, beforeOutboxCommit: null,
            StateDbConnectionProfile.OfflineCopyRead, readObserver: null);

    public ReconcileAcceptanceLeaseState? ReadAcceptanceLease(GoalId goalId, TimeSpan staleAfter)
    {
        using var connection = OpenConnection();
        return ReconcileSweepRemediationStore.GetAcceptanceLease(connection, goalId.Value, staleAfter);
    }

    public static SqliteOrchestratorStateRepository OpenReadOnly(
        string dbPath, Action<GoalSnapshotReadObservation>? readObserver) =>
        new(
            dbPath,
            statementObserver: null,
            telemetryOptions: null,
            beforeOutboxCommit: null,
            StateDbConnectionProfile.QueryOnlyRead, readObserver);

    public static string VerifyJournalMode(string dbPath)
    {
        using var connection = StateDbConnectionFactory.Open(
            dbPath,
            StateDbConnectionProfile.QueryOnlyRead);
        return StateDbConnectionFactory.ReadJournalMode(connection);
    }

    public static string ValidateReadOnlySchema(string dbPath)
    {
        using var conn = StateDbConnectionFactory.Open(
            dbPath,
            StateDbConnectionProfile.QueryOnlyRead);

        using var version = conn.CreateCommand();
        version.CommandText = "SELECT value FROM meta WHERE key = 'schema_version'";
        var value = version.ExecuteScalar()?.ToString();
        if (!string.Equals(value, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"State schema is incompatible or has a pending migration: expected {CurrentSchemaVersion}, found {value ?? "missing"}.");
        }

        using var requiredColumns = conn.CreateCommand();
        requiredColumns.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM pragma_table_info('goals') WHERE name IN ('version', 'source_backlog_item_id')) +
                (SELECT COUNT(*) FROM pragma_table_info('model_fit_history') WHERE name IN ('outcome_rule', 'outcome_class', 'dispatch_lane'))
            """;
        // Keep the read-only successor check compatible with the immediately preceding
        // outbox schema. The successor runs the idempotent quarantine-column migration
        // only after this preflight succeeds and it opens the repository for writing.
        if (Convert.ToInt32(requiredColumns.ExecuteScalar(), CultureInfo.InvariantCulture) != 5)
        {
            throw new InvalidOperationException(
                "State schema has pending column migrations and cannot be opened safely by the successor.");
        }

        using var requiredObjects = conn.CreateCommand();
        requiredObjects.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE (type = 'table' AND name = 'engineering_practices')
               OR (type = 'table' AND name = 'state_outbox')
               OR (type = 'index' AND name IN (
                     'ix_goals_source_backlog_item_id',
                     'ix_goals_status',
                     'ix_model_fit_history_outcome_class',
                     'ix_state_outbox_kind'))
            """;
        if (Convert.ToInt32(requiredObjects.ExecuteScalar(), CultureInfo.InvariantCulture) != 6)
        {
            throw new InvalidOperationException(
                "State schema has pending table or index migrations and cannot be opened safely by the successor.");
        }

        return value;
    }

    internal SqliteOrchestratorStateRepository(string dbPath, Action<string>? statementObserver)
        : this(dbPath, statementObserver, telemetryOptions: null)
    {
    }

    internal SqliteOrchestratorStateRepository(
        string dbPath,
        Action<string>? statementObserver,
        SqliteWriteTelemetryOptions? telemetryOptions,
        Action? beforeOutboxCommit = null,
        Action<GoalSnapshotReadObservation>? readObserver = null)
        : this(
            dbPath,
            statementObserver,
            telemetryOptions,
            beforeOutboxCommit,
            StateDbConnectionProfile.ReadWrite,
            readObserver)
    {
    }

    private SqliteOrchestratorStateRepository(
        string dbPath,
        Action<string>? statementObserver,
        SqliteWriteTelemetryOptions? telemetryOptions,
        Action? beforeOutboxCommit,
        StateDbConnectionProfile connectionProfile,
        Action<GoalSnapshotReadObservation>? readObserver = null)
    {
        _dbPath = dbPath;
        _statementObserver = statementObserver;
        _writeTelemetry = new SqliteWriteTelemetry(dbPath, telemetryOptions);
        _beforeOutboxCommit = beforeOutboxCommit;
        _connectionProfile = connectionProfile;
        _readObserver = readObserver;
    }

    internal static string? AmbientWriteOperationTag => CurrentWriteOperationTag.Value;

    internal static IDisposable UseWriteOperationTag(string operationTag)
    {
        if (string.IsNullOrWhiteSpace(operationTag))
            throw new ArgumentException("Operation tag cannot be empty.", nameof(operationTag));

        var previous = CurrentWriteOperationTag.Value;
        CurrentWriteOperationTag.Value = operationTag.Trim();
        return new RestoreWriteOperationTag(previous);
    }

    private static string ResolveOperationTag(string fallback)
    {
        var ambient = CurrentWriteOperationTag.Value;
        if (string.IsNullOrWhiteSpace(ambient) ||
            fallback.Equals(ambient, StringComparison.Ordinal) ||
            fallback.StartsWith(ambient + "/", StringComparison.Ordinal))
        {
            return fallback;
        }

        return $"{ambient}/{fallback}";
    }

    private static string ResolveOperationTag(string fallback, string operationName)
    {
        if (string.IsNullOrWhiteSpace(operationName))
            throw new ArgumentException("Operation name cannot be empty.", nameof(operationName));

        var operation = operationName.Trim();
        var ambient = CurrentWriteOperationTag.Value;
        if (string.IsNullOrWhiteSpace(ambient) ||
            operation.Equals(ambient, StringComparison.Ordinal) ||
            operation.StartsWith(ambient + "/", StringComparison.Ordinal))
        {
            return operation;
        }

        return $"{ambient}/{operation}";
    }

    private sealed class RestoreWriteOperationTag : IDisposable
    {
        private readonly string? _previous;
        private bool _disposed;

        public RestoreWriteOperationTag(string? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            CurrentWriteOperationTag.Value = _previous;
            _disposed = true;
        }
    }

    // Pooling=False matters under WAL: a POOLED connection can be returned to the pool still holding
    // a WAL read/lock slot, so a later writer meets "database is locked" that busy_timeout cannot wait
    // out (it is not a plain lock-wait). The sibling CollaborationItemStore already does this; the
    // state repo did not, which let a goal-create issued during a conduct --loop tick crash the loop.
    private SqliteConnection OpenConnection(int? busyTimeoutMilliseconds = null) =>
        StateDbConnectionFactory.Open(
            _dbPath,
            _connectionProfile,
            busyTimeoutMilliseconds ?? _writeTelemetry.Options.BusyTimeoutMilliseconds,
            _statementObserver);

    // Shared bounded retry for transient SQLITE_BUSY/LOCKED during write-lock acquisition. Callers
    // must keep the operation limited to acquisition so a side-effecting transaction body is never
    // re-executed. busy_timeout handles simple lock waits; immediate BUSY still needs this backoff.
    internal static bool IsTransientLock(SqliteException ex) =>
        ex.SqliteErrorCode == 5 /* SQLITE_BUSY */ || ex.SqliteErrorCode == 6 /* SQLITE_LOCKED */;

    internal static bool IsMissingStateOutboxTable(SqliteException ex) =>
        ex.SqliteErrorCode == 1 &&
        ex.Message.Contains("no such table: state_outbox", StringComparison.OrdinalIgnoreCase);

    internal static async Task<T> WithBusyRetryAsync<T>(
        Func<Task<T>> operation,
        CancellationToken ct,
        TimeSpan? retryBudget = null,
        int maxBusyRetries = int.MaxValue,
        Func<int, TimeSpan, CancellationToken, Task>? retryDelay = null,
        Func<TimeSpan>? elapsed = null,
        Action<int, TimeSpan, SqliteException>? retryObserver = null)
    {
        var budget = retryBudget ?? TimeSpan.FromMilliseconds(StateDbConnectionFactory.DefaultBusyTimeoutMilliseconds);
        var stopwatch = Stopwatch.StartNew();
        TimeSpan Elapsed() => elapsed?.Invoke() ?? stopwatch.Elapsed;
        var delayMs = 50;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (SqliteException ex) when (IsTransientLock(ex))
            {
                var observedElapsed = Elapsed();
                if (attempt >= maxBusyRetries || observedElapsed >= budget)
                {
                    ex.Data["Mcg.AttemptCount"] = attempt;
                    ex.Data["Mcg.ElapsedMilliseconds"] = observedElapsed.TotalMilliseconds;
                    throw;
                }

                retryObserver?.Invoke(attempt, observedElapsed, ex);
                var remaining = budget - observedElapsed;
                var delay = TimeSpan.FromMilliseconds(Math.Min(delayMs, Math.Max(0, remaining.TotalMilliseconds)));
                if (delay <= TimeSpan.Zero)
                    throw;
                if (retryDelay is null)
                    await Task.Delay(delay, ct);
                else
                    await retryDelay(attempt, delay, ct);
                delayMs = Math.Min(delayMs * 2, 1000);
            }
        }
    }

    // Opens a connection and acquires the write lock (BEGIN IMMEDIATE) with bounded retry. Only the
    // lock ACQUISITION is retried — once it returns, the caller runs its body exactly once, so no
    // side-effecting transaction delegate is ever re-executed.
    private async Task<WriteConnection> BeginWriteAsync(string operation, CancellationToken cancellationToken)
    {
        var startedAt = _writeTelemetry.Options.MonotonicMilliseconds();
        TimeSpan Elapsed() => TimeSpan.FromMilliseconds(Math.Max(
            0,
            _writeTelemetry.Options.MonotonicMilliseconds() - startedAt));
        var attemptCount = 0;
        SqliteException? lastTransient = null;
        try
        {
            var retryBudget = _writeTelemetry.Options.BusyRetryBudget;
            var conn = await WithBusyRetryAsync(async () =>
            {
                attemptCount++;
                var remainingMilliseconds = Math.Max(
                    1,
                    (int)Math.Ceiling((retryBudget - Elapsed()).TotalMilliseconds));
                var conn = OpenConnection(remainingMilliseconds);
                try
                {
                    await RunNonQueryAsync(
                        conn,
                        "BEGIN IMMEDIATE",
                        cancellationToken,
                        _writeTelemetry.Options.BeginImmediateCommandTimeoutSeconds);
                    StateStoreFormatFence.Enforce(conn);
                    return conn;
                }
                catch
                {
                    await conn.DisposeAsync();
                    throw;
                }
            }, cancellationToken, retryBudget, _writeTelemetry.Options.MaxBusyRetries,
                _writeTelemetry.Options.RetryDelay,
                Elapsed,
                (attempt, elapsed, exception) =>
                {
                    lastTransient = exception;
                    _writeTelemetry.EmitBusyRetry(operation, elapsed, exception, attempt);
                });
            var acquisitionWait = Elapsed();
            if (lastTransient is not null)
                _writeTelemetry.EmitBusyRecovered(operation, acquisitionWait, lastTransient, attemptCount);
            return new WriteConnection(
                conn,
                _writeTelemetry.StartScope(operation, acquisitionWait));
        }
        catch (SqliteException ex) when (IsTransientLock(ex))
        {
            ex.Data["Mcg.StateStore"] = "state";
            ex.Data["Mcg.DatabasePath"] = Path.GetFullPath(_dbPath);
            ex.Data["Mcg.Operation"] = operation;
            ex.Data["Mcg.AttemptCount"] = Math.Max(1, attemptCount);
            ex.Data["Mcg.ElapsedMilliseconds"] = Elapsed().TotalMilliseconds;
            _writeTelemetry.EmitBusyFailure(operation, Elapsed(), ex, Math.Max(1, attemptCount));
            throw;
        }
    }

    private sealed record WriteConnection(
        SqliteConnection Connection,
        SqliteWriteTelemetry.WriteTelemetryScope Telemetry);

    internal void ApplyCoreSchemaMigration(SqliteConnection conn)
    {
        RunNonQuery(conn, GoalSnapshotArchiveSchemaSql);
        GoalTaskHistoryStore.CreateSchema(conn);
        GoalTimelineStore.CreateSchema(conn);
        if (CoreSchemaTablesAlreadyExist(conn))
        {
            MigrateVersionColumn(conn);
            if (!PracticeRegistrySchemaExists(conn))
                PracticeRegistryStore.ApplySchemaMigration(conn);
            if (!StateOutboxSchemaExists(conn))
                EnsureStateOutboxSchema(conn);
            MigrateStateOutboxColumns(conn);
            return;
        }

        RunNonQuery(conn, """
            CREATE TABLE IF NOT EXISTS meta (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            )
            """);
        RunNonQuery(conn, """
            CREATE TABLE IF NOT EXISTS goals (
                id            TEXT PRIMARY KEY,
                status        TEXT NOT NULL,
                objective     TEXT NOT NULL,
                source_backlog_item_id TEXT NULL,
                updated_at    TEXT NOT NULL,
                snapshot_json TEXT NOT NULL,
                version       INTEGER NOT NULL DEFAULT 0
            )
            """);
        RunNonQuery(conn, """
            CREATE TABLE IF NOT EXISTS human_input_requests (
                id            TEXT PRIMARY KEY,
                goal_id       TEXT NOT NULL,
                snapshot_json TEXT NOT NULL
            )
            """);
        RunNonQuery(conn, """
            CREATE TABLE IF NOT EXISTS model_fit_history (
                goal_id       TEXT NOT NULL,
                task_id       TEXT NOT NULL,
                role          TEXT NOT NULL,
                provider_name TEXT NOT NULL,
                model_name    TEXT NOT NULL,
                complexity    TEXT NULL,
                task_shape    TEXT NULL,
                outcome       TEXT NOT NULL,
                self_rating   TEXT NOT NULL,
                timestamp     TEXT NOT NULL,
                outcome_rule  TEXT NULL,
                outcome_class TEXT NOT NULL DEFAULT 'unknown-era',
                dispatch_lane TEXT NULL,
                PRIMARY KEY (goal_id, task_id, timestamp)
            )
            """);
        RunNonQuery(conn, "CREATE INDEX IF NOT EXISTS ix_model_fit_history_role ON model_fit_history(role)");
        RunNonQuery(conn, "CREATE INDEX IF NOT EXISTS ix_model_fit_history_model ON model_fit_history(provider_name, model_name)");
        RunNonQuery(conn, "CREATE INDEX IF NOT EXISTS ix_model_fit_history_outcome_class ON model_fit_history(outcome_class)");
        RunNonQuery(conn, "CREATE INDEX IF NOT EXISTS ix_goals_status ON goals(status)");
        RunNonQuery(conn, "CREATE INDEX IF NOT EXISTS ix_goals_source_backlog_item_id ON goals(source_backlog_item_id)");
        EnsureStateOutboxSchema(conn);
        RunNonQuery(conn, $"INSERT OR IGNORE INTO meta (key, value) VALUES ('schema_version', '{CurrentSchemaVersion}')");
        PracticeRegistryStore.ApplySchemaMigration(conn);
    }

    private void EnsureStateOutboxSchema(SqliteConnection conn)
    {
        RunNonQuery(conn, """
            CREATE TABLE IF NOT EXISTS state_outbox (
                id                TEXT PRIMARY KEY,
                kind              TEXT NOT NULL,
                payload_json      TEXT NOT NULL,
                created_at        TEXT NOT NULL,
                quarantined_at    TEXT NULL,
                quarantine_reason TEXT NULL,
                processing_token  TEXT NULL,
                processing_started_at TEXT NULL
            )
            """);
        RunNonQuery(conn, "CREATE INDEX IF NOT EXISTS ix_state_outbox_kind ON state_outbox(kind)");
    }

    internal void ApplyStateOutboxLeaseSchemaMigration(SqliteConnection conn)
    {
        MigrateStateOutboxColumns(conn);
        RunNonQuery(conn, "CREATE INDEX IF NOT EXISTS ix_state_outbox_kind ON state_outbox(kind)");
    }

    private void MigrateStateOutboxColumns(SqliteConnection conn)
    {
        AddColumnIfMissing(
            conn,
            "state_outbox",
            "quarantined_at",
            "ALTER TABLE state_outbox ADD COLUMN quarantined_at TEXT NULL");
        AddColumnIfMissing(
            conn,
            "state_outbox",
            "quarantine_reason",
            "ALTER TABLE state_outbox ADD COLUMN quarantine_reason TEXT NULL");
        AddColumnIfMissing(
            conn,
            "state_outbox",
            "processing_token",
            "ALTER TABLE state_outbox ADD COLUMN processing_token TEXT NULL");
        AddColumnIfMissing(
            conn,
            "state_outbox",
            "processing_started_at",
            "ALTER TABLE state_outbox ADD COLUMN processing_started_at TEXT NULL");
    }

    // Idempotent migration: adds metadata columns to existing schemas that pre-date them.
    // Old binaries ignore the extra columns; this binary treats absent version as 0.
    private void MigrateVersionColumn(SqliteConnection conn)
    {
        using var check = conn.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('goals') WHERE name = 'version'";
        if (Convert.ToInt32(check.ExecuteScalar()) == 0)
            RunNonQuery(conn, "ALTER TABLE goals ADD COLUMN version INTEGER NOT NULL DEFAULT 0");

        using var sourceCheck = conn.CreateCommand();
        sourceCheck.CommandText = "SELECT COUNT(*) FROM pragma_table_info('goals') WHERE name = 'source_backlog_item_id'";
        if (Convert.ToInt32(sourceCheck.ExecuteScalar()) == 0)
            RunNonQuery(conn, "ALTER TABLE goals ADD COLUMN source_backlog_item_id TEXT NULL");

        if (!IndexExists(conn, "ix_goals_source_backlog_item_id"))
            RunNonQuery(conn, "CREATE INDEX IF NOT EXISTS ix_goals_source_backlog_item_id ON goals(source_backlog_item_id)");

        if (!IndexExists(conn, "ix_goals_status"))
            RunNonQuery(conn, "CREATE INDEX IF NOT EXISTS ix_goals_status ON goals(status)");

        AddColumnIfMissing(conn, "model_fit_history", "outcome_rule", "ALTER TABLE model_fit_history ADD COLUMN outcome_rule TEXT NULL");
        AddColumnIfMissing(conn, "model_fit_history", "outcome_class", "ALTER TABLE model_fit_history ADD COLUMN outcome_class TEXT NOT NULL DEFAULT 'unknown-era'");
        AddColumnIfMissing(conn, "model_fit_history", "dispatch_lane", "ALTER TABLE model_fit_history ADD COLUMN dispatch_lane TEXT NULL");

        if (!IndexExists(conn, "ix_model_fit_history_outcome_class"))
            RunNonQuery(conn, "CREATE INDEX IF NOT EXISTS ix_model_fit_history_outcome_class ON model_fit_history(outcome_class)");
    }

    internal void ApplyModelFitHistoryBackfillMigration(SqliteConnection conn)
    {
        var kernel = LoadFromConnectionAsync(conn, goalIds: null, CancellationToken.None).GetAwaiter().GetResult();
        foreach (var row in ModelFitHistory.FromGoals(kernel.Goals))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE model_fit_history
                SET outcome_rule = $outcome_rule,
                    outcome_class = $outcome_class
                WHERE goal_id = $goal_id
                  AND task_id = $task_id
                  AND timestamp = $timestamp
                  AND (outcome_rule IS NOT $outcome_rule
                       OR outcome_class <> $outcome_class)
                """;
            cmd.Parameters.AddWithValue("$outcome_rule", row.OutcomeRule ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$outcome_class", TaskOutcomeClassifier.FormatClass(row.OutcomeClass));
            cmd.Parameters.AddWithValue("$goal_id", row.GoalId);
            cmd.Parameters.AddWithValue("$task_id", row.TaskId);
            cmd.Parameters.AddWithValue("$timestamp", row.Timestamp.ToString("O"));
            cmd.ExecuteNonQuery();
        }
    }

    private void AddColumnIfMissing(SqliteConnection conn, string tableName, string columnName, string alterSql)
    {
        using var check = conn.CreateCommand();
        check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{tableName}') WHERE name = $name";
        check.Parameters.AddWithValue("$name", columnName);
        if (Convert.ToInt32(check.ExecuteScalar()) == 0)
            RunNonQuery(conn, alterSql);
    }

    private static bool CoreSchemaTablesAlreadyExist(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'table'
              AND name IN ($meta, $goals, $human_input_requests, $model_fit_history)
            """;
        cmd.Parameters.AddWithValue("$meta", "meta");
        cmd.Parameters.AddWithValue("$goals", "goals");
        cmd.Parameters.AddWithValue("$human_input_requests", "human_input_requests");
        cmd.Parameters.AddWithValue("$model_fit_history", "model_fit_history");
        return Convert.ToInt32(cmd.ExecuteScalar()) == CoreSchemaTableNames.Length;
    }

    private static bool PracticeRegistrySchemaExists(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'engineering_practices'";
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    private static bool StateOutboxSchemaExists(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE (type = 'table' AND name = 'state_outbox')
               OR (type = 'index' AND name = 'ix_state_outbox_kind')
            """;
        return Convert.ToInt32(cmd.ExecuteScalar()) == 2;
    }

    private static bool IndexExists(SqliteConnection conn, string indexName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = $name";
        cmd.Parameters.AddWithValue("$name", indexName);
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    public async Task<AgentOrchestratorKernel> LoadAsync(CancellationToken cancellationToken = default)
    {
        return await WithBusyRetryAsync(async () =>
        {
            await using var conn = OpenConnection();
            return await LoadFromConnectionAsync(conn, goalIds: null, cancellationToken);
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<ProviderUsageGoalEvidence>?> ReadProviderHoldSeedEvidenceAsync(
        IReadOnlyCollection<GoalId> goalIds, CancellationToken cancellationToken = default)
    {
        return await WithBusyRetryAsync(async () =>
        {
            await using var connection = OpenConnection();
            if (_readObserver is null || goalIds.Count == 0)
                return await ProviderHoldSeedEvidenceStore.ReadAsync(connection, goalIds, SerializerOptions, cancellationToken, _readObserver);

            // Pin the marker/length query and the canonical evidence read to one SQLite snapshot.
            // This is observation only: no payload is copied into the measurement query.
            using var transaction = connection.BeginTransaction(deferred: true);
            var lengths = await ReadProviderEvidenceLengthsAsync(connection, goalIds, cancellationToken);
            foreach (var row in lengths.Where(row => row.Value.Stored is not null))
                _readObserver(new("provider-evidence-stored", row.Key, row.Value.Stored!.Value));
            return await ProviderHoldSeedEvidenceStore.ReadAsync(connection, goalIds, SerializerOptions, cancellationToken,
                observation =>
                {
                    if (observation.Operation == "provider-seed-snapshot-decode" && observation.GoalId is { } id &&
                        lengths.TryGetValue(id, out var length))
                        _readObserver(new("provider-evidence-decoded", id, length.Snapshot));
                    _readObserver(observation);
                });
        }, cancellationToken);
    }

    private static async Task<Dictionary<string, (long Snapshot, long? Stored)>> ReadProviderEvidenceLengthsAsync(
        SqliteConnection connection, IReadOnlyCollection<GoalId> ids, CancellationToken token)
    {
        var hasArchive = await ProviderHoldSeedEvidenceStore.HasArchiveAsync(connection, token);
        using var schema = connection.CreateCommand();
        schema.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type = 'table' AND name = 'provider_hold_seed_evidence'";
        var hasEvidence = (long)(await schema.ExecuteScalarAsync(token))! != 0;
        using var command = connection.CreateCommand();
        var payload = hasArchive
            ? "CASE WHEN json_extract(g.snapshot_json, '$.IsMetadataOnly') = 1 AND a.snapshot_json IS NOT NULL THEN a.snapshot_json ELSE g.snapshot_json END"
            : "g.snapshot_json";
        command.CommandText = $"""
            SELECT g.id, length({payload}), {(hasEvidence ? "length(e.evidence_json)" : "NULL")}
            FROM goals g
            {(hasArchive ? "LEFT JOIN goal_snapshot_archive a ON a.goal_id = g.id" : "")}
            {(hasEvidence ? $"LEFT JOIN provider_hold_seed_evidence e ON e.goal_id = g.id AND e.format_version = {ProviderHoldSeedEvidenceStore.FormatVersion} AND e.marker = {ProviderHoldSeedEvidenceStore.MarkerSql(hasArchive)}" : "")}
            WHERE g.id IN (SELECT value FROM json_each($ids))
            """;
        command.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(ids.Select(id => id.Value)));
        var result = new Dictionary<string, (long, long?)>(StringComparer.Ordinal);
        using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            result[reader.GetString(0)] = (reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetInt64(2));
        return result;
    }

    public Task<AgentOrchestratorKernel> LoadGoalsAsync(
        IReadOnlyCollection<GoalId> goalIds,
        CancellationToken cancellationToken = default) => LoadSelectedGoalsAsync(goalIds, includePractices: true, cancellationToken);

    public Task<AgentOrchestratorKernel> LoadStatusGoalsAsync(IReadOnlyCollection<GoalId> goalIds,
        CancellationToken cancellationToken = default) => LoadSelectedGoalsAsync(goalIds, includePractices: false, cancellationToken);

    private async Task<AgentOrchestratorKernel> LoadSelectedGoalsAsync(IReadOnlyCollection<GoalId> goalIds,
        bool includePractices, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(goalIds);
        foreach (var id in goalIds)
            _readObserver?.Invoke(new("requested", id.Value));
        return await WithBusyRetryAsync(async () =>
        {
            await using var conn = OpenConnection();
            return await LoadFromConnectionAsync(conn, goalIds, cancellationToken, _readObserver, includePractices);
        }, cancellationToken);
    }

    internal const string GoalVersionQuery = "goal-version-query";
    internal const string ChangedSnapshotRead = "changed-snapshot-read";
    internal const string HumanInputRequestLoad = "human-input-request-load";
    internal const string KernelBuild = "kernel-build";
    internal const string EngineeringPracticeLoad = "engineering-practice-load";

    private readonly struct ReloadTimingScope(Action<string, TimeSpan>? report, string name) : IDisposable
    {
        private readonly long _startedAt = report is null ? 0 : Stopwatch.GetTimestamp();

        public void Dispose() => report?.Invoke(name, Stopwatch.GetElapsedTime(_startedAt));
    }

    internal async Task<(AgentOrchestratorKernel Kernel, int Reloaded, int Reused)> LoadGoalsReusingUnchangedAsync(
        IReadOnlyCollection<GoalId> goalIds, GoalReloadMemory memory, CancellationToken cancellationToken = default,
        Action<string, TimeSpan>? timing = null)
    {
        foreach (var id in goalIds)
            _readObserver?.Invoke(new("requested", id.Value));

        return await WithBusyRetryAsync(async () =>
        {
            await using var connection = OpenConnection();
            // The probe, documents and split rows must describe the same committed state.
            using var transaction = connection.BeginTransaction(deferred: true);
            List<(string Id, long Version)> versions;
            using (new ReloadTimingScope(timing, GoalVersionQuery))
                versions = await ReadSelectedGoalVersionsAsync(connection, goalIds, cancellationToken);
            var previous = memory.Entries;
            var next = ImmutableDictionary.CreateBuilder<string, GoalReloadMemory.Entry>(StringComparer.Ordinal);
            var changed = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var (id, version) in versions)
            {
                if (previous.TryGetValue(id, out var entry) && entry.Version == version)
                {
                    next.Add(id, entry);
                    _readObserver?.Invoke(new("version-reused", id));
                }
                else
                    changed.Add(id, version);
            }

            var reused = next.Count;
            Dictionary<string, GoalSnapshot> reloaded;
            using (new ReloadTimingScope(timing, ChangedSnapshotRead))
                reloaded = await ReadGoalSnapshotsAsync(
                    connection, changed.Keys.Select(id => new GoalId(id)).ToArray(), cancellationToken, _readObserver,
                    (conn, snapshot, markers, token) => ProjectTickGoalAsync(conn, snapshot, markers, changed[snapshot.Id], token));
            var snapshots = new List<GoalSnapshot>(reused + reloaded.Count);
            foreach (var (id, version) in versions)
            {
                if (next.TryGetValue(id, out var entry))
                    snapshots.Add(entry.Snapshot);
                else if (reloaded.TryGetValue(id, out var goal))
                {
                    snapshots.Add(goal);
                    if (!goal.IsMetadataOnly)
                        next.Add(id, new(version, goal));
                }
            }

            var kernel = await LoadFromConnectionAsync(connection, goalIds, cancellationToken, _readObserver,
                preloadedGoals: snapshots, timing: timing);
            transaction.Commit();
            memory.Replace(next.ToImmutable());
            return (kernel, reloaded.Count, reused);
        }, cancellationToken);
    }

    private static async Task<List<(string Id, long Version)>> ReadSelectedGoalVersionsAsync(
        SqliteConnection connection, IReadOnlyCollection<GoalId> goalIds, CancellationToken token)
    {
        var versions = new List<(string, long)>();
        if (goalIds.Count == 0) return versions;

        await using var command = CreateGoalReadCommand(connection, "id, COALESCE(version, 0)", goalIds);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            versions.Add((reader.GetString(0), reader.GetInt64(1)));
        return versions;
    }

    public async Task<AgentOrchestratorKernel> LoadBoardSnapshotAsync(CancellationToken cancellationToken = default)
    {
        return await WithBusyRetryAsync(async () =>
        {
            await using var conn = OpenConnection();
            return await LoadFromConnectionAsync(conn, goalIds: null, cancellationToken);
        }, cancellationToken);
    }

    public async Task SaveAsync(AgentOrchestratorKernel kernel, CancellationToken cancellationToken = default)
    {
        await SaveAsync(ResolveOperationTag(nameof(SaveAsync)), kernel, cancellationToken);
    }

    public async Task SaveAsync(
        string operationName,
        AgentOrchestratorKernel kernel,
        CancellationToken cancellationToken = default)
    {
        var write = await BeginWriteAsync(ResolveOperationTag(nameof(SaveAsync), operationName), cancellationToken);
        await using var conn = write.Connection;
        var telemetry = write.Telemetry;
        try
        {
            await WriteSnapshotAsync(conn, kernel, telemetry, cancellationToken);
            await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
            telemetry.Emit("commit");
        }
        catch (Exception ex)
        {
            try { await RunNonQueryAsync(conn, "ROLLBACK", cancellationToken); } catch { }
            telemetry.Emit("rollback", ex);
            throw;
        }
    }

    public async Task<int> ArchiveTerminalGoalSnapshotsAsync(
        DateTimeOffset cutoff, long maxSnapshotBytes = 134_217_728, CancellationToken cancellationToken = default,
        bool compressSnapshots = false)
    {
        var write = await BeginWriteAsync(ResolveOperationTag(nameof(ArchiveTerminalGoalSnapshotsAsync)), cancellationToken);
        await using var conn = write.Connection;
        try
        {
            // Already-migrated databases do not replay the core schema migration.
            await RunNonQueryAsync(conn, GoalSnapshotArchiveSchemaSql, cancellationToken);
            var archivedGoalIds = new List<string>();
            var count = await SnapshotArchive.ArchiveAsync(
                conn, cutoff, maxSnapshotBytes, cancellationToken, compressSnapshots, archivedGoalIds);
            if (archivedGoalIds.Count > 0)
                await WriteAttentionArchiveMarkersInTransactionAsync(conn, archivedGoalIds, cancellationToken);
            await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
            write.Telemetry.Emit("commit");
            return count;
        }
        catch (Exception ex)
        {
            try { await RunNonQueryAsync(conn, "ROLLBACK", CancellationToken.None); } catch { }
            write.Telemetry.Emit("rollback", ex);
            throw;
        }
    }

    public async Task<GoalSnapshotArchiveRecompressionResult> RecompressArchivedGoalSnapshotsAsync(
        long maxSourceBytes = 67_108_864, CancellationToken cancellationToken = default)
    {
        var write = await BeginWriteAsync(ResolveOperationTag(nameof(RecompressArchivedGoalSnapshotsAsync)), cancellationToken);
        await using var conn = write.Connection;
        try
        {
            await RunNonQueryAsync(conn, GoalSnapshotArchiveSchemaSql, cancellationToken);
            var result = await GoalSnapshotArchiveRecompression.RecompressAsync(conn, maxSourceBytes, cancellationToken);
            await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
            write.Telemetry.Emit("commit");
            return result;
        }
        catch (Exception ex)
        {
            try { await RunNonQueryAsync(conn, "ROLLBACK", CancellationToken.None); } catch { }
            write.Telemetry.Emit("rollback", ex);
            throw;
        }
    }

    public async Task SaveGoalSnapshotsAsync(
        IReadOnlyCollection<GoalSnapshot> goals,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(goals);

        await SaveGoalSnapshotsAsync(
            ResolveOperationTag($"{nameof(SaveGoalSnapshotsAsync)}({goals.Count})"),
            goals,
            cancellationToken);
    }

    public async Task SaveGoalSnapshotsAsync(
        string operationName,
        IReadOnlyCollection<GoalSnapshot> goals,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(goals);

        if (goals.Count == 0)
            return;

        var waitingGoal = goals.FirstOrDefault(HasHumanWaitState);
        if (waitingGoal is not null)
        {
            throw new InvalidOperationException(
                $"Goal '{ShortGoalId(waitingGoal.Id)}' contains WaitingForHuman state; persist it with its human-input requests.");
        }

        var write = await BeginWriteAsync(
            ResolveOperationTag($"{nameof(SaveGoalSnapshotsAsync)}({goals.Count})", operationName),
            cancellationToken);
        await using var conn = write.Connection;
        var telemetry = write.Telemetry;
        try
        {
            var updatedAt = DateTimeOffset.UtcNow.ToString("O");
            // SQLite has no provider-level array/upsert binding here, so each row is still a
            // statement; the bulk boundary is one connection, one BEGIN IMMEDIATE, one COMMIT.
            foreach (var goal in goals)
            {
                var goalWrite = await UpsertGoalRowAsync(
                    conn,
                    goal,
                    updatedAt,
                    versionSql: "goals.version + 1",
                    cancellationToken,
                    skipUnchanged: true);
                telemetry.AddWrite(goalWrite);
                if (goalWrite.RowsWritten > 0)
                {
                    telemetry.AddWrite(await UpsertModelFitHistoryRowsAsync(conn, goal, cancellationToken));
                }
            }

            await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
            telemetry.Emit("commit");
        }
        catch (Exception ex)
        {
            try { await RunNonQueryAsync(conn, "ROLLBACK", cancellationToken); } catch { }
            telemetry.Emit("rollback", ex);
            throw;
        }
    }

    public async Task<IReadOnlyList<GoalSnapshotSaveResult>> SaveGoalSnapshotsWithMergeAsync(
        IReadOnlyCollection<GoalSnapshotSaveRequest> goals,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(goals);

        if (goals.Count == 0)
            return [];

        var results = new List<GoalSnapshotSaveResult>(goals.Count);
        foreach (var request in goals)
        {
            if (!string.Equals(request.Baseline.Id, request.Current.Id, StringComparison.Ordinal))
            {
                results.Add(new GoalSnapshotSaveResult(
                    request.Current.Id,
                    GoalSnapshotSaveDisposition.Skipped,
                    null,
                    "baseline/current goal id mismatch"));
                continue;
            }

            var result = await TransactGoalStateVersionedAsync(
                ResolveOperationTag($"{nameof(TransactGoalStateAsync)}({ShortGoalId(request.Current.Id)})"),
                new GoalId(request.Current.Id),
                (storedState, storedVersion, _) =>
                {
                    var storedSnapshot = storedState?.Goal;
                    if (storedSnapshot is null)
                    {
                        return Task.FromResult<(bool ShouldSave, GoalStateSnapshot? NewState, GoalSnapshotSaveResult Result)>(
                            (false, null, new GoalSnapshotSaveResult(
                                request.Current.Id,
                                GoalSnapshotSaveDisposition.Skipped,
                                null,
                                "goal row no longer exists")));
                    }

                    // Refresh may carry committed authority newer than the checkpoint but older
                    // than the stored row. Merge that progress using only the stored authority;
                    // a tick cannot propose a newer revision or contradict the committed action.
                    var tickDisposition = request.Current.ExplicitDisposition;
                    var storedDisposition = storedSnapshot.ExplicitDisposition;
                    if (tickDisposition is not null &&
                        (storedDisposition is null || tickDisposition.Revision > storedDisposition.Revision ||
                         (tickDisposition.Revision == storedDisposition.Revision &&
                          tickDisposition.Action != storedDisposition.Action)))
                    {
                        return Task.FromResult<(bool ShouldSave, GoalStateSnapshot? NewState, GoalSnapshotSaveResult Result)>(
                            (false, null, new GoalSnapshotSaveResult(request.Current.Id,
                                GoalSnapshotSaveDisposition.Skipped, storedSnapshot,
                                "tick cannot change explicit operator disposition", storedState!.HumanInputRequests)));
                    }

                    var baseline = CompleteTickSnapshot(request.Baseline, storedSnapshot, storedVersion);
                    var current = CompleteTickSnapshot(request.Current, storedSnapshot, storedVersion, request.Baseline);
                    if (tickDisposition == storedDisposition &&
                        (request.Baseline.TickRowVersion is null || request.Baseline.TickRowVersion == storedVersion) &&
                        SnapshotEquals(RefinedSpecSnapshotAccess.NormalizeForStorage(storedSnapshot),
                            RefinedSpecSnapshotAccess.NormalizeForStorage(baseline)))
                    {
                        var state = ValidateConsistentGoalState(
                            RefinedSpecSnapshotAccess.NormalizeForStorage(current),
                            MergeHumanInputRequests(storedState!.HumanInputRequests, request.HumanInputRequests));
                        return Task.FromResult<(bool ShouldSave, GoalStateSnapshot? NewState, GoalSnapshotSaveResult Result)>(
                            (true, state, new GoalSnapshotSaveResult(
                                request.Current.Id,
                                GoalSnapshotSaveDisposition.Saved,
                                state.Goal,
                                "stored version matched tick baseline",
                                state.HumanInputRequests)));
                    }

                    if (request.RejectConflict)
                    {
                        return Task.FromResult<(bool ShouldSave, GoalStateSnapshot? NewState, GoalSnapshotSaveResult Result)>(
                            (false, null, new GoalSnapshotSaveResult(
                                request.Current.Id,
                                GoalSnapshotSaveDisposition.Skipped,
                                storedSnapshot,
                                "stored version advanced during critical dispatch checkpoint; rejected stale tick snapshot",
                                storedState!.HumanInputRequests)));
                    }

                    var preserveTimelineOrder = request.Baseline.TickRowVersion is not null || request.Current.TickRowVersion is not null;
                    if (!TryMergeGoalSnapshots(baseline, storedSnapshot, current, preserveTimelineOrder, out var merged, out var reason))
                    {
                        return Task.FromResult<(bool ShouldSave, GoalStateSnapshot? NewState, GoalSnapshotSaveResult Result)>(
                            (false, null, new GoalSnapshotSaveResult(
                                request.Current.Id,
                                GoalSnapshotSaveDisposition.Skipped,
                                storedSnapshot,
                                reason,
                                storedState!.HumanInputRequests)));
                    }

                    var normalized = NormalizeStoredVerificationStatus(merged, preserveTimelineOrder, out var normalizedReason);
                    var resultReason = normalizedReason is null ? reason : $"{reason}; {normalizedReason}";
                    var mergedState = ValidateConsistentGoalState(
                        normalized,
                        MergeHumanInputRequests(storedState!.HumanInputRequests, request.HumanInputRequests));
                    return Task.FromResult<(bool ShouldSave, GoalStateSnapshot? NewState, GoalSnapshotSaveResult Result)>(
                        (true, mergedState, new GoalSnapshotSaveResult(
                            request.Current.Id,
                            GoalSnapshotSaveDisposition.Merged,
                            mergedState.Goal,
                            resultReason,
                            mergedState.HumanInputRequests)));
                },
                cancellationToken);
            results.Add(result);
        }

        return results;
    }

    public async Task<IReadOnlyList<GoalSnapshotCheckpointResult>> CheckpointGoalSnapshotsAsync(
        IReadOnlyCollection<GoalSnapshotSaveRequest> goals,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(goals);
        var results = new List<GoalSnapshotCheckpointResult>(goals.Count);
        foreach (var request in goals)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var operation = ResolveOperationTag(
                $"{nameof(TransactGoalStateAsync)}({ShortGoalId(request.Current.Id)})");
            try
            {
                var saveResult = (await SaveGoalSnapshotsWithMergeAsync([request], cancellationToken)).Single();
                results.Add(new GoalSnapshotCheckpointResult(
                    request.Current.Id,
                    GoalSnapshotCheckpointDisposition.Durable,
                    saveResult,
                    "state",
                    Path.GetFullPath(_dbPath),
                    operation));
            }
            catch (SqliteException ex) when (IsTransientLock(ex))
            {
                results.Add(new GoalSnapshotCheckpointResult(
                    request.Current.Id,
                    GoalSnapshotCheckpointDisposition.Held,
                    SaveResult: null,
                    Store: ReadExceptionData(ex, "Mcg.StateStore") ?? "state",
                    DatabasePath: ReadExceptionData(ex, "Mcg.DatabasePath") ?? Path.GetFullPath(_dbPath),
                    Operation: ReadExceptionData(ex, "Mcg.Operation") ?? operation,
                    SqliteErrorCode: ex.SqliteErrorCode,
                    SqliteExtendedErrorCode: ex.SqliteExtendedErrorCode,
                    AttemptCount: ReadExceptionInt(ex, "Mcg.AttemptCount", _writeTelemetry.Options.MaxBusyRetries),
                    ElapsedMilliseconds: ReadExceptionDouble(ex, "Mcg.ElapsedMilliseconds")));
            }
        }

        return results;
    }

    private static string? ReadExceptionData(Exception exception, string key) =>
        exception.Data.Contains(key) ? exception.Data[key]?.ToString() : null;

    private static int ReadExceptionInt(Exception exception, string key, int fallback) =>
        exception.Data.Contains(key) && int.TryParse(exception.Data[key]?.ToString(), out var value)
            ? value
            : fallback;

    private static double ReadExceptionDouble(Exception exception, string key) =>
        exception.Data.Contains(key) && double.TryParse(
            exception.Data[key]?.ToString(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : 0;

    private static bool HasHumanWaitState(GoalSnapshot goal) =>
        goal.Status == GoalStatus.WaitingForHuman ||
        goal.Tasks.Any(task => task.Status == WorkTaskStatus.WaitingForHuman);

    private static IReadOnlyList<HumanInputRequestSnapshot> MergeHumanInputRequests(
        IReadOnlyList<HumanInputRequestSnapshot> stored,
        IReadOnlyList<HumanInputRequestSnapshot>? current)
    {
        var merged = stored.ToDictionary(request => request.Id, StringComparer.Ordinal);
        foreach (var request in current ?? [])
        {
            var typedExisting = request.PlannerIdentity is null ? null : merged.Values.FirstOrDefault(item => item.PlannerIdentity == request.PlannerIdentity);
            if (typedExisting is not null && !request.PlannerEvidence!.SemanticallyEquals(typedExisting.PlannerEvidence))
                throw new InvalidOperationException("Conflicting Planner evidence payload in snapshot merge.");
            var mergeId = typedExisting?.Id ?? request.Id;
            if (!merged.TryGetValue(mergeId, out var existing))
            {
                merged[request.Id] = request;
                continue;
            }

            var newerAmendmentAnswer = request.Kind == HumanWaitKind.PlannerBriefAmendment && request.Amendment is not null &&
                (request.AnswerHistory?.Count ?? 0) > (existing.AnswerHistory?.Count ?? 0);
            var winner = newerAmendmentAnswer ? request : existing.IsCompleted
                ? existing
                : request.IsCompleted || request.SuppressionCount > existing.SuppressionCount
                    ? request
                    : existing;
            var suppressionWinner = CompareSuppressionVersion(request, existing) switch
            {
                > 0 => request,
                < 0 => existing,
                _ => request.SuppressionCount > existing.SuppressionCount ? request : existing
            };
            merged[mergeId] = winner with
            {
                Id = mergeId,
                AmendmentReplanRouted = request.AmendmentReplanRouted || existing.AmendmentReplanRouted,
                SuppressionCount = suppressionWinner.SuppressionCount,
                SuppressionAnswerRevision = suppressionWinner.SuppressionAnswerRevision,
                SuppressionRevision = suppressionWinner.SuppressionRevision
            };
        }

        return merged.Values.ToArray();
    }

    private static int CompareSuppressionVersion(
        HumanInputRequestSnapshot left,
        HumanInputRequestSnapshot right)
    {
        var answerRevision = left.SuppressionAnswerRevision.CompareTo(right.SuppressionAnswerRevision);
        return answerRevision != 0
            ? answerRevision
            : left.SuppressionRevision.CompareTo(right.SuppressionRevision);
    }

    private static GoalStateSnapshot ValidateConsistentGoalState(
        GoalSnapshot goal,
        IReadOnlyList<HumanInputRequestSnapshot> humanInputRequests)
    {
        var openRequests = humanInputRequests
            .Where(request => !request.IsCompleted)
            .ToArray();
        foreach (var task in goal.Tasks.Where(task => task.Status == WorkTaskStatus.WaitingForHuman))
        {
            if (!openRequests.Any(request => string.Equals(request.TaskId, task.Id, StringComparison.Ordinal)) &&
                !humanInputRequests.Any(request => request.TaskId == task.Id &&
                    task.RequiredRole == AgentRole.Planner && request.Kind == HumanWaitKind.PlannerBriefAmendment &&
                    request.Amendment is not null && !request.AmendmentReplanRouted && request.IsCompleted && !request.WasDismissed &&
                    request.AnswerHistory?.Any(answer => !answer.IsRetracted) == true &&
                    (task.LatestRetryAt is null || request.RequestedAt >= task.LatestRetryAt)))
            {
                throw new InvalidOperationException(
                    $"Goal '{ShortGoalId(goal.Id)}' task '{task.Id[..Math.Min(8, task.Id.Length)]}' " +
                    "is WaitingForHuman but has no open human-input request.");
            }
        }

        if (goal.Status == GoalStatus.WaitingForHuman && openRequests.Length == 0)
        {
            throw new InvalidOperationException(
                $"Goal '{ShortGoalId(goal.Id)}' is WaitingForHuman but has no open human-input request.");
        }

        return new GoalStateSnapshot(goal, humanInputRequests);
    }

    public async Task<T> TransactAsync<T>(
        Func<AgentOrchestratorKernel, CancellationToken, Task<(bool ShouldSave, T Result)>> transaction,
        CancellationToken cancellationToken = default)
    {
        return await TransactAsync(
            ResolveOperationTag(nameof(TransactAsync)),
            transaction,
            cancellationToken);
    }

    public async Task<T> TransactAsync<T>(
        string operationName,
        Func<AgentOrchestratorKernel, CancellationToken, Task<(bool ShouldSave, T Result)>> transaction,
        CancellationToken cancellationToken = default)
    {
        return await TransactAsync(
            ResolveOperationTag(nameof(TransactAsync), operationName),
            async (kernel, _, token) => await transaction(kernel, token),
            cancellationToken);
    }

    public async Task<T> TransactAsync<T>(
        Func<AgentOrchestratorKernel, Func<Task>, CancellationToken, Task<(bool ShouldSave, T Result)>> transaction,
        CancellationToken cancellationToken = default)
    {
        return await TransactAsync(ResolveOperationTag(nameof(TransactAsync)), transaction, cancellationToken);
    }

    public async Task<T> TransactAsync<T>(
        string operationName,
        Func<AgentOrchestratorKernel, Func<Task>, CancellationToken, Task<(bool ShouldSave, T Result)>> transaction,
        CancellationToken cancellationToken = default)
    {
        var write = await BeginWriteAsync(ResolveOperationTag(nameof(TransactAsync), operationName), cancellationToken);
        await using var conn = write.Connection;
        var telemetry = write.Telemetry;
        T result = default!;
        StateDbCommitBeforeRethrowException? commitBeforeRethrow = null;
        try
        {
            var kernel = await LoadFromConnectionAsync(conn, goalIds: null, cancellationToken);

            async Task CheckpointAsync()
            {
                await WriteSnapshotAsync(conn, kernel, telemetry, cancellationToken);
            }

            bool shouldSave;
            try
            {
                using (StateDbWriteSession.Enter(_dbPath, conn))
                {
                    (shouldSave, result) = await transaction(kernel, CheckpointAsync, cancellationToken);
                }
            }
            catch (StateDbCommitBeforeRethrowException ex)
            {
                shouldSave = true;
                commitBeforeRethrow = ex;
            }

            if (shouldSave)
                await WriteSnapshotAsync(conn, kernel, telemetry, cancellationToken);

            await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
            telemetry.Emit("commit");
        }
        catch (Exception ex)
        {
            try { await RunNonQueryAsync(conn, "ROLLBACK", cancellationToken); } catch { }
            telemetry.Emit("rollback", ex);
            throw;
        }

        if (commitBeforeRethrow is not null)
            throw commitBeforeRethrow.CreatePostCommitException();

        return result;
    }

    public async Task<T> TransactWithOutboxAsync<T>(
        Func<AgentOrchestratorKernel, CancellationToken, Task<(
            bool ShouldSave,
            T Result,
            IReadOnlyList<OrchestratorStateOutboxMessage> OutboxMessages)>> transaction,
        CancellationToken cancellationToken = default)
    {
        var write = await BeginWriteAsync(
            ResolveOperationTag(nameof(TransactWithOutboxAsync)),
            cancellationToken);
        await using var conn = write.Connection;
        var telemetry = write.Telemetry;
        T result = default!;
        StateDbCommitBeforeRethrowException? commitBeforeRethrow = null;
        try
        {
            var kernel = await LoadFromConnectionAsync(conn, goalIds: null, cancellationToken);
            var shouldSave = false;
            IReadOnlyList<OrchestratorStateOutboxMessage> outboxMessages = [];
            try
            {
                using (StateDbWriteSession.Enter(_dbPath, conn))
                {
                    (shouldSave, result, outboxMessages) = await transaction(kernel, cancellationToken);
                }
            }
            catch (StateDbCommitBeforeRethrowException ex)
            {
                shouldSave = true;
                commitBeforeRethrow = ex;
            }

            if (shouldSave)
                await WriteSnapshotAsync(conn, kernel, telemetry, cancellationToken);

            foreach (var message in outboxMessages)
            {
                await InsertOutboxMessageAsync(conn, message, cancellationToken);
            }

            _beforeOutboxCommit?.Invoke();
            await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
            telemetry.Emit("commit");
        }
        catch (Exception ex)
        {
            try { await RunNonQueryAsync(conn, "ROLLBACK", cancellationToken); } catch { }
            telemetry.Emit("rollback", ex);
            throw;
        }

        if (commitBeforeRethrow is not null)
            throw commitBeforeRethrow.CreatePostCommitException();

        return result;
    }

    public async Task<IReadOnlyList<OrchestratorStateOutboxMessage>> ListOutboxMessagesAsync(
        string kind,
        CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, kind, payload_json, created_at
            FROM state_outbox
            WHERE kind = $kind AND quarantined_at IS NULL
            ORDER BY created_at, id
            """;
        cmd.Parameters.AddWithValue("$kind", kind);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var messages = new List<OrchestratorStateOutboxMessage>();
        while (await reader.ReadAsync(cancellationToken))
        {
            messages.Add(new OrchestratorStateOutboxMessage(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        }

        return messages;
    }

    public Task<OrchestratorStateOutboxState?> GetOutboxStateAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        OrchestratorStateOutboxState? ambientState = null;
        try
        {
            if (StateDbWriteSession.TryExecute(
                    _dbPath,
                    connection => ambientState = ReadOutboxState(connection, id)))
            {
                return Task.FromResult(ambientState);
            }
        }
        catch (SqliteException ex) when (IsMissingStateOutboxTable(ex))
        {
            return Task.FromResult<OrchestratorStateOutboxState?>(null);
        }

        return GetOutboxStateCoreAsync(id, cancellationToken);
    }

    public Task<OrchestratorStateOutboxEnsureResult> EnsureOutboxMessageAsync(
        OrchestratorStateOutboxMessage message,
        CancellationToken cancellationToken = default)
    {
        OrchestratorStateOutboxEnsureResult? ambientResult = null;
        if (StateDbWriteSession.TryExecute(
                _dbPath,
                connection => ambientResult = EnsureOutboxMessage(connection, message)))
        {
            return Task.FromResult(ambientResult!);
        }

        return EnsureOutboxMessageCoreAsync(message, cancellationToken);
    }

    public async Task<bool> TryProcessOutboxMessageAsync(
        string id,
        Func<OrchestratorStateOutboxMessage, CancellationToken, Task<OrchestratorStateOutboxProcessingResult>> processor,
        CancellationToken cancellationToken = default)
    {
        var processingToken = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var message = await TryClaimOutboxMessageAsync(id, processingToken, cancellationToken);
        if (message is null)
            return false;

        try
        {
            // External delivery must never run while state.db has a write transaction open.
            var result = await processor(message, cancellationToken);
            await FinalizeOutboxMessageAsync(id, processingToken, result, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            try
            {
                await ReleaseOutboxMessageClaimAsync(
                    id,
                    processingToken,
                    SingleLine(ex.Message),
                    CancellationToken.None);
            }
            catch
            {
                // Preserve the delivery failure. The persisted lease makes a hard-crash or
                // release failure recoverable after its bounded expiry.
            }

            throw;
        }
    }

    private async Task<OrchestratorStateOutboxMessage?> TryClaimOutboxMessageAsync(
        string id,
        string processingToken,
        CancellationToken cancellationToken)
    {
        var write = await BeginWriteAsync(
            ResolveOperationTag(nameof(TryProcessOutboxMessageAsync)),
            cancellationToken);
        await using var conn = write.Connection;
        var telemetry = write.Telemetry;
        try
        {
            OrchestratorStateOutboxMessage? message;
            await using (var select = conn.CreateCommand())
            {
                select.CommandText = """
                    SELECT id, kind, payload_json, created_at
                    FROM state_outbox
                    WHERE id = $id
                      AND quarantined_at IS NULL
                      AND (processing_token IS NULL OR processing_started_at IS NULL OR processing_started_at <= $stale_before)
                    """;
                select.Parameters.AddWithValue("$id", id);
                select.Parameters.AddWithValue(
                    "$stale_before",
                    DateTimeOffset.UtcNow.Subtract(OutboxProcessingLease).ToString("O", CultureInfo.InvariantCulture));
                await using var reader = await select.ExecuteReaderAsync(cancellationToken);
                message = await reader.ReadAsync(cancellationToken)
                    ? new OrchestratorStateOutboxMessage(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        DateTimeOffset.Parse(
                            reader.GetString(3),
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind))
                    : null;
            }

            if (message is null)
            {
                await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
                telemetry.Emit("commit");
                return null;
            }

            await using var claim = conn.CreateCommand();
            claim.CommandText = """
                UPDATE state_outbox
                SET processing_token = $processing_token,
                    processing_started_at = $processing_started_at
                WHERE id = $id
                """;
            claim.Parameters.AddWithValue("$id", id);
            claim.Parameters.AddWithValue("$processing_token", processingToken);
            claim.Parameters.AddWithValue(
                "$processing_started_at",
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            if (await claim.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException($"Outbox message '{id}' disappeared while being claimed.");

            await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
            telemetry.Emit("commit");
            return message;
        }
        catch (Exception ex)
        {
            try { await RunNonQueryAsync(conn, "ROLLBACK", cancellationToken); } catch { }
            telemetry.Emit("rollback", ex);
            throw;
        }
    }

    private async Task FinalizeOutboxMessageAsync(
        string id,
        string processingToken,
        OrchestratorStateOutboxProcessingResult result,
        CancellationToken cancellationToken)
    {
        var write = await BeginWriteAsync(
            ResolveOperationTag(nameof(TryProcessOutboxMessageAsync)),
            cancellationToken);
        await using var conn = write.Connection;
        var telemetry = write.Telemetry;
        try
        {
            await using var cmd = conn.CreateCommand();
            if (result.Disposition == OrchestratorStateOutboxDisposition.Complete)
            {
                cmd.CommandText = "DELETE FROM state_outbox WHERE id = $id AND processing_token = $processing_token";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$processing_token", processingToken);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(result.Detail))
                {
                    throw new InvalidOperationException(
                        $"Outbox message '{id}' cannot be quarantined without a reason.");
                }

                cmd.CommandText = """
                    UPDATE state_outbox
                    SET quarantined_at = $quarantined_at,
                        quarantine_reason = $quarantine_reason,
                        processing_token = NULL,
                        processing_started_at = NULL
                    WHERE id = $id AND processing_token = $processing_token
                    """;
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$processing_token", processingToken);
                cmd.Parameters.AddWithValue(
                    "$quarantined_at",
                    DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("$quarantine_reason", result.Detail);
            }

            if (await cmd.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException(
                    $"Outbox message '{id}' lost its processing claim before finalization.");
            }
            await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
            telemetry.Emit("commit");
        }
        catch (Exception ex)
        {
            try { await RunNonQueryAsync(conn, "ROLLBACK", cancellationToken); } catch { }
            telemetry.Emit("rollback", ex);
            throw;
        }
    }

    private async Task ReleaseOutboxMessageClaimAsync(
        string id,
        string processingToken,
        string failureDetail,
        CancellationToken cancellationToken)
    {
        var write = await BeginWriteAsync(
            ResolveOperationTag(nameof(TryProcessOutboxMessageAsync)),
            cancellationToken);
        await using var conn = write.Connection;
        var telemetry = write.Telemetry;
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE state_outbox
                SET processing_token = NULL,
                    quarantine_reason = $failure_detail
                WHERE id = $id AND processing_token = $processing_token
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$processing_token", processingToken);
            cmd.Parameters.AddWithValue("$failure_detail", failureDetail);
            _ = await cmd.ExecuteNonQueryAsync(cancellationToken);
            await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
            telemetry.Emit("commit");
        }
        catch (Exception ex)
        {
            try { await RunNonQueryAsync(conn, "ROLLBACK", cancellationToken); } catch { }
            telemetry.Emit("rollback", ex);
            throw;
        }
    }

    private static async Task InsertOutboxMessageAsync(
        SqliteConnection conn,
        OrchestratorStateOutboxMessage message,
        CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO state_outbox (id, kind, payload_json, created_at)
            VALUES ($id, $kind, $payload_json, $created_at)
            ON CONFLICT(id) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("$id", message.Id);
        cmd.Parameters.AddWithValue("$kind", message.Kind);
        cmd.Parameters.AddWithValue("$payload_json", message.PayloadJson);
        cmd.Parameters.AddWithValue("$created_at", message.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<OrchestratorStateOutboxState?> GetOutboxStateCoreAsync(
        string id,
        CancellationToken cancellationToken)
    {
        await using var conn = OpenConnection();
        try
        {
            await using var cmd = BuildOutboxStateCommand(conn, id);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadOutboxState(reader) : null;
        }
        catch (SqliteException ex) when (IsMissingStateOutboxTable(ex))
        {
            return null;
        }
    }

    private async Task<OrchestratorStateOutboxEnsureResult> EnsureOutboxMessageCoreAsync(
        OrchestratorStateOutboxMessage message,
        CancellationToken cancellationToken)
    {
        var write = await BeginWriteAsync(
            ResolveOperationTag(nameof(EnsureOutboxMessageAsync)),
            cancellationToken);
        await using var conn = write.Connection;
        var telemetry = write.Telemetry;
        try
        {
            var inserted = await InsertOutboxMessageIfMissingAsync(conn, message, cancellationToken);
            var state = await ReadOutboxStateAsync(conn, message.Id, cancellationToken)
                ?? throw new InvalidOperationException($"Outbox message '{message.Id}' disappeared after ensure.");
            await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
            telemetry.Emit("commit");
            return new OrchestratorStateOutboxEnsureResult(
                inserted
                    ? OrchestratorStateOutboxEnsureDisposition.Acquired
                    : ToEnsureDisposition(state.Status),
                state);
        }
        catch (Exception ex)
        {
            try { await RunNonQueryAsync(conn, "ROLLBACK", cancellationToken); } catch { }
            telemetry.Emit("rollback", ex);
            throw;
        }
    }

    private static OrchestratorStateOutboxEnsureResult EnsureOutboxMessage(
        SqliteConnection connection,
        OrchestratorStateOutboxMessage message)
    {
        using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO state_outbox (id, kind, payload_json, created_at)
            VALUES ($id, $kind, $payload_json, $created_at)
            ON CONFLICT(id) DO NOTHING
            """;
        BindOutboxMessage(insert, message);
        var inserted = insert.ExecuteNonQuery() == 1;
        var state = ReadOutboxState(connection, message.Id)
            ?? throw new InvalidOperationException($"Outbox message '{message.Id}' disappeared after ensure.");
        return new OrchestratorStateOutboxEnsureResult(
            inserted
                ? OrchestratorStateOutboxEnsureDisposition.Acquired
                : ToEnsureDisposition(state.Status),
            state);
    }

    private static async Task<bool> InsertOutboxMessageIfMissingAsync(
        SqliteConnection connection,
        OrchestratorStateOutboxMessage message,
        CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO state_outbox (id, kind, payload_json, created_at)
            VALUES ($id, $kind, $payload_json, $created_at)
            ON CONFLICT(id) DO NOTHING
            """;
        BindOutboxMessage(cmd, message);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static void BindOutboxMessage(SqliteCommand command, OrchestratorStateOutboxMessage message)
    {
        command.Parameters.AddWithValue("$id", message.Id);
        command.Parameters.AddWithValue("$kind", message.Kind);
        command.Parameters.AddWithValue("$payload_json", message.PayloadJson);
        command.Parameters.AddWithValue("$created_at", message.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
    }

    private static OrchestratorStateOutboxState? ReadOutboxState(SqliteConnection connection, string id)
    {
        using var cmd = BuildOutboxStateCommand(connection, id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadOutboxState(reader) : null;
    }

    private static async Task<OrchestratorStateOutboxState?> ReadOutboxStateAsync(
        SqliteConnection connection,
        string id,
        CancellationToken cancellationToken)
    {
        await using var cmd = BuildOutboxStateCommand(connection, id);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadOutboxState(reader) : null;
    }

    private static SqliteCommand BuildOutboxStateCommand(SqliteConnection connection, string id)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, kind, payload_json, created_at,
                   quarantined_at, quarantine_reason, processing_token, processing_started_at
            FROM state_outbox
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", id);
        return cmd;
    }

    private static OrchestratorStateOutboxState ReadOutboxState(SqliteDataReader reader)
    {
        var message = new OrchestratorStateOutboxMessage(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
        var quarantinedAt = reader.IsDBNull(4) ? null : reader.GetString(4);
        var detail = reader.IsDBNull(5) ? null : reader.GetString(5);
        var processingToken = reader.IsDBNull(6) ? null : reader.GetString(6);
        var processingStartedAt = reader.IsDBNull(7)
            ? (DateTimeOffset?)null
            : DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var status = quarantinedAt is not null
            ? OrchestratorStateOutboxStatus.Quarantined
            : processingToken is not null
                ? OrchestratorStateOutboxStatus.Processing
                : detail is not null
                    ? OrchestratorStateOutboxStatus.Failed
                    : OrchestratorStateOutboxStatus.Pending;
        return new OrchestratorStateOutboxState(message, status, detail, processingStartedAt);
    }

    private static OrchestratorStateOutboxEnsureDisposition ToEnsureDisposition(
        OrchestratorStateOutboxStatus status) =>
        status switch
        {
            OrchestratorStateOutboxStatus.Pending => OrchestratorStateOutboxEnsureDisposition.Existing,
            OrchestratorStateOutboxStatus.Processing => OrchestratorStateOutboxEnsureDisposition.Processing,
            OrchestratorStateOutboxStatus.Failed => OrchestratorStateOutboxEnsureDisposition.Failed,
            OrchestratorStateOutboxStatus.Quarantined => OrchestratorStateOutboxEnsureDisposition.Quarantined,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
        };

    private static string SingleLine(string value) =>
        string.Concat(value.Select(character => char.IsWhiteSpace(character) ? ' ' : character)).Trim();

    public async Task<OrchestratorStateOutboxRequeueOutcome> RequeueOrInsertOutboxMessageAsync(
        OrchestratorStateOutboxMessage message,
        CancellationToken cancellationToken = default)
    {
        var write = await BeginWriteAsync(
            ResolveOperationTag(nameof(RequeueOrInsertOutboxMessageAsync)), cancellationToken);
        await using var conn = write.Connection;
        var telemetry = write.Telemetry;
        try
        {
            var outcome = RequeueOrInsertOutboxMessage(conn, message);
            await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
            telemetry.Emit("commit");
            return outcome;
        }
        catch (Exception ex)
        {
            try { await RunNonQueryAsync(conn, "ROLLBACK", cancellationToken); } catch { }
            telemetry.Emit("rollback", ex);
            throw;
        }
    }

    // The caller must hold the same write transaction used to claim outbox work.
    internal static OrchestratorStateOutboxRequeueOutcome RequeueOrInsertOutboxMessage(
        SqliteConnection connection,
        OrchestratorStateOutboxMessage message)
    {
        using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE state_outbox
            SET quarantined_at = NULL,
                quarantine_reason = NULL,
                processing_token = NULL,
                processing_started_at = NULL
            WHERE id = $id AND kind = $kind
              AND (processing_token IS NULL OR processing_started_at IS NULL OR processing_started_at <= $stale_before)
              AND (quarantined_at IS NOT NULL OR quarantine_reason IS NOT NULL
                   OR processing_token IS NOT NULL OR processing_started_at IS NOT NULL)
            """;
        update.Parameters.AddWithValue("$id", message.Id);
        update.Parameters.AddWithValue("$kind", message.Kind);
        update.Parameters.AddWithValue(
            "$stale_before",
            DateTimeOffset.UtcNow.Subtract(OutboxProcessingLease).ToString("O", CultureInfo.InvariantCulture));
        if (update.ExecuteNonQuery() == 1)
            return OrchestratorStateOutboxRequeueOutcome.Requeued;

        var state = ReadOutboxState(connection, message.Id);
        if (state is null)
        {
            TryInsertOutboxMessageIfMissing(connection, message);
            return OrchestratorStateOutboxRequeueOutcome.Inserted;
        }

        if (!state.Message.Kind.Equals(message.Kind, StringComparison.Ordinal))
            throw new InvalidOperationException($"Outbox message '{message.Id}' has a different kind.");

        // A clean pending row needs no rewrite. All other unchanged rows hold a live lease.
        return state.Status == OrchestratorStateOutboxStatus.Pending
            ? OrchestratorStateOutboxRequeueOutcome.Existing
            : OrchestratorStateOutboxRequeueOutcome.ProtectedByLiveLease;
    }

    internal static bool TryInsertOutboxMessageIfMissing(
        SqliteConnection conn,
        OrchestratorStateOutboxMessage message)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO state_outbox (id, kind, payload_json, created_at)
            VALUES ($id, $kind, $payload_json, $created_at)
            ON CONFLICT(id) DO NOTHING
            """;
        BindOutboxMessage(cmd, message);
        return cmd.ExecuteNonQuery() == 1;
    }

    public Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(CancellationToken cancellationToken = default) =>
        ListGoalMetadataAsync(includeTerminalCreatedAt: false, cancellationToken);

    // Status discovers goals by identity and lifecycle only. In particular this must not parse
    // snapshot timelines or materialize objectives/promotion details for every terminal goal.
    public async Task<IReadOnlyList<(string Id, string Status)>> ListStatusGoalIdsAsync(
        CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, status FROM goals";
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<(string, string)>();
        long characters = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetString(0);
            var status = reader.GetString(1);
            characters += id.Length + status.Length;
            result.Add((id, status));
        }
        _readObserver?.Invoke(new("metadata", null, characters));
        return result;
    }

    public async Task<(string GoalId, DateTimeOffset PromotedAt)?> ReadLatestStatusLandingAsync(
        CancellationToken cancellationToken = default)
    {
        long characters = 0;
        try
        {
            using var connection = OpenConnection();
            using var schema = connection.CreateCommand();
            schema.CommandText = "SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = 'goal_promotion_facts'";
            if (await schema.ExecuteScalarAsync(cancellationToken) is null) return null;
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT goal_id, promoted_at FROM goal_promotion_facts ORDER BY promoted_at DESC, rowid DESC";
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(1)) continue;
                var at = reader.GetString(1);
                characters += at.Length;
                if (!DateTimeOffset.TryParseExact(at, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) continue;
                var id = reader.GetString(0);
                characters += id.Length;
                return (id, parsed);
            }
            return null;
        }
        finally { _readObserver?.Invoke(new("latest-landing", null, characters)); }
    }

    public async Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(
        bool includeTerminalCreatedAt, CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        var results = new List<GoalSummary>();
        var hasPromotionFacts = HasPromotionFactsTable(conn);
        var promotionColumns = hasPromotionFacts
            ? "promotion.goal_id, promotion.candidate_revision, promotion.prior_main_revision, promotion.promoted_main_revision, promotion.promoted_at, promotion.provenance"
            : "NULL, NULL, NULL, NULL, NULL, NULL";
        var promotionJoin = hasPromotionFacts
            ? "LEFT JOIN goal_promotion_facts AS promotion ON promotion.goal_id = goals.id" : string.Empty;

        await using var cmd = conn.CreateCommand();
        var hasTimelineTable = await GoalTimelineStore.HasTableAsync(conn, cancellationToken);
        cmd.CommandText = $"""
            SELECT
                id,
                status,
                objective,
                updated_at,
                CASE WHEN {MetadataNonTerminalPredicate()} THEN {GoalTimelineStore.OrderedInstantSql(hasTimelineTable)} WHEN $include_terminal_created_at = 1
                    THEN COALESCE({GoalTimelineStore.FirstSql(hasTimelineTable)},
                        json_extract(goals.snapshot_json, '$.CreatedAt'))
                END AS created_at,
                {ActiveWithFailedTaskConditionSql()},
                {promotionColumns}
            FROM goals
            {promotionJoin}
            ORDER BY updated_at DESC
            """;
        cmd.Parameters.AddWithValue("$include_terminal_created_at", includeTerminalCreatedAt ? 1 : 0);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        long characters = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            if (_readObserver is not null)
                for (var column = 0; column < reader.FieldCount; column++)
                    if (reader.GetValue(column) is string text) characters += text.Length;
            results.Add(new GoalSummary(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                CreatedAt: reader.IsDBNull(4)
                    ? null
                    : DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                Condition: reader.IsDBNull(5) ? null : reader.GetString(5),
                PromotionFact: ReadPromotionFact(reader, 6)));
        }

        _readObserver?.Invoke(new("metadata", null, characters));
        return results;
    }

    private static string MetadataNonTerminalPredicate() =>
        $"status NOT IN ('{GoalStatus.Completed}', '{GoalStatus.Cancelled}', '{GoalStatus.Superseded}')";

    public async Task<IReadOnlyList<GoalSummary>> ListGoalBoardRowsAsync(
        GoalBoardRowQuery query, CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        return await SqliteGoalBoardRowReader.ReadAsync(conn, query, _statementObserver, cancellationToken);
    }

    public async Task<IReadOnlyList<GoalSummary>> ListGoalIdStatusesAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        var results = new List<GoalSummary>();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, status, objective, updated_at
            FROM goals
            ORDER BY updated_at DESC
            """;
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new GoalSummary(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3)));
        }

        return results;
    }

    public async Task<IReadOnlyList<GoalSummary>> ListConductLoopGoalMetadataAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        var results = new List<GoalSummary>();

        await using var cmd = conn.CreateCommand();
        // EXPLAIN QUERY PLAN retains one ordered goals scan with correlated JSON virtual tables;
        // keeping those tables beneath lazy CASE expressions avoids a second status-index scan and
        // preserves row order. SQLite evaluates CASE arms on demand, so terminal rows never invoke
        // json_each (covered by the malformed-JSON sentinel fact).
        var hasTimelineTable = await GoalTimelineStore.HasTableAsync(conn, cancellationToken);
        var nonTerminal = ConductLoopGoalStatus.SqlNonTerminalPredicate("status");
        var latestCommit = GoalTaskHistoryStore.LatestFieldSql(
            await GoalTaskHistoryStore.HasTableAsync(conn, cancellationToken), "dispatch", "ResultCommit");
        cmd.CommandText = $"""
            SELECT
                id,
                status,
                {GoalMetadataTitleSql()},
                updated_at,
                CASE WHEN {nonTerminal} THEN (
                    SELECT {latestCommit}
                    FROM json_each(goals.snapshot_json, '$.Tasks') AS task
                    WHERE COALESCE(({latestCommit}), '') <> ''
                    ORDER BY CAST(task.key AS INTEGER) DESC
                    LIMIT 1
                ) END AS result_commit,
                CASE WHEN {nonTerminal} THEN {GoalTimelineStore.OrderedInstantSql(hasTimelineTable)} END AS created_at,
                CASE WHEN {nonTerminal} THEN {GoalTimelineStore.OrderedInstantSql(hasTimelineTable, descending: true)} END AS terminated_at,
                {ActiveWithFailedTaskConditionSql()}
            FROM goals
            ORDER BY updated_at DESC
            """;
        _statementObserver?.Invoke(cmd.CommandText);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetString(0);
            var status = reader.GetString(1);
            var updatedAt = reader.GetString(3);
            results.Add(new GoalSummary(
                id,
                status,
                reader.GetString(2),
                updatedAt,
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
                reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
                Condition: reader.IsDBNull(7) ? null : reader.GetString(7),
                terminalMetadataLoader: ConductLoopGoalStatus.IsTerminal(status)
                    ? () => LoadTerminalMetadata(id, updatedAt)
                    : null));
        }

        return results;
    }

    public async Task<IReadOnlyList<GoalSummary>> ListLiveConductLoopGoalMetadataAsync(
        IReadOnlyCollection<string> requestedGoalIds, CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        var hasTimelineTable = await GoalTimelineStore.HasTableAsync(conn, cancellationToken);
        var nonTerminal = ConductLoopGoalStatus.SqlNonTerminalPredicate("status");
        var latestCommit = GoalTaskHistoryStore.LatestFieldSql(
            await GoalTaskHistoryStore.HasTableAsync(conn, cancellationToken), "dispatch", "ResultCommit");
        cmd.CommandText = $"""
            SELECT
                id,
                status,
                {GoalMetadataTitleSql()},
                updated_at,
                CASE WHEN {nonTerminal} THEN (
                    SELECT {latestCommit}
                    FROM json_each(goals.snapshot_json, '$.Tasks') AS task
                    WHERE COALESCE(({latestCommit}), '') <> ''
                    ORDER BY CAST(task.key AS INTEGER) DESC
                    LIMIT 1
                ) END AS result_commit,
                CASE WHEN {nonTerminal} THEN {GoalTimelineStore.OrderedInstantSql(hasTimelineTable)} END AS created_at,
                CASE WHEN {nonTerminal} THEN {GoalTimelineStore.OrderedInstantSql(hasTimelineTable, descending: true)} END AS terminated_at,
                {ActiveWithFailedTaskConditionSql()}
            FROM goals
            WHERE {nonTerminal} OR id IN (SELECT value FROM json_each($ids))
            ORDER BY updated_at DESC
            """;
        cmd.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(requestedGoalIds));
        _statementObserver?.Invoke(cmd.CommandText);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        var results = new List<GoalSummary>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetString(0);
            var status = reader.GetString(1);
            var updatedAt = reader.GetString(3);
            results.Add(new GoalSummary(
                id,
                status,
                reader.GetString(2),
                updatedAt,
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
                reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
                Condition: reader.IsDBNull(7) ? null : reader.GetString(7),
                terminalMetadataLoader: ConductLoopGoalStatus.IsTerminal(status)
                    ? () => LoadTerminalMetadata(id, updatedAt)
                    : null));
        }

        return results;
    }

    public async Task<IReadOnlyDictionary<string, string>> ListConductLoopGoalStatusesByIdAsync(
        IReadOnlyCollection<string> goalIds, CancellationToken cancellationToken = default)
    {
        var statuses = new Dictionary<string, string>(StringComparer.Ordinal);
        if (goalIds.Count == 0)
            return statuses;

        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, status
            FROM goals
            WHERE id IN (SELECT value FROM json_each($ids))
            """;
        cmd.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(goalIds));
        _statementObserver?.Invoke(cmd.CommandText);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            statuses.Add(reader.GetString(0), reader.GetString(1));

        return statuses;
    }

    public async Task<bool> EnsureTerminalOpenWorkIndexAsync(CancellationToken cancellationToken = default)
    {
        _statementObserver?.Invoke("BEGIN IMMEDIATE");
        var write = await BeginWriteAsync(ResolveOperationTag(nameof(EnsureTerminalOpenWorkIndexAsync)), cancellationToken);
        await using var conn = write.Connection;
        async Task ExecuteAsync(string sql, CancellationToken ct)
        {
            _statementObserver?.Invoke(sql);
            await RunNonQueryAsync(conn, sql, ct);
        }
        try
        {
            await using var probe = conn.CreateCommand();
            probe.CommandText = TerminalOpenWorkIndex.ExistsSql;
            _statementObserver?.Invoke(probe.CommandText);
            var exists = Convert.ToInt64(await probe.ExecuteScalarAsync(cancellationToken)) != 0;
            if (!exists)
            {
                await ExecuteAsync(TerminalOpenWorkIndex.SchemaSql, cancellationToken);
                await ExecuteAsync(TerminalOpenWorkIndex.BackfillSql, cancellationToken);
            }
            await ExecuteAsync("COMMIT", cancellationToken);
            write.Telemetry.Emit("commit");
            return !exists;
        }
        catch (Exception ex)
        {
            try { await ExecuteAsync("ROLLBACK", CancellationToken.None); } catch { }
            write.Telemetry.Emit("rollback", ex);
            throw;
        }
    }

    public async Task<IReadOnlyList<GoalId>> ListTerminalGoalIdsWithNonTerminalTasksAsync(
        CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = TerminalOpenWorkIndex.ExistsSql;
        _statementObserver?.Invoke(cmd.CommandText);
        var exists = Convert.ToInt64(await cmd.ExecuteScalarAsync(cancellationToken)) != 0;
        cmd.CommandText = exists ? TerminalOpenWorkIndex.ListSql : TerminalOpenWorkIndex.ScanSql;
        _statementObserver?.Invoke(cmd.CommandText);
        var ids = new List<GoalId>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            ids.Add(new GoalId(reader.GetString(0)));
        return ids;
    }

    private TerminalGoalMetadataValues LoadTerminalMetadata(string id, string updatedAt)
    {
        var key = new TerminalMetadataCacheKey(id, updatedAt);
        return _terminalMetadataCache.GetOrAdd(
            key,
            static (cacheKey, repository) => new Lazy<TerminalGoalMetadataValues>(
                () => repository.ReadTerminalMetadata(cacheKey),
                LazyThreadSafetyMode.ExecutionAndPublication),
            this).Value;
    }

    private TerminalGoalMetadataValues ReadTerminalMetadata(TerminalMetadataCacheKey key)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT snapshot_json
            FROM goals
            WHERE id = $id
              AND updated_at = $updated_at
              AND {ConductLoopGoalStatus.SqlTerminalPredicate("status")}
            """;
        cmd.Parameters.AddWithValue("$id", key.Id);
        cmd.Parameters.AddWithValue("$updated_at", key.UpdatedAt);
        using var reader = cmd.ExecuteReader();
        var snapshotJson = reader.Read() ? reader.GetString(0) : throw new InvalidOperationException(
            $"Terminal goal metadata changed before detail access for goal '{key.Id}'.");

        using var document = GoalSnapshotByteAccounting.Parse(snapshotJson);
        var root = document.RootElement;
        if (root.TryGetProperty("IsMetadataOnly", out var metadataOnly) && metadataOnly.ValueKind == JsonValueKind.True)
        {
            var stub = GoalSnapshotByteAccounting.Deserialize<GoalSnapshot>(snapshotJson, SerializerOptions)!;
            return new TerminalGoalMetadataValues(stub.ResultCommit, stub.CreatedAt, stub.TerminatedAt);
        }
        string? resultCommit = null;
        if (root.TryGetProperty("Tasks", out var tasks) && tasks.ValueKind == JsonValueKind.Array)
        {
            foreach (var task in tasks.EnumerateArray())
            {
                var dispatch = TaskHistoryStore.ReadLatestDispatchAsync(conn, key.Id, task).GetAwaiter().GetResult();
                if (dispatch.ValueKind == JsonValueKind.Object &&
                    dispatch.TryGetProperty("ResultCommit", out var commit) &&
                    !string.IsNullOrEmpty(commit.GetString()))
                {
                    resultCommit = commit.GetString();
                }
            }
        }

        DateTimeOffset? createdAt = null;
        DateTimeOffset? terminatedAt = null;
        var timeline = new GoalTimelineStore(SerializerOptions).ReadAsync(conn, key.Id, root).GetAwaiter().GetResult();
        if (timeline.Length > 0)
        {
            createdAt = ReadOccurredAt(timeline[0]);
            terminatedAt = ReadOccurredAt(timeline[^1]);
        }

        return new TerminalGoalMetadataValues(resultCommit, createdAt, terminatedAt);
    }

    private static DateTimeOffset? ReadOccurredAt(JsonElement timelineEvent) =>
        timelineEvent.TryGetProperty("OccurredAt", out var occurredAt) && occurredAt.ValueKind == JsonValueKind.String
            ? DateTimeOffset.Parse(occurredAt.GetString()!, CultureInfo.InvariantCulture)
            : null;

    private static string ActiveWithFailedTaskConditionSql() => $"""
        CASE
            WHEN status = '{GoalStatus.Active}' AND EXISTS (
                SELECT 1
                FROM json_each(goals.snapshot_json, '$.Tasks') AS failed_task
                WHERE json_extract(failed_task.value, '$.Status') = '{WorkTaskStatus.Failed}'
            ) THEN '{GoalLifecycle.ActiveWithFailedTaskCondition}'
            ELSE NULL
        END AS condition
        """;

    public async Task<IReadOnlyList<TerminalOwnerQuestionHold>> ListTerminalOwnerQuestionHoldsAsync(
        IReadOnlyCollection<GoalId> goalIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(goalIds);
        if (goalIds.Count == 0) return [];

        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        var parameterNames = goalIds.Select((_, index) => $"$goal_id{index}").ToArray();
        cmd.CommandText = $"""
            SELECT id, json_extract(snapshot_json, '$.CurrentHold.Identity'),
                json_extract(snapshot_json, '$.CurrentHold.State'),
                json_extract(snapshot_json, '$.CurrentHold.Blocker'),
                json_extract(snapshot_json, '$.CurrentHold.StartedAt')
            FROM goals
            WHERE id IN ({string.Join(", ", parameterNames)})
                AND status COLLATE NOCASE IN ('Completed', 'Cancelled', 'Superseded')
                AND json_valid(snapshot_json)
                AND json_extract(snapshot_json, '$.CurrentHold.State') COLLATE NOCASE = 'steward-owner-question'
            """;
        var index = 0;
        foreach (var goalId in goalIds)
            cmd.Parameters.AddWithValue(parameterNames[index++], goalId.Value);
        var results = new List<TerminalOwnerQuestionHold>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            results.Add(new TerminalOwnerQuestionHold(reader.GetString(0), reader.GetString(1),
                reader.GetString(2), reader.GetString(3), DateTimeOffset.Parse(reader.GetString(4),
                    System.Globalization.CultureInfo.InvariantCulture)));
        return results;
    }

    public async Task<IReadOnlyList<GoalId>> ListGateTestObligationGoalIdsAsync(
        IReadOnlyCollection<GoalId>? goalIds = null,
        CancellationToken cancellationToken = default)
    {
        if (goalIds is { Count: 0 }) return [];

        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        var parameterNames = goalIds?.Select((_, index) => $"$goal_id{index}").ToArray();
        var filter = parameterNames is null ? "" : $"AND id IN ({string.Join(", ", parameterNames)})";
        cmd.CommandText = $"""
            SELECT id FROM goals
            WHERE CASE WHEN json_valid(snapshot_json)
                THEN json_array_length(snapshot_json, '$.GateTestObligations') ELSE 0 END > 0
                {filter}
            ORDER BY rowid
            """;
        if (goalIds is not null)
        {
            var index = 0;
            foreach (var goalId in goalIds)
                cmd.Parameters.AddWithValue(parameterNames![index++], goalId.Value);
        }
        var results = new List<GoalId>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            results.Add(new GoalId(reader.GetString(0)));
        return results;
    }

    public async Task<IReadOnlyList<HumanInputRequestSnapshot>> ListOpenHumanInputRequestsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT snapshot_json FROM human_input_requests";
        var results = new List<HumanInputRequestSnapshot>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var snapshot = JsonSerializer.Deserialize<HumanInputRequestSnapshot>(reader.GetString(0), SerializerOptions);
            if (snapshot is { IsCompleted: false }) results.Add(snapshot);
        }
        return results;
    }

    public async Task<IReadOnlyList<GoalId>> ListGoalIdsWithCompletedHumanInputAsync(
        IReadOnlyCollection<GoalId> goalIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(goalIds);
        if (goalIds.Count == 0)
        {
            return [];
        }

        await using var conn = OpenConnection();
        var results = new HashSet<string>(StringComparer.Ordinal);
        var parameterNames = goalIds.Select((_, index) => $"$goal_id{index}").ToArray();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT goal_id, snapshot_json
            FROM human_input_requests
            WHERE goal_id IN ({string.Join(", ", parameterNames)})
            """;
        var index = 0;
        foreach (var goalId in goalIds)
        {
            cmd.Parameters.AddWithValue(parameterNames[index], goalId.Value);
            index++;
        }

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var snapshot = JsonSerializer.Deserialize<HumanInputRequestSnapshot>(reader.GetString(1), SerializerOptions);
            if (snapshot?.IsCompleted == true &&
                !IsSyntheticParkedHumanWaitCompletion(snapshot))
            {
                results.Add(reader.GetString(0));
            }
        }

        return results.Select(id => new GoalId(id)).ToArray();
    }

    private static bool IsSyntheticParkedHumanWaitCompletion(HumanInputRequestSnapshot snapshot) =>
        !snapshot.WasDismissed &&
        GoalParkResolution.IsParkResolution(snapshot.Answer);

    public async Task<IReadOnlyList<QuarantinedGoalSummary>> ListQuarantinedGoalRowsAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        var results = new List<QuarantinedGoalSummary>();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, snapshot_json FROM goals ORDER BY updated_at DESC";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            TryReadGoalSnapshot(
                reader.GetString(0),
                reader.GetString(1),
                results,
                logQuarantine: false,
                out _);
        }

        return results;
    }

    public Task<IReadOnlyList<ModelFitHistoryRow>> ListModelFitHistoryAsync(CancellationToken cancellationToken = default) =>
        ReadModelFitHistoryAsync(providerHoldRule: null, cancellationToken);

    public Task<IReadOnlyList<ModelFitHistoryRow>> ListProviderHoldHistoryAsync(string exhaustionRule,
        CancellationToken cancellationToken = default) => ReadModelFitHistoryAsync([exhaustionRule], cancellationToken);

    public Task<IReadOnlyList<ModelFitHistoryRow>> ListProviderHoldHistoryAsync(
        CancellationToken cancellationToken = default) => ReadModelFitHistoryAsync(ProviderHoldHistory.HoldOutcomeRules, cancellationToken);

    public async Task<ProviderHoldSeedHistory> ReadProviderHoldSeedHistoryAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = OpenConnection();
        var candidates = await ProviderHoldSeedDiscovery.ReadAsync(
            connection, SerializerOptions, cancellationToken, _readObserver);
        var (evidence, goals) = await ReadAttemptsAsync(candidates.FailureGoals);
        var failureHistory = evidence.Select(ProviderHoldSeedPruning.Adapt).Concat(goals).ToArray();
        var dispatchMatchingVisits = 0;
        void RecordDispatchVisits(int visits) => dispatchMatchingVisits += visits;
        var attempts = failureHistory.SelectMany(goal => ProviderHoldHistory.SeedAttempts(goal, RecordDispatchVisits))
            .Distinct().ToArray();
        var rows = await ReadModelFitHistoryAsync(connection, ProviderHoldHistory.HoldOutcomeRules,
            cancellationToken, attempts);
        var selection = ProviderHoldHistory.SelectSeedEvidence(rows, attempts, new HashSet<string>(StringComparer.Ordinal));
        var recoveryIds = await candidates.FindRecoveryCandidatesAsync(connection, selection, cancellationToken);
        recoveryIds.ExceptWith(candidates.FailureGoals);
        var (recoveryEvidence, recoveryGoals) = await ReadAttemptsAsync(recoveryIds);
        // An orphan refusal may have selected a goal that supplies an unindexed recovery.
        var unindexedFailureHistory = failureHistory.Where(goal =>
            !selection.Sources.Contains(goal.Id.Value) && !selection.Dependencies.Contains(goal.Id.Value));
        var recoveries = unindexedFailureHistory.Concat(recoveryEvidence.Select(ProviderHoldSeedPruning.Adapt))
            .Concat(recoveryGoals)
            .SelectMany(goal => ProviderHoldHistory.SeedRecoveryAttempts(goal, RecordDispatchVisits)).Where(attempt =>
                selection.LowerBounds.TryGetValue(attempt.ProviderName, out var first) && attempt.Timestamp >= first)
            .Distinct().ToArray();
        var witnesses = recoveries.Select(attempt => attempt.GoalId).ToHashSet(StringComparer.Ordinal);
        var replay = evidence.Concat(recoveryEvidence.Where(row => witnesses.Contains(row.GoalId)))
            .ToDictionary(row => row.GoalId, StringComparer.Ordinal);
        foreach (var id in selection.Sources.Concat(selection.Dependencies).Concat(witnesses)
            .Concat(candidates.MarkerGoals.Select(id => id.Value)))
            if (candidates.ArchiveEvidence.TryGetValue(new GoalId(id), out var archived))
                replay.TryAdd(id, archived);
        return new(rows, attempts, candidates.MarkerGoals, replay.Values.ToArray())
        {
            Recoveries = recoveries,
            DispatchMatchingVisits = dispatchMatchingVisits
        };

        async Task<(IReadOnlyList<ProviderUsageGoalEvidence> Evidence, Goal[] Goals)> ReadAttemptsAsync(
            IReadOnlyCollection<GoalId> ids)
        {
            if (ids.Count == 0) return ([], []);
            IReadOnlyList<ProviderUsageGoalEvidence> projected = [];
            try
            {
                projected = ids.Where(candidates.ArchiveEvidence.ContainsKey)
                    .Select(id => candidates.ArchiveEvidence[id]).ToArray();
                var remaining = ids.Where(id => !candidates.ArchiveEvidence.ContainsKey(id)).ToArray();
                if (remaining.Length != 0)
                    projected = projected.Concat(await ReadProviderHoldSeedEvidenceAsync(remaining, cancellationToken) ?? []).ToArray();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Fall back to the same canonical load used when sparse seed evidence is unavailable.
            }
            var missing = ids.Except(projected.Where(row => row.Tasks.Count != 0)
                .Select(row => new GoalId(row.GoalId))).ToHashSet();
            Goal[] canonical = [];
            if (missing.Count != 0)
            {
                var loaded = await LoadGoalsAsync(missing, cancellationToken);
                canonical = loaded.Goals.Where(goal => missing.Contains(goal.Id) && !goal.IsMetadataOnly).ToArray();
                if (canonical.Length != missing.Count)
                    throw new InvalidDataException("Required provider hold attempt history is unavailable.");
            }
            return (projected.Where(row => !missing.Contains(new GoalId(row.GoalId))).ToArray(), canonical);
        }
    }

    private async Task<IReadOnlyList<ModelFitHistoryRow>> ReadModelFitHistoryAsync(IReadOnlyList<string>? providerHoldRule,
        CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        return await ReadModelFitHistoryAsync(connection, providerHoldRule, cancellationToken);
    }

    private async Task<IReadOnlyList<ModelFitHistoryRow>> ReadModelFitHistoryAsync(SqliteConnection connection,
        IReadOnlyList<string>? providerHoldRule, CancellationToken cancellationToken,
        IReadOnlyCollection<ProviderHoldSeedAttempt>? seedAttempts = null)
    {
        var results = new List<ModelFitHistoryRow>();

        await using var cmd = connection.CreateCommand();
        // SQLite NOCASE only folds ASCII. For a non-ASCII provider retain the full read,
        // leaving canonical OrdinalIgnoreCase matching to SeedCandidates. For ASCII names
        // the SQL is a conservative superset (julianday rounds sub-millisecond instants).
        var ruleParameters = providerHoldRule?.Select((rule, index) =>
        {
            var name = $"$holdRule{index}";
            cmd.Parameters.AddWithValue(name, rule);
            return name;
        }).ToArray();
        var rules = ruleParameters is null ? "" : string.Join(", ", ruleParameters);
        var attempts = seedAttempts is { Count: > 0 } ? """
            UNION ALL
            SELECT json_extract(value, '$.ProviderName'), json_extract(value, '$.Timestamp')
            FROM json_each($seedAttempts)
            """ : "";
        if (attempts.Length != 0)
            cmd.Parameters.AddWithValue("$seedAttempts", JsonSerializer.Serialize(seedAttempts!
                .GroupBy(attempt => attempt.ProviderName, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.MinBy(attempt => attempt.Timestamp)!), SerializerOptions));
        var prefix = providerHoldRule is not null ? $"""
            WITH hold_events AS (
                SELECT provider_name, timestamp FROM model_fit_history WHERE outcome_rule IN ({rules})
                {attempts}
            ), exhausted AS MATERIALIZED (
                SELECT provider_name, CASE WHEN count(*) != count(julianday(timestamp)) THEN NULL
                    ELSE min(julianday(timestamp)) END AS first_at
                FROM hold_events
                GROUP BY provider_name COLLATE NOCASE)
            """ : string.Empty;
        var recovery = seedAttempts is not null ? "(outcome = 'Completed' OR outcome_class = 'success') AND" : "";
        var restriction = providerHoldRule is not null ? $"""
            WHERE outcome_rule IN ({rules}) OR ({recovery} (EXISTS (
                SELECT 1 FROM model_fit_history e
                WHERE e.provider_name GLOB '*[^ -~]*') OR EXISTS (
                SELECT 1 FROM exhausted e WHERE e.provider_name GLOB '*[^ -~]*') OR EXISTS (
                SELECT 1 FROM exhausted e
                WHERE e.provider_name = model_fit_history.provider_name COLLATE NOCASE
                    AND (e.first_at IS NULL OR julianday(model_fit_history.timestamp) IS NULL
                        OR julianday(model_fit_history.timestamp) >= e.first_at))))
            """ : string.Empty;
        cmd.CommandText = $"""
            {prefix}
            SELECT goal_id, task_id, role, provider_name, model_name, complexity, task_shape, outcome, self_rating, timestamp, outcome_rule, outcome_class, dispatch_lane
            FROM model_fit_history
            {restriction}
            ORDER BY timestamp DESC
            """;
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        long characters = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            if (_readObserver is not null)
                for (var column = 0; column < reader.FieldCount; column++)
                    if (reader.GetValue(column) is string text) characters += text.Length;
            results.Add(ReadModelFitHistoryRow(reader));
        }

        _readObserver?.Invoke(new("model-fit-history", null, characters));
        return results;
    }

    public async Task<IReadOnlyList<ModelOutcomeRecord>> BuildModelOutcomeScorecardAsync(
        int windowSize = ModelOutcomeScorecard.DefaultWindowSize,
        CancellationToken cancellationToken = default)
    {
        return ModelOutcomeScorecard.Build(await ListModelFitHistoryAsync(cancellationToken), windowSize);
    }

    public async Task<ModelFitBestFit?> QueryBestFitForRoleAsync(AgentRole role, CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        var rows = new List<ModelFitHistoryRow>();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT goal_id, task_id, role, provider_name, model_name, complexity, task_shape, outcome, self_rating, timestamp, outcome_rule, outcome_class, dispatch_lane
            FROM model_fit_history
            WHERE role = $role
            ORDER BY timestamp DESC
            """;
        cmd.Parameters.AddWithValue("$role", role.ToString());
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadModelFitHistoryRow(reader));
        }

        return ModelFitHistory.QueryBestFitForRole(rows, role);
    }

    private static async Task<AgentOrchestratorKernel> LoadFromConnectionAsync(
        SqliteConnection conn,
        IReadOnlyCollection<GoalId>? goalIds,
        CancellationToken cancellationToken,
        Action<GoalSnapshotReadObservation>? readObserver = null, bool includePractices = true,
        IReadOnlyList<GoalSnapshot>? preloadedGoals = null, Action<string, TimeSpan>? timing = null)
    {
        if (preloadedGoals is null)
        {
            using var snapshotTiming = new ReloadTimingScope(timing, ChangedSnapshotRead);
            preloadedGoals = (await ReadGoalSnapshotsAsync(conn, goalIds, cancellationToken, readObserver))
                .Values.ToArray();
        }

        List<HumanInputRequestSnapshot> humanInputSnapshots;
        using (new ReloadTimingScope(timing, HumanInputRequestLoad))
        {
            humanInputSnapshots = new List<HumanInputRequestSnapshot>();
            if (goalIds is null || goalIds.Count > 0)
            {
                await using var cmd = conn.CreateCommand();
                if (goalIds is null)
                {
                    cmd.CommandText = "SELECT snapshot_json FROM human_input_requests";
                }
                else
                {
                    var parameterNames = goalIds.Select((_, index) => $"$goal_id{index}").ToArray();
                    cmd.CommandText = $"SELECT snapshot_json FROM human_input_requests WHERE goal_id IN ({string.Join(", ", parameterNames)})";
                    var index = 0;
                    foreach (var goalId in goalIds)
                    {
                        cmd.Parameters.AddWithValue(parameterNames[index], goalId.Value);
                        index++;
                    }
                }

                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                long characters = 0;
                while (await reader.ReadAsync(cancellationToken))
                {
                    var json = reader.GetString(0);
                    characters += json.Length;
                    var snap = JsonSerializer.Deserialize<HumanInputRequestSnapshot>(json, SerializerOptions);
                    if (snap != null) humanInputSnapshots.Add(snap);
                }
                readObserver?.Invoke(new("human-input", null, characters));
            }
        }

        AgentOrchestratorKernel kernel;
        using (new ReloadTimingScope(timing, KernelBuild))
            kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(preloadedGoals, humanInputSnapshots));
        if (includePractices)
        {
            using var practiceTiming = new ReloadTimingScope(timing, EngineeringPracticeLoad);
            kernel.SetEngineeringPractices(PracticeRegistryStore.ListActive(conn));
        }
        return kernel;
    }

    private static SqliteCommand CreateGoalReadCommand(
        SqliteConnection connection, string columns, IReadOnlyCollection<GoalId>? goalIds)
    {
        var command = connection.CreateCommand();
        command.CommandText = $"SELECT {columns} FROM goals";
        if (goalIds is null) return command;

        var parameters = goalIds.Select((_, index) => $"$id{index}").ToArray();
        command.CommandText += $" WHERE id IN ({string.Join(", ", parameters)})";
        var parameterIndex = 0;
        foreach (var goalId in goalIds)
            command.Parameters.AddWithValue(parameters[parameterIndex++], goalId.Value);
        return command;
    }

    private static async Task<Dictionary<string, GoalSnapshot>> ReadGoalSnapshotsAsync(
        SqliteConnection connection, IReadOnlyCollection<GoalId>? goalIds, CancellationToken token,
        Action<GoalSnapshotReadObservation>? readObserver,
        Func<SqliteConnection, GoalSnapshot, GoalTaskHistoryStore.DocumentMarkers, CancellationToken, Task<GoalSnapshot>>?
            projectTickGoal = null)
    {
        var snapshots = new Dictionary<string, GoalSnapshot>(StringComparer.Ordinal);
        if (goalIds is { Count: 0 }) return snapshots;

        await using var command = CreateGoalReadCommand(connection, "id, snapshot_json", goalIds);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var id = reader.GetString(0);
            var json = reader.GetString(1);
            readObserver?.Invoke(new("hot-attempt", id, json.Length));
            var markers = new GoalTaskHistoryStore.DocumentMarkers();
            if (!TryReadGoalSnapshot(id, json, null, logQuarantine: true, out var snapshot, markers))
                continue;

            readObserver?.Invoke(new("hot-deserialized", id, json.Length));
            var hydrated = projectTickGoal is not null
                ? await projectTickGoal(connection, snapshot, markers, token)
                : await HydrateReadGoalAsync(connection, snapshot, markers, token, readObserver,
                    hydrateArchive: goalIds is not null);
            snapshots.Add(id, hydrated!);
        }

        return snapshots;
    }

    private static async Task<GoalSnapshot?> HydrateReadGoalAsync(SqliteConnection connection, GoalSnapshot? snapshot,
        GoalTaskHistoryStore.DocumentMarkers markers, CancellationToken token,
        Action<GoalSnapshotReadObservation>? readObserver = null, bool hydrateArchive = true)
    {
        var wasStub = snapshot is { IsMetadataOnly: true };
        if (wasStub && hydrateArchive)
            snapshot = await SnapshotArchive.HydrateAsync(connection, snapshot, token, readObserver);
        return await TaskHistoryStore.HydrateDecodedAsync(connection, snapshot, markers, wasStub, token, readObserver);
    }

    private static bool TryReadGoalSnapshot(
        string goalId,
        string json,
        List<QuarantinedGoalSummary>? quarantined,
        bool logQuarantine,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out GoalSnapshot? snapshot,
        GoalTaskHistoryStore.DocumentMarkers? markers = null)
    {
        try
        {
            snapshot = markers is null
                ? GoalSnapshotByteAccounting.Deserialize<GoalSnapshot>(json, SerializerOptions)
                : TaskHistoryStore.DecodeDocument(json, markers);
            if (snapshot is not null)
                return true;

            var error = "JsonException: deserialized goal snapshot was null";
            quarantined?.Add(new QuarantinedGoalSummary(goalId, error));
            if (logQuarantine)
                Console.Error.WriteLine($"QUARANTINED goal {ShortGoalId(goalId)}: {error}");
            return false;
        }
        catch (Exception ex) when (IsSnapshotDeserializeException(ex))
        {
            var error = $"{ex.GetType().Name}: {ex.Message}";
            quarantined?.Add(new QuarantinedGoalSummary(goalId, error));
            if (logQuarantine)
                Console.Error.WriteLine($"QUARANTINED goal {ShortGoalId(goalId)}: {error}");
            snapshot = null;
            return false;
        }
    }

    private static bool IsSnapshotDeserializeException(Exception exception) =>
        exception is JsonException or NotSupportedException or ArgumentException;

    private static IReadOnlyList<GateTestObligation> MergePlannerGateObligations(
        IReadOnlyList<GateTestObligation>? stored, IReadOnlyList<GateTestObligation>? current)
    {
        var merged = (stored ?? []).ToList();
        foreach (var item in current ?? [])
        {
            var existing = merged.FirstOrDefault(candidate => candidate.HasSameIdentity(item));
            if (existing is null) merged.Add(item);
            else if (item.PlannerIdentity is not null && !item.PlannerEvidence!.SemanticallyEquals(existing.PlannerEvidence))
                throw new InvalidOperationException("Conflicting Planner gate evidence payload in snapshot merge.");
        }
        return merged;
    }
    private static bool TryMergeGoalSnapshots(
        GoalSnapshot baseline,
        GoalSnapshot stored,
        GoalSnapshot current,
        bool preserveTimelineOrder,
        out GoalSnapshot merged,
        out string reason)
    {
        if (!string.Equals(baseline.Id, stored.Id, StringComparison.Ordinal) ||
            !string.Equals(baseline.Id, current.Id, StringComparison.Ordinal))
        {
            merged = stored;
            reason = "goal id changed during tick";
            return false;
        }

        if (!TryMergeTaskSnapshots(baseline.Tasks, stored.Tasks, current.Tasks, out var mergedTasks, out reason))
        {
            merged = stored;
            return false;
        }

        // Explicit commands are serialized by the goal-row CAS. Only the stored row may
        // supply that authority; lifecycle progress is still tick-owned and merges separately.
        // A committed explicit stop is terminal even if an older tick still reconciles
        // progress under the preceding park. Keep that evidence without reviving the hold.
        var stoppedAfterPark = stored.ExplicitDisposition?.Action == GoalExplicitDispositionAction.Park &&
            stored.Status is GoalStatus.Cancelled or GoalStatus.Superseded;
        var lifecycle = stoppedAfterPark ? stored.Status : PickTickOwned(
            baseline.ParkedLifecycleStatus ?? baseline.Status,
            stored.ParkedLifecycleStatus ?? stored.Status,
            current.ParkedLifecycleStatus ?? current.Status);
        var hasParkDisposition = stored.ExplicitDisposition?.Action == GoalExplicitDispositionAction.Park;
        var explicitlyParked = hasParkDisposition &&
            lifecycle is not (GoalStatus.Cancelled or GoalStatus.Superseded);

        var mergedSourceBacklogLink = PickStoreOwned(
            (baseline.SourceBacklogItemId, baseline.SourceBacklogCoverage),
            (stored.SourceBacklogItemId, stored.SourceBacklogCoverage),
            (current.SourceBacklogItemId, current.SourceBacklogCoverage));

        var obligationMerge = GoalSnapshotObligationMerge.Merge(
            baseline.CriterionEvidenceObligations, stored.CriterionEvidenceObligations, current.CriterionEvidenceObligations);
        // The hold is derived from this exact authority/accepted round. Do not
        // publish a stale discrepancy (or clearance) over a concurrent repair.
        var operatorConsistencyChanged = baseline.CurrentHold != current.CurrentHold &&
            (baseline.CurrentHold?.State == PlannerOperatorEvidenceConsistencyHold.State ||
             current.CurrentHold?.State == PlannerOperatorEvidenceConsistencyHold.State);
        if (operatorConsistencyChanged &&
            (ConductLoopGoalStatus.IsTerminal((stored.ParkedLifecycleStatus ?? stored.Status).ToString()) ||
             !string.Equals(JsonSerializer.Serialize(baseline.BriefVersions), JsonSerializer.Serialize(stored.BriefVersions), StringComparison.Ordinal) ||
             !string.Equals(JsonSerializer.Serialize(baseline.RefinedSpecVersions), JsonSerializer.Serialize(stored.RefinedSpecVersions), StringComparison.Ordinal) ||
             !string.Equals(JsonSerializer.Serialize(baseline.CriterionEvidenceObligations), JsonSerializer.Serialize(stored.CriterionEvidenceObligations), StringComparison.Ordinal) ||
             baseline.Tasks.Where(task => task.RequiredRole == AgentRole.Planner).Any(task =>
                 !string.Equals(JsonSerializer.Serialize(task), JsonSerializer.Serialize(stored.Tasks.SingleOrDefault(saved => saved.Id == task.Id)), StringComparison.Ordinal))))
        {
            merged = stored;
            reason = "Planner operator evidence consistency has changed stored authority or lifecycle; reload and revalidate the complete goal.";
            return false;
        }
        var plannerProjectionChanged = (current.CriterionEvidenceObligations ?? []).Any(item =>
            item.PlannerSources is { Count: > 0 } && !string.Equals(
                JsonSerializer.Serialize(item.PlannerSources),
                JsonSerializer.Serialize(baseline.CriterionEvidenceObligations?.SingleOrDefault(before => before.Id == item.Id)?.PlannerSources),
                StringComparison.Ordinal)) ||
            // Replaying existing ownership can repair only Reviewer attestation, with
            // no provenance delta. That task transition belongs to the same atomic
            // projection and must respect terminal, brief and Planner-round authority.
            ((current.CriterionEvidenceObligations ?? []).Any(item => item.PlannerSources is { Count: > 0 }) &&
             current.Tasks.Any(task => task.RequiredRole == AgentRole.Reviewer && task.Status == WorkTaskStatus.Completed &&
                 baseline.Tasks.Any(before => before.Id == task.Id && before.Status == WorkTaskStatus.Failed)));
        if (plannerProjectionChanged && ConductLoopGoalStatus.IsTerminal((stored.ParkedLifecycleStatus ?? stored.Status).ToString()))
        {
            merged = stored;
            reason = "Planner ownership projection cannot change a stored terminal goal; rejected the complete reconciliation delta.";
            return false;
        }
        if (plannerProjectionChanged &&
            (!string.Equals(JsonSerializer.Serialize(baseline.BriefVersions), JsonSerializer.Serialize(stored.BriefVersions), StringComparison.Ordinal) ||
             !string.Equals(JsonSerializer.Serialize(baseline.RefinedSpecVersions), JsonSerializer.Serialize(stored.RefinedSpecVersions), StringComparison.Ordinal) ||
             baseline.Tasks.Where(task => task.RequiredRole == AgentRole.Planner).Any(task =>
                 !string.Equals(JsonSerializer.Serialize(task), JsonSerializer.Serialize(stored.Tasks.SingleOrDefault(saved => saved.Id == task.Id)), StringComparison.Ordinal))))
        {
            merged = stored;
            reason = "Planner ownership projection has changed stored brief, criteria or Planner round; reload and revalidate the complete goal.";
            return false;
        }
        if (obligationMerge.ConflictIds.Any(id => (current.CriterionEvidenceObligations ?? [])
                .Concat(stored.CriterionEvidenceObligations ?? [])
                .Any(item => item.Id == id && item.PlannerSources is { Count: > 0 })))
        {
            merged = stored;
            reason = "Planner ownership projection conflicts with stored criterion authority; reload and revalidate the complete goal: " +
                string.Join(",", obligationMerge.ConflictIds);
            return false;
        }
        var refinedSpecVersions = PickStoreOwnedList(baseline.RefinedSpecVersions, stored.RefinedSpecVersions,
            current.RefinedSpecVersions);
        merged = stored with
        {
            Objective = PickStoreOwned(baseline.Objective, stored.Objective, current.Objective),
            BriefVersions = PickStoreOwnedList(baseline.BriefVersions, stored.BriefVersions, current.BriefVersions),
            Status = explicitlyParked ? GoalStatus.Parked : lifecycle,
            ExplicitDisposition = stored.ExplicitDisposition,
            ParkedLifecycleStatus = hasParkDisposition ? lifecycle : null,
            Tasks = mergedTasks,
            GateTestObligations = MergePlannerGateObligations(stored.GateTestObligations, current.GateTestObligations),
            CriterionEvidenceObligations = obligationMerge.Obligations,
            Timeline = MergeTimeline(baseline.Timeline, stored.Timeline, current.Timeline, preserveTimelineOrder),
            DependsOn = PickStoreOwnedList(baseline.DependsOn, stored.DependsOn, current.DependsOn),
            // Slice-batch ownership is immutable aggregate intake metadata. Operator/store changes win
            // over a stale conductor tick exactly like the other store-owned snapshot fields.
            SliceBatchParentId = PickStoreOwned(
                baseline.SliceBatchParentId,
                stored.SliceBatchParentId,
                current.SliceBatchParentId),
            SourceBacklogItemId = mergedSourceBacklogLink.SourceBacklogItemId,
            SourceBacklogCoverage = mergedSourceBacklogLink.SourceBacklogCoverage,
            RefinedSpec = RefinedSpecSnapshotAccess.UsesLegacyTopLevel(refinedSpecVersions?.Count ?? 0)
                ? PickStoreOwned(RefinedSpecSnapshotAccess.GetCurrent(baseline),
                    RefinedSpecSnapshotAccess.GetCurrent(stored), RefinedSpecSnapshotAccess.GetCurrent(current))
                : null,
            ClarificationRoundCount = PickTickOwned(
                baseline.ClarificationRoundCount,
                stored.ClarificationRoundCount,
                current.ClarificationRoundCount),
            RefinedSpecVersions = refinedSpecVersions,
            LatestAcceptanceFailure = PickStoreOwned(baseline.LatestAcceptanceFailure, stored.LatestAcceptanceFailure, current.LatestAcceptanceFailure),
            AcceptanceFailureDeferredForRetry = PickStoreOwned(
                baseline.AcceptanceFailureDeferredForRetry,
                stored.AcceptanceFailureDeferredForRetry,
                current.AcceptanceFailureDeferredForRetry),
            AutomaticAcceptanceRetryCount = PickStoreOwned(
                baseline.AutomaticAcceptanceRetryCount,
                stored.AutomaticAcceptanceRetryCount,
                current.AutomaticAcceptanceRetryCount),
            OperatorAcceptanceRegateCount = PickStoreOwned(
                baseline.OperatorAcceptanceRegateCount,
                stored.OperatorAcceptanceRegateCount,
                current.OperatorAcceptanceRegateCount),
            ConsecutiveAcceptanceIdentityStaleCount = PickTickOwned(
                baseline.ConsecutiveAcceptanceIdentityStaleCount,
                stored.ConsecutiveAcceptanceIdentityStaleCount,
                current.ConsecutiveAcceptanceIdentityStaleCount),
            LastAcceptanceIdentityStaleAttemptId = PickTickOwned(
                baseline.LastAcceptanceIdentityStaleAttemptId,
                stored.LastAcceptanceIdentityStaleAttemptId,
                current.LastAcceptanceIdentityStaleAttemptId),
            CurrentHold = PickTickOwned(
                baseline.CurrentHold,
                stored.CurrentHold,
                current.CurrentHold),
            Priority = PickTickOwned(baseline.Priority, stored.Priority, current.Priority),
            LandingBlocker = PickTickOwned(baseline.LandingBlocker, stored.LandingBlocker, current.LandingBlocker),
            Isolation = PickTickOwned(baseline.Isolation, stored.Isolation, current.Isolation),
            PriorityScheduling = PickTickOwned(baseline.PriorityScheduling, stored.PriorityScheduling, current.PriorityScheduling),
            EffectiveAcceptanceCriteriaCorrections = PickStoreOwnedList(
                baseline.EffectiveAcceptanceCriteriaCorrections,
                stored.EffectiveAcceptanceCriteriaCorrections,
                current.EffectiveAcceptanceCriteriaCorrections)
        };
        reason = "stored version advanced during tick; reapplied tick snapshot delta onto fresh goal row";
        if (obligationMerge.ConflictIds.Count > 0)
            reason += "; criterion evidence obligation conflict kept stored: " + string.Join(",", obligationMerge.ConflictIds);
        var retainedFields = GetRetainedGoalFields(stored, current, merged);
        if (retainedFields.Count > 0)
            reason += "; stored value kept over differing tick value: " + string.Join(",", retainedFields);
        return true;
    }

    private static IReadOnlyList<string> GetRetainedGoalFields(
        GoalSnapshot stored, GoalSnapshot current, GoalSnapshot merged)
    {
        try
        {
            var storedFields = SerializeMergeFields(stored);
            var tickFields = SerializeMergeFields(current);
            var mergedFields = SerializeMergeFields(merged);
            return storedFields.EnumerateObject().Select(field => field.Name)
                .Union(tickFields.EnumerateObject().Select(field => field.Name), StringComparer.Ordinal)
                .Where(name =>
                {
                    var storedValue = FieldValue(storedFields, name);
                    return storedValue == FieldValue(mergedFields, name) &&
                        storedValue != FieldValue(tickFields, name);
                })
                .Order(StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return [];
        }

        // These collections merge by element and do not have a field-level winner.
        static JsonElement SerializeMergeFields(GoalSnapshot goal) =>
            GoalSnapshotByteAccounting.SerializeToElement(goal with
            {
                Tasks = [],
                Timeline = [],
                GateTestObligations = null,
                CriterionEvidenceObligations = null
            }, SerializerOptions);

        static string FieldValue(JsonElement fields, string name) =>
            fields.TryGetProperty(name, out var value) ? value.GetRawText() : "null";
    }

    private static GoalSnapshot NormalizeStoredVerificationStatus(GoalSnapshot snapshot, bool preserveTimelineOrder,
        out string? reason)
    {
        reason = null;
        if (snapshot.Status is GoalStatus.Verified or GoalStatus.Parked or GoalStatus.WaitingForHuman)
        {
            return snapshot;
        }

        if (snapshot.Status is GoalStatus.Failed or GoalStatus.AcceptanceFailed or GoalStatus.Cancelled or GoalStatus.Superseded)
        {
            return snapshot;
        }

        var kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([snapshot], []));
        var goalId = new GoalId(snapshot.Id);
        if (!kernel.ReconcileGoalVerificationStatus(
            goalId,
            "Tick merge reconciled stored all-task verification gates to Verified."))
        {
            return snapshot;
        }

        reason = "reconciled stored all-task verification gates to Verified";
        var reconciled = kernel.ExportSnapshot().Goals.Single();
        return preserveTimelineOrder
            ? snapshot with { Status = reconciled.Status, Timeline = [.. snapshot.Timeline, reconciled.Timeline[^1]] }
            : reconciled;
    }

    private static bool TryMergeTaskSnapshots(
        IReadOnlyList<TaskSnapshot> baseline,
        IReadOnlyList<TaskSnapshot> stored,
        IReadOnlyList<TaskSnapshot> current,
        out IReadOnlyList<TaskSnapshot> merged,
        out string reason)
    {
        var baselineById = baseline.ToDictionary(task => task.Id, StringComparer.Ordinal);
        var storedById = stored.ToDictionary(task => task.Id, StringComparer.Ordinal);
        var currentById = current.ToDictionary(task => task.Id, StringComparer.Ordinal);
        var taskIds = stored.Select(task => task.Id)
            .Concat(current.Select(task => task.Id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var results = new List<TaskSnapshot>(taskIds.Length);

        foreach (var taskId in taskIds)
        {
            var hasStored = storedById.TryGetValue(taskId, out var storedTask);
            var hasCurrent = currentById.TryGetValue(taskId, out var currentTask);
            var hasBaseline = baselineById.TryGetValue(taskId, out var baselineTask);

            if (!hasCurrent)
            {
                if (hasStored && baselineById.ContainsKey(taskId))
                {
                    results.Add(storedTask!);
                    continue;
                }

                reason = $"tick snapshot no longer contains task {ShortGoalId(taskId)}";
                merged = stored;
                return false;
            }

            if (!hasBaseline)
            {
                results.Add(hasStored ? storedTask! : currentTask!);
                continue;
            }

            if (!hasStored)
            {
                if (SnapshotEquals(baselineTask, currentTask))
                    continue;

                reason = $"stored goal no longer contains task {ShortGoalId(taskId)} changed by tick";
                merged = stored;
                return false;
            }

            results.Add(MergeTaskSnapshot(baselineTask!, storedTask!, currentTask!));
        }

        merged = results;
        reason = "stored version advanced during tick; merged task deltas";
        return true;
    }

    private static TaskSnapshot MergeTaskSnapshot(TaskSnapshot baseline, TaskSnapshot stored, TaskSnapshot current)
    {
        var attemptAuthority = PickChangedAttemptAuthority(baseline, stored, current);
        return stored with
        {
            Description = PickStoreOwned(baseline.Description, stored.Description, current.Description),
            RequiredRole = PickStoreOwned(baseline.RequiredRole, stored.RequiredRole, current.RequiredRole),
            Status = attemptAuthority is null ? PickTickOwned(baseline.Status, stored.Status, current.Status) : attemptAuthority.Status,
            AssignedAgentId = attemptAuthority is null ? PickTickOwned(baseline.AssignedAgentId, stored.AssignedAgentId, current.AssignedAgentId) : attemptAuthority.AssignedAgentId,
            LastExecution = attemptAuthority is null ? PickTickOwned(baseline.LastExecution, stored.LastExecution, current.LastExecution) : attemptAuthority.LastExecution,
            LastVerification = attemptAuthority is null ? PickStoreOwned(baseline.LastVerification, stored.LastVerification, current.LastVerification) : attemptAuthority.LastVerification,
            VerificationHistory = PickStoreOwnedList(baseline.VerificationHistory, stored.VerificationHistory, current.VerificationHistory),
            LastDispatch = PreserveWorkerBuildEnrollment(
                attemptAuthority is null ? PickTickOwned(baseline.LastDispatch, stored.LastDispatch, current.LastDispatch) : attemptAuthority.LastDispatch,
                stored.LastDispatch, current.LastDispatch),
            LastProcess = attemptAuthority is null ? PickSameAttemptProcess(baseline.LastProcess, stored.LastProcess, current.LastProcess) : attemptAuthority.LastProcess,
            // Operation ownership is independent of the worker-attempt snapshot. A stale tick may
            // advance other attempt fields while the persisted owner has already launched its child.
            CompletionBuildEvidence = MergeCompletionBuildEvidence(
                baseline.CompletionBuildEvidence, stored.CompletionBuildEvidence, current.CompletionBuildEvidence),
            WorkerBuildInvocations = MergeWorkerBuildInvocations(stored.WorkerBuildInvocations, current.WorkerBuildInvocations),
            VerificationPlan = PickStoreOwned(baseline.VerificationPlan, stored.VerificationPlan, current.VerificationPlan),
            SubscriptionRetryAfter = PickStoreOwned(baseline.SubscriptionRetryAfter, stored.SubscriptionRetryAfter, current.SubscriptionRetryAfter),
            SubscriptionDeferralReleasedAt = PickStoreOwned(baseline.SubscriptionDeferralReleasedAt, stored.SubscriptionDeferralReleasedAt, current.SubscriptionDeferralReleasedAt),
            SubscriptionLimitReviewNote = PickStoreOwned(baseline.SubscriptionLimitReviewNote, stored.SubscriptionLimitReviewNote, current.SubscriptionLimitReviewNote),
            SubscriptionLimitReviewedAt = PickStoreOwned(baseline.SubscriptionLimitReviewedAt, stored.SubscriptionLimitReviewedAt, current.SubscriptionLimitReviewedAt),
            SubscriptionLimitReviewedFailureCount = PickStoreOwned(baseline.SubscriptionLimitReviewedFailureCount, stored.SubscriptionLimitReviewedFailureCount, current.SubscriptionLimitReviewedFailureCount),
            CriterionRetryCount = PickStoreOwned(baseline.CriterionRetryCount, stored.CriterionRetryCount, current.CriterionRetryCount),
            WorkerBuildCheckRecoveryCount = PickStoreOwned(baseline.WorkerBuildCheckRecoveryCount, stored.WorkerBuildCheckRecoveryCount, current.WorkerBuildCheckRecoveryCount),
            CriterionRetryFeedback = PickStoreOwnedList(baseline.CriterionRetryFeedback, stored.CriterionRetryFeedback, current.CriterionRetryFeedback),
            CriterionRetryFeedbackRoundAt = PickStoreOwned(baseline.CriterionRetryFeedbackRoundAt, stored.CriterionRetryFeedbackRoundAt, current.CriterionRetryFeedbackRoundAt),
            AcceptedRetryFeedback = PickStoreOwned(baseline.AcceptedRetryFeedback, stored.AcceptedRetryFeedback, current.AcceptedRetryFeedback),
            AcceptanceRetryRoutingHold = PickStoreOwned(baseline.AcceptanceRetryRoutingHold, stored.AcceptanceRetryRoutingHold, current.AcceptanceRetryRoutingHold),
            OperatorRoutingNotes = OperatorRoutingNoteLedger.MergeNotes(stored.OperatorRoutingNotes, current.OperatorRoutingNotes),
            OperatorRoutingAutomaticFeedback = PickStoreOwnedList(baseline.OperatorRoutingAutomaticFeedback, stored.OperatorRoutingAutomaticFeedback, current.OperatorRoutingAutomaticFeedback),
            EmptyOutputRetryCount = PickStoreOwned(baseline.EmptyOutputRetryCount, stored.EmptyOutputRetryCount, current.EmptyOutputRetryCount),
            LatestRetryAt = PickStoreOwned(baseline.LatestRetryAt, stored.LatestRetryAt, current.LatestRetryAt),
            LatestRetryInherited = PickStoreOwned(baseline.LatestRetryInherited, stored.LatestRetryInherited, current.LatestRetryInherited),
            PendingRetryRoundKind = PickStoreOwned(baseline.PendingRetryRoundKind, stored.PendingRetryRoundKind, current.PendingRetryRoundKind),
            PendingRetryCause = PickStoreOwned(baseline.PendingRetryCause, stored.PendingRetryCause, current.PendingRetryCause),
            RetryAdmissionHistory = MergeRetryAdmissionHistory(
                stored.RetryAdmissionHistory,
                current.RetryAdmissionHistory),
            RetryAdmissionHoldRoute = PickStoreOwned(
                baseline.RetryAdmissionHoldRoute,
                stored.RetryAdmissionHoldRoute,
                current.RetryAdmissionHoldRoute),
            InterruptedDispatchRecoveryId = attemptAuthority is null
                ? PickTickOwned(baseline.InterruptedDispatchRecoveryId, stored.InterruptedDispatchRecoveryId, current.InterruptedDispatchRecoveryId)
                : attemptAuthority.InterruptedDispatchRecoveryId,
            WasCancelledByConductor = attemptAuthority is null
                ? PickTickOwned(baseline.WasCancelledByConductor, stored.WasCancelledByConductor, current.WasCancelledByConductor)
                : attemptAuthority.WasCancelledByConductor,
            // Pre-review receipts are produced by the conductor tick. Preserve the tick's
            // current-HEAD evidence when an unrelated store mutation advances concurrently.
            PreReviewEvidenceReceipt = PickTickOwned(baseline.PreReviewEvidenceReceipt, stored.PreReviewEvidenceReceipt, current.PreReviewEvidenceReceipt)
        };
    }

    private static IReadOnlyList<WorkerBuildInvocation> MergeWorkerBuildInvocations(
        IReadOnlyList<WorkerBuildInvocation>? stored, IReadOnlyList<WorkerBuildInvocation>? current)
    {
        var result = (stored ?? []).ToDictionary(i => i.Id);
        foreach (var incoming in current ?? [])
        {
            if (!result.TryGetValue(incoming.Id, out var previous)) { result.Add(incoming.Id, incoming); continue; }
            if (!previous.SameOrigin(incoming)) throw new InvalidOperationException("Conflicting worker build origin.");
            var older = previous.Version <= incoming.Version ? previous : incoming;
            var newer = previous.Version <= incoming.Version ? incoming : previous;
            if (older.Version == newer.Version)
            {
                if (System.Text.Json.JsonSerializer.Serialize(older) != System.Text.Json.JsonSerializer.Serialize(newer))
                    throw new InvalidOperationException("Conflicting worker build transition.");
            }
            else
            {
                // A stale snapshot may skip multiple host transitions, but cannot rewrite their prefix.
                older.RequireSuccessor(newer with { Version = older.Version + 1 });
            }
            result[incoming.Id] = newer;
        }
        return result.Values.OrderBy(i => i.DispatchAt).ThenBy(i => i.Id, StringComparer.Ordinal).ToArray();
    }

    private static TaskDispatchSnapshot? PreserveWorkerBuildEnrollment(TaskDispatchSnapshot? selected,
        TaskDispatchSnapshot? stored, TaskDispatchSnapshot? current)
    {
        if (selected is null) return null;
        foreach (var candidate in new[] { stored, current })
        {
            if (candidate is null || candidate.DispatchedAt != selected.DispatchedAt || candidate.WorkerBuildProtocolVersion == 0) continue;
            if (candidate.Command != selected.Command || candidate.WorkingDirectory != selected.WorkingDirectory ||
                candidate.WorkerBuildProtocolVersion != 1)
                throw new InvalidOperationException("Worker build enrollment cannot be transferred to a different dispatch origin.");
            selected = selected with { WorkerBuildProtocolVersion = 1 };
        }
        return selected;
    }

    private static IReadOnlyList<RetryAdmissionReceipt> MergeRetryAdmissionHistory(
        IReadOnlyList<RetryAdmissionReceipt>? stored,
        IReadOnlyList<RetryAdmissionReceipt>? current)
    {
        var merged = new Dictionary<string, RetryAdmissionReceipt>(StringComparer.Ordinal);
        foreach (var receipt in stored ?? [])
            merged[receipt.ReceiptId] = receipt;
        foreach (var receipt in current ?? [])
        {
            if (!merged.TryGetValue(receipt.ReceiptId, out var existing) ||
                existing.ReservationReleasedAt is null &&
                (receipt.ReservationReleasedAt is not null ||
                 existing.WorkerStartedAt is null && receipt.WorkerStartedAt is not null))
            {
                merged[receipt.ReceiptId] = receipt;
            }
        }

        return merged.Values
            .OrderBy(receipt => receipt.RecordedAt)
            .ThenBy(receipt => receipt.ReceiptId, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<ProgressEventSnapshot> MergeTimeline(
        IReadOnlyList<ProgressEventSnapshot> baseline,
        IReadOnlyList<ProgressEventSnapshot> stored,
        IReadOnlyList<ProgressEventSnapshot> current,
        bool preserveStoredOrder)
    {
        var baselineKeys = baseline.Select(SnapshotKey).ToHashSet(StringComparer.Ordinal);
        var merged = stored.ToList();
        var mergedKeys = merged.Select(SnapshotKey).ToHashSet(StringComparer.Ordinal);
        foreach (var evt in current)
        {
            var key = SnapshotKey(evt);
            if (baselineKeys.Contains(key) || !mergedKeys.Add(key))
                continue;

            merged.Add(evt);
        }

        return preserveStoredOrder ? merged : merged.OrderBy(evt => evt.OccurredAt).ToList();
    }

    private static T PickTickOwned<T>(T baseline, T stored, T current) =>
        PickWithSameFieldPrecedence(baseline, stored, current, preferStoredOnConflict: false);

    private static T PickStoreOwned<T>(T baseline, T stored, T current) =>
        PickWithSameFieldPrecedence(baseline, stored, current, preferStoredOnConflict: true);

    private static TaskSnapshot? PickChangedAttemptAuthority(
        TaskSnapshot baseline,
        TaskSnapshot stored,
        TaskSnapshot current)
    {
        var storedDispatchChanged = !SameDispatchAttempt(baseline.LastDispatch, stored.LastDispatch);
        var currentDispatchChanged = !SameDispatchAttempt(baseline.LastDispatch, current.LastDispatch);
        if (storedDispatchChanged != currentDispatchChanged)
            return storedDispatchChanged ? stored : current;
        if (storedDispatchChanged)
            return PickNewerDispatchAttempt(stored, current);

        var storedProcessChanged = !SameProcessAttempt(baseline.LastProcess, stored.LastProcess);
        var currentProcessChanged = !SameProcessAttempt(baseline.LastProcess, current.LastProcess);
        if (storedProcessChanged != currentProcessChanged)
            return storedProcessChanged ? stored : current;
        if (storedProcessChanged)
            return PickNewerProcessAttempt(stored, current);

        if (stored.LastProcess?.WasCancelled != current.LastProcess?.WasCancelled)
            return stored.LastProcess?.WasCancelled is true ? stored : current;

        return null;
    }

    private static TaskSnapshot PickNewerDispatchAttempt(TaskSnapshot stored, TaskSnapshot current)
    {
        if (stored.LastDispatch is null)
            return current.LastDispatch is null ? stored : current;
        if (current.LastDispatch is null)
            return stored;

        return stored.LastDispatch.DispatchedAt >= current.LastDispatch.DispatchedAt ? stored : current;
    }

    private static TaskSnapshot PickNewerProcessAttempt(TaskSnapshot stored, TaskSnapshot current)
    {
        if (stored.LastProcess is null)
            return current.LastProcess is null ? stored : current;
        if (current.LastProcess is null)
            return stored;
        if (stored.LastProcess.StartedAt != current.LastProcess.StartedAt)
            return stored.LastProcess.StartedAt > current.LastProcess.StartedAt ? stored : current;
        if (stored.LastProcess.WasCancelled != current.LastProcess.WasCancelled)
            return stored.LastProcess.WasCancelled ? stored : current;
        if (stored.LastProcess.CompletedAt is null != current.LastProcess.CompletedAt is null)
            return stored.LastProcess.CompletedAt is not null ? stored : current;

        return stored.LastProcess.CompletedAt >= current.LastProcess.CompletedAt ? stored : current;
    }

    private static bool SameDispatchAttempt(TaskDispatchSnapshot? left, TaskDispatchSnapshot? right)
    {
        if (left is null || right is null)
            return left is null && right is null;

        return left.DispatchedAt == right.DispatchedAt &&
            (string.IsNullOrWhiteSpace(left.AssignedAgentId) ||
             string.IsNullOrWhiteSpace(right.AssignedAgentId) ||
             string.Equals(left.AssignedAgentId, right.AssignedAgentId, StringComparison.OrdinalIgnoreCase));
    }

    private static bool SameProcessAttempt(TaskProcessSnapshot? left, TaskProcessSnapshot? right)
    {
        if (left is null || right is null)
            return left is null && right is null;

        return left.ProcessId == right.ProcessId && left.StartedAt == right.StartedAt;
    }

    private static TaskProcessSnapshot? PickSameAttemptProcess(
        TaskProcessSnapshot? baseline,
        TaskProcessSnapshot? stored,
        TaskProcessSnapshot? current)
    {
        var storedChanged = !SnapshotEquals(baseline, stored);
        var currentChanged = !SnapshotEquals(baseline, current);
        if (!storedChanged || !currentChanged)
            return PickTickOwned(baseline, stored, current);

        if (stored?.WasCancelled != current?.WasCancelled)
            return stored?.WasCancelled is true ? stored : current;
        if (stored?.CompletedAt is null != current?.CompletedAt is null)
            return stored?.CompletedAt is not null ? stored : current;
        if (stored?.CompletedAt != current?.CompletedAt)
            return stored?.CompletedAt > current?.CompletedAt ? stored : current;

        return current;
    }

    private static T PickWithSameFieldPrecedence<T>(T baseline, T stored, T current, bool preferStoredOnConflict)
    {
        var storedChanged = !SnapshotEquals(baseline, stored);
        var currentChanged = !SnapshotEquals(baseline, current);

        return (storedChanged, currentChanged) switch
        {
            (true, true) => preferStoredOnConflict ? stored : current,
            (true, false) => stored,
            (false, true) => current,
            _ => stored
        };
    }

    private static IReadOnlyList<T>? PickStoreOwnedList<T>(
        IReadOnlyList<T>? baseline,
        IReadOnlyList<T>? stored,
        IReadOnlyList<T>? current) =>
        PickStoreOwned(baseline, stored, current);

    private static bool SnapshotEquals(GoalSnapshot left, GoalSnapshot right) =>
        GoalSnapshotByteAccounting.Serialize(left, SerializerOptions) ==
        GoalSnapshotByteAccounting.Serialize(right, SerializerOptions);

    private static bool SnapshotEquals<T>(T? left, T? right) =>
        JsonSerializer.Serialize(left, SerializerOptions) == JsonSerializer.Serialize(right, SerializerOptions);

    private static string SnapshotKey<T>(T snapshot) =>
        JsonSerializer.Serialize(snapshot, SerializerOptions);

    private static string ShortGoalId(string goalId) =>
        goalId.Length <= 8 ? goalId : goalId[..8];

    private static async Task WriteSnapshotAsync(
        SqliteConnection conn,
        AgentOrchestratorKernel kernel,
        SqliteWriteTelemetry.WriteTelemetryScope? telemetry,
        CancellationToken cancellationToken)
    {
        var snapshot = kernel.ExportSnapshot();
        var updatedAt = DateTimeOffset.UtcNow.ToString("O");

        foreach (var goal in snapshot.Goals)
        {
            var goalWrite = await UpsertGoalRowAsync(
                conn,
                goal,
                updatedAt,
                versionSql: "goals.version + 1",
                cancellationToken,
                skipUnchanged: true);
            telemetry?.AddWrite(goalWrite);
            if (goalWrite.RowsWritten > 0)
            {
                telemetry?.AddWrite(await UpsertModelFitHistoryRowsAsync(conn, goal, cancellationToken));
            }
        }

        foreach (var request in snapshot.HumanInputRequests)
        {
            var json = JsonSerializer.Serialize(request, SerializerOptions);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO human_input_requests (id, goal_id, snapshot_json)
                VALUES ($id, $goal_id, $json)
                ON CONFLICT(id) DO UPDATE SET
                    goal_id       = excluded.goal_id,
                    snapshot_json = excluded.snapshot_json
                """;
            cmd.Parameters.AddWithValue("$id", request.Id);
            cmd.Parameters.AddWithValue("$goal_id", request.GoalId);
            cmd.Parameters.AddWithValue("$json", json);
            telemetry?.AddWrite(await cmd.ExecuteNonQueryAsync(cancellationToken), Encoding.UTF8.GetByteCount(json));
        }
    }

    public async Task<GoalSnapshot?> LoadGoalAsync(GoalId goalId, CancellationToken cancellationToken = default)
    {
        return await WithBusyRetryAsync(async () =>
        {
            await using var conn = OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT snapshot_json FROM goals WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", goalId.Value);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return (GoalSnapshot?)null;
            var markers = new GoalTaskHistoryStore.DocumentMarkers();
            var snapshot = TaskHistoryStore.DecodeDocument(reader.GetString(0), markers);
            return await HydrateReadGoalAsync(conn, snapshot, markers, cancellationToken);
        }, cancellationToken);
    }

    internal async Task<IReadOnlyList<GoalSnapshot>> FindGoalSnapshotsByIdPrefixAsync(
        string idPrefix,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ValidateHistoricalIdPrefixLookup(idPrefix, limit);
        return await WithBusyRetryAsync(async () =>
        {
            var matches = new List<GoalSnapshot>();
            await using var conn = OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT snapshot_json FROM goals WHERE id LIKE $prefix COLLATE NOCASE ORDER BY id LIMIT $limit";
            cmd.Parameters.AddWithValue("$prefix", idPrefix + "%");
            cmd.Parameters.AddWithValue("$limit", limit);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var markers = new GoalTaskHistoryStore.DocumentMarkers();
                var snapshot = TaskHistoryStore.DecodeDocument(reader.GetString(0), markers)
                    ?? throw new InvalidOperationException("Stored goal snapshot could not be deserialized.");
                matches.Add((await HydrateReadGoalAsync(conn, snapshot, markers, cancellationToken))!);
            }

            return (IReadOnlyList<GoalSnapshot>)matches;
        }, cancellationToken);
    }

    internal async Task<IReadOnlyList<CitedPriorTaskMatch>> FindTaskSnapshotsByIdPrefixAsync(
        string idPrefix,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ValidateHistoricalIdPrefixLookup(idPrefix, limit);
        return await WithBusyRetryAsync(async () =>
        {
            var matches = new List<CitedPriorTaskMatch>();
            await using var conn = OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT goals.snapshot_json
                FROM goals
                WHERE EXISTS (
                    SELECT 1
                    FROM json_each(goals.snapshot_json, '$.Tasks') AS task
                    WHERE json_extract(task.value, '$.Id') LIKE $prefix COLLATE NOCASE
                )
                ORDER BY goals.id
                LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$prefix", idPrefix + "%");
            cmd.Parameters.AddWithValue("$limit", limit);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var markers = new GoalTaskHistoryStore.DocumentMarkers();
                var goal = TaskHistoryStore.DecodeDocument(reader.GetString(0), markers)
                    ?? throw new InvalidOperationException("Stored goal snapshot could not be deserialized.");
                goal = (await HydrateReadGoalAsync(conn, goal, markers, cancellationToken))!;
                foreach (var task in goal.Tasks.Where(task => task.Id.StartsWith(idPrefix, StringComparison.OrdinalIgnoreCase)))
                {
                    matches.Add(new CitedPriorTaskMatch(goal, task));
                    if (matches.Count == limit)
                    {
                        return (IReadOnlyList<CitedPriorTaskMatch>)matches;
                    }
                }
            }

            return (IReadOnlyList<CitedPriorTaskMatch>)matches;
        }, cancellationToken);
    }

    private static void ValidateHistoricalIdPrefixLookup(string idPrefix, int limit)
    {
        if (idPrefix.Length is < 8 or > 32 || idPrefix.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Historical id prefixes must contain 8 to 32 hexadecimal characters.", nameof(idPrefix));
        }

        if (limit is < 1 or > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Historical id prefix lookups are capped at two matches.");
        }
    }

    public static async Task<long?> TryLoadGoalStateVersionAsync(
        string dbPath,
        string goalId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dbPath) ||
            string.IsNullOrWhiteSpace(goalId) ||
            !File.Exists(dbPath))
        {
            return null;
        }

        try
        {
            await using var conn = StateDbConnectionFactory.Open(
                dbPath,
                StateDbConnectionProfile.QueryOnlyRead);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(version, 0) FROM goals WHERE id = $id LIMIT 1";
            cmd.Parameters.AddWithValue("$id", goalId);
            var value = await cmd.ExecuteScalarAsync(cancellationToken);
            return value is null or DBNull ? null : Convert.ToInt64(value);
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    public async Task<T> TransactGoalAsync<T>(
        GoalId goalId,
        Func<GoalSnapshot?, CancellationToken, Task<(bool ShouldSave, GoalSnapshot? NewSnapshot, T Result)>> transaction,
        CancellationToken cancellationToken = default)
    {
        return await TransactGoalAsync(
            ResolveOperationTag($"{nameof(TransactGoalAsync)}({ShortGoalId(goalId.Value)})"),
            goalId,
            transaction,
            cancellationToken);
    }

    public async Task<T> TransactGoalAsync<T>(
        string operationName,
        GoalId goalId,
        Func<GoalSnapshot?, CancellationToken, Task<(bool ShouldSave, GoalSnapshot? NewSnapshot, T Result)>> transaction,
        CancellationToken cancellationToken = default)
    {
        var versionMismatchDelay = 50;
        var resolvedOperation = ResolveOperationTag($"{nameof(TransactGoalAsync)}({ShortGoalId(goalId.Value)})", operationName);
        for (var attempt = 1; ; attempt++)
        {
            // Load this goal's snapshot and version outside the write transaction.
            // WAL mode provides a consistent reader snapshot without an explicit read lock.
            var (loadedSnapshot, loadedVersion) = await LoadGoalSnapshotAndVersionAsync(goalId, cancellationToken);

            // Delegate produces the new snapshot to write (runs outside the write lock).
            var (shouldSave, newSnapshot, result) = await transaction(loadedSnapshot, cancellationToken);

            if (!shouldSave || newSnapshot is null)
                return result;

            // Short BEGIN IMMEDIATE: re-read version, write one row if version unchanged.
            var casSucceeded = await TryCasWriteGoalRowAsync(
                goalId,
                newSnapshot,
                humanInputRequests: null,
                loadedVersion,
                resolvedOperation,
                cancellationToken);
            if (casSucceeded)
                return result;

            // Version mismatch detected: concurrent writer incremented the version between our
            // load and our CAS write. Treat this as a transient error and retry the full
            // load-mutate-CAS cycle — same retry budget as SQLITE_BUSY.
            if (attempt >= MaxOptimisticConcurrencyRetries)
                throw new GoalTransactionConflictException(
                    $"TransactGoalAsync: optimistic concurrency retries exhausted for goal {goalId.Value[..8]}");

            await Task.Delay(versionMismatchDelay, cancellationToken);
            versionMismatchDelay = Math.Min(versionMismatchDelay * 2, 1000);
        }
    }

    public async Task<T> TransactGoalStateAsync<T>(
        GoalId goalId,
        Func<GoalStateSnapshot?, CancellationToken, Task<(bool ShouldSave, GoalStateSnapshot? NewState, T Result)>> transaction,
        CancellationToken cancellationToken = default)
    {
        return await TransactGoalStateAsync(
            ResolveOperationTag($"{nameof(TransactGoalStateAsync)}({ShortGoalId(goalId.Value)})"),
            goalId,
            transaction,
            cancellationToken);
    }

    public Task<T> TransactGoalStateAsync<T>(
        string operationName,
        GoalId goalId,
        Func<GoalStateSnapshot?, CancellationToken, Task<(bool ShouldSave, GoalStateSnapshot? NewState, T Result)>> transaction,
        CancellationToken cancellationToken = default) =>
        TransactGoalStateVersionedAsync(operationName, goalId, (state, _, token) => transaction(state, token),
            cancellationToken);

    private async Task<T> TransactGoalStateVersionedAsync<T>(
        string operationName,
        GoalId goalId,
        Func<GoalStateSnapshot?, int, CancellationToken,
            Task<(bool ShouldSave, GoalStateSnapshot? NewState, T Result)>> transaction,
        CancellationToken cancellationToken)
    {
        var versionMismatchDelay = 50;
        var resolvedOperation = ResolveOperationTag($"{nameof(TransactGoalStateAsync)}({ShortGoalId(goalId.Value)})", operationName);
        for (var attempt = 1; ; attempt++)
        {
            var (loadedState, loadedVersion) = await LoadGoalStateAndVersionAsync(goalId, cancellationToken);
            var (shouldSave, newState, result) = await transaction(loadedState, loadedVersion, cancellationToken);

            if (!shouldSave || newState is null)
                return result;

            if (!string.Equals(newState.Goal.Id, goalId.Value, StringComparison.Ordinal) ||
                newState.HumanInputRequests.Any(request =>
                    !string.Equals(request.GoalId, goalId.Value, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"TransactGoalStateAsync for goal {goalId.Value[..8]} cannot persist state owned by another goal.");
            }

            var casSucceeded = await TryCasWriteGoalRowAsync(
                goalId,
                newState.Goal,
                newState.HumanInputRequests,
                loadedVersion,
                resolvedOperation,
                cancellationToken);
            if (casSucceeded)
                return result;

            if (attempt >= MaxOptimisticConcurrencyRetries)
                throw new GoalTransactionConflictException(
                    $"TransactGoalStateAsync: optimistic concurrency retries exhausted for goal {goalId.Value[..8]}");

            await Task.Delay(versionMismatchDelay, cancellationToken);
            versionMismatchDelay = Math.Min(versionMismatchDelay * 2, 1000);
        }
    }

    internal Action? BeforeGoalStateOutboxCommit { get; init; }

    public async Task<T> TransactGoalStateWithOutboxAsync<T>(
        string operationName,
        GoalId goalId,
        Func<GoalStateSnapshot?, CancellationToken, Task<(bool ShouldSave, GoalStateSnapshot? NewState,
            T Result, IReadOnlyList<OrchestratorStateOutboxMessage> OutboxMessages)>> transaction,
        CancellationToken cancellationToken = default)
    {
        var versionMismatchDelay = 50;
        var resolvedOperation = ResolveOperationTag(
            $"{nameof(TransactGoalStateWithOutboxAsync)}({ShortGoalId(goalId.Value)})", operationName);
        for (var attempt = 1; ; attempt++)
        {
            var (loadedState, loadedVersion) = await LoadGoalStateAndVersionAsync(goalId, cancellationToken);
            var (shouldSave, newState, result, messages) = await transaction(loadedState, cancellationToken);
            if (!shouldSave || newState is null)
                return result;

            if (!string.Equals(newState.Goal.Id, goalId.Value, StringComparison.Ordinal) ||
                newState.HumanInputRequests.Any(request =>
                    !string.Equals(request.GoalId, goalId.Value, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"TransactGoalStateWithOutboxAsync for goal {goalId.Value[..8]} cannot persist state owned by another goal.");
            }

            if (await TryCasWriteGoalRowAsync(goalId, newState.Goal, newState.HumanInputRequests,
                    loadedVersion, resolvedOperation, cancellationToken, messages, BeforeGoalStateOutboxCommit))
                return result;

            if (attempt >= MaxOptimisticConcurrencyRetries)
                throw new GoalTransactionConflictException(
                    $"TransactGoalStateWithOutboxAsync: optimistic concurrency retries exhausted for goal {goalId.Value[..8]}");

            await Task.Delay(versionMismatchDelay, cancellationToken);
            versionMismatchDelay = Math.Min(versionMismatchDelay * 2, 1000);
        }
    }

    private async Task<(GoalSnapshot? Snapshot, int Version)> LoadGoalSnapshotAndVersionAsync(
        GoalId goalId, CancellationToken cancellationToken)
    {
        return await WithBusyRetryAsync(async () =>
        {
            await using var conn = OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT snapshot_json, COALESCE(version, 0) FROM goals WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", goalId.Value);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return ((GoalSnapshot?)null, 0);
            var version = reader.GetInt32(1);
            var markers = new GoalTaskHistoryStore.DocumentMarkers();
            var snapshot = TaskHistoryStore.DecodeDocument(reader.GetString(0), markers);
            snapshot = await HydrateReadGoalAsync(conn, snapshot, markers, cancellationToken);
            return (snapshot, version);
        }, cancellationToken);
    }

    private async Task<(GoalStateSnapshot? State, int Version)> LoadGoalStateAndVersionAsync(
        GoalId goalId,
        CancellationToken cancellationToken)
    {
        return await WithBusyRetryAsync(async () =>
        {
            await using var conn = OpenConnection();
            GoalSnapshot? goalSnapshot;
            int version;
            await using (var goalCommand = conn.CreateCommand())
            {
                goalCommand.CommandText = "SELECT snapshot_json, COALESCE(version, 0) FROM goals WHERE id = $id";
                goalCommand.Parameters.AddWithValue("$id", goalId.Value);
                await using var reader = await goalCommand.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                    return ((GoalStateSnapshot?)null, 0);

                var markers = new GoalTaskHistoryStore.DocumentMarkers();
                goalSnapshot = TaskHistoryStore.DecodeDocument(reader.GetString(0), markers);
                version = reader.GetInt32(1);
                goalSnapshot = await HydrateReadGoalAsync(conn, goalSnapshot, markers, cancellationToken);
            }

            var humanInputRequests = new List<HumanInputRequestSnapshot>();
            await using (var inputCommand = conn.CreateCommand())
            {
                inputCommand.CommandText = "SELECT snapshot_json FROM human_input_requests WHERE goal_id = $goal_id";
                inputCommand.Parameters.AddWithValue("$goal_id", goalId.Value);
                await using var reader = await inputCommand.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var request = JsonSerializer.Deserialize<HumanInputRequestSnapshot>(
                        reader.GetString(0),
                        SerializerOptions);
                    if (request is not null)
                        humanInputRequests.Add(request);
                }
            }

            return (new GoalStateSnapshot(goalSnapshot!, humanInputRequests), version);
        }, cancellationToken);
    }

    // Attempts a short CAS write for one goal row. Acquires BEGIN IMMEDIATE, re-reads the version,
    // writes only if it matches expectedVersion, increments version, then commits.
    // Returns true on success, false when the version has changed (caller should retry).
    private async Task<bool> TryCasWriteGoalRowAsync(
        GoalId goalId,
        GoalSnapshot snapshot,
        IReadOnlyList<HumanInputRequestSnapshot>? humanInputRequests,
        int expectedVersion,
        string operationName,
        CancellationToken cancellationToken,
        IReadOnlyList<OrchestratorStateOutboxMessage>? outboxMessages = null,
        Action? beforeCommit = null)
    {
        var write = await BeginWriteAsync(operationName, cancellationToken);
        await using var conn = write.Connection;
        var telemetry = write.Telemetry;
        try
        {
            int currentVersion;
            await using (var checkCmd = conn.CreateCommand())
            {
                checkCmd.CommandText = "SELECT COALESCE(version, 0) FROM goals WHERE id = $id";
                checkCmd.Parameters.AddWithValue("$id", goalId.Value);
                var val = await checkCmd.ExecuteScalarAsync(cancellationToken);
                currentVersion = val is null or DBNull ? 0 : Convert.ToInt32(val);
            }

            if (currentVersion != expectedVersion)
            {
                await RunNonQueryAsync(conn, "ROLLBACK", cancellationToken);
                telemetry.Emit("rollback");
                return false;
            }

            var updatedAt = DateTimeOffset.UtcNow.ToString("O");
            telemetry.AddWrite(await UpsertGoalRowAsync(conn, snapshot, updatedAt, versionSql: "$version", cancellationToken, currentVersion + 1));
            telemetry.AddWrite(await UpsertModelFitHistoryRowsAsync(conn, snapshot, cancellationToken));
            if (humanInputRequests is not null)
            {
                foreach (var request in humanInputRequests)
                {
                    var json = JsonSerializer.Serialize(request, SerializerOptions);
                    await using var inputCommand = conn.CreateCommand();
                    inputCommand.CommandText = """
                        INSERT INTO human_input_requests (id, goal_id, snapshot_json)
                        VALUES ($id, $goal_id, $json)
                        ON CONFLICT(id) DO UPDATE SET
                            goal_id       = excluded.goal_id,
                            snapshot_json = excluded.snapshot_json
                        """;
                    inputCommand.Parameters.AddWithValue("$id", request.Id);
                    inputCommand.Parameters.AddWithValue("$goal_id", request.GoalId);
                    inputCommand.Parameters.AddWithValue("$json", json);
                    telemetry.AddWrite(
                        await inputCommand.ExecuteNonQueryAsync(cancellationToken),
                        Encoding.UTF8.GetByteCount(json));
                }
            }

            if (outboxMessages is not null)
            {
                foreach (var message in outboxMessages)
                    await InsertOutboxMessageAsync(conn, message, cancellationToken);
            }
            beforeCommit?.Invoke();
            await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
            telemetry.Emit("commit");
            return true;
        }
        catch (Exception ex)
        {
            try { await RunNonQueryAsync(conn, "ROLLBACK", cancellationToken); } catch { }
            telemetry.Emit("rollback", ex);
            throw;
        }
    }

    private static async Task<(int RowsWritten, long SerializedBytes)> UpsertGoalRowAsync(
        SqliteConnection conn,
        GoalSnapshot goal,
        string updatedAt,
        string versionSql,
        CancellationToken cancellationToken,
        int? version = null,
        bool skipUnchanged = false)
    {
        if (goal.IsMetadataOnly)
        {
            throw new InvalidOperationException($"Refusing to persist metadata-only goal snapshot '{ShortGoalId(goal.Id)}'. Hydrate the full aggregate before saving.");
        }

        if (goal.TickRowVersion is not null)
        {
            var stored = await ReadGoalSnapshotsAsync(conn, [new GoalId(goal.Id)], cancellationToken, readObserver: null);
            if (!stored.TryGetValue(goal.Id, out var existing))
                throw new InvalidOperationException($"Cannot save deferred rows for missing goal '{goal.Id}'.");
            goal = CompleteTickSnapshot(goal, existing, version - 1);
        }

        var timeline = await new GoalTimelineStore(SerializerOptions).WriteAsync(conn, goal, cancellationToken);
        using var measurement = GoalSnapshotByteAccounting.PrepareWriteMeasurement();
        var prepared = TaskHistoryStore.PrepareWrite(goal, timeline.MarkerCount, measurement);
        var json = prepared.Document;
        var historyChanged = await TaskHistoryStore.WriteAsync(conn, prepared, cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $$"""
            INSERT INTO goals (id, status, objective, source_backlog_item_id, updated_at, snapshot_json, version)
            VALUES ($id, $status, $objective, $source_backlog_item_id, $updated_at, $json, COALESCE($version, 1))
            ON CONFLICT(id) DO UPDATE SET
                status        = excluded.status,
                objective     = excluded.objective,
                source_backlog_item_id = excluded.source_backlog_item_id,
                snapshot_json = excluded.snapshot_json,
                version       = {{versionSql}},
                updated_at    = CASE WHEN excluded.snapshot_json != goals.snapshot_json OR $history_changed = 1
                                     THEN excluded.updated_at
                                     ELSE goals.updated_at END
            WHERE $skip_unchanged = 0 OR excluded.snapshot_json != goals.snapshot_json OR $history_changed = 1
            """;
        cmd.Parameters.AddWithValue("$id", goal.Id);
        cmd.Parameters.AddWithValue("$status", goal.Status.ToString());
        cmd.Parameters.AddWithValue("$objective", goal.Objective);
        cmd.Parameters.AddWithValue("$source_backlog_item_id", (object?)goal.SourceBacklogItemId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$updated_at", updatedAt);
        cmd.Parameters.AddWithValue("$json", json);
        cmd.Parameters.AddWithValue("$version", version is null ? DBNull.Value : version.Value);
        cmd.Parameters.AddWithValue("$skip_unchanged", skipUnchanged ? 1 : 0);
        cmd.Parameters.AddWithValue("$history_changed", historyChanged || timeline.Changed ? 1 : 0);
        var rows = await cmd.ExecuteNonQueryAsync(cancellationToken);
        var serializedBytes = rows > 0 ? Encoding.UTF8.GetByteCount(json) : 0;
        if (rows > 0 && measurement is not null)
            measurement.Utf8Bytes = serializedBytes;
        return (rows, serializedBytes);
    }

    private static string GoalMetadataTitleSql()
    {
        var normalized = "replace(objective, char(13), char(10))";
        var firstLine = $"CASE WHEN instr({normalized}, char(10)) > 0 THEN substr({normalized}, 1, instr({normalized}, char(10)) - 1) ELSE objective END";
        return $"substr(({firstLine}), 1, {GoalMetadataTitleMaxChars})";
    }

    private static async Task<(int RowsWritten, long SerializedBytes)> UpsertModelFitHistoryRowAsync(
        SqliteConnection conn,
        ModelFitHistoryRow row,
        CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO model_fit_history (
                goal_id, task_id, role, provider_name, model_name, complexity, task_shape, outcome, self_rating, timestamp, outcome_rule, outcome_class, dispatch_lane)
            VALUES (
                $goal_id, $task_id, $role, $provider_name, $model_name, $complexity, $task_shape, $outcome, $self_rating, $timestamp, $outcome_rule, $outcome_class, $dispatch_lane)
            ON CONFLICT(goal_id, task_id, timestamp) DO UPDATE SET
                role          = excluded.role,
                provider_name = excluded.provider_name,
                model_name    = excluded.model_name,
                complexity    = excluded.complexity,
                task_shape    = excluded.task_shape,
                outcome       = excluded.outcome,
                self_rating   = excluded.self_rating,
                outcome_rule  = excluded.outcome_rule,
                outcome_class = excluded.outcome_class,
                dispatch_lane = excluded.dispatch_lane
            """;
        cmd.Parameters.AddWithValue("$goal_id", row.GoalId);
        cmd.Parameters.AddWithValue("$task_id", row.TaskId);
        cmd.Parameters.AddWithValue("$role", row.Role.ToString());
        cmd.Parameters.AddWithValue("$provider_name", row.ProviderName);
        cmd.Parameters.AddWithValue("$model_name", row.ModelName);
        cmd.Parameters.AddWithValue("$complexity", row.Complexity?.ToString() ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$task_shape", row.TaskShape ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$outcome", row.Outcome.ToString());
        cmd.Parameters.AddWithValue("$self_rating", ModelFitHistory.NormalizeSelfRating(row.SelfRating));
        cmd.Parameters.AddWithValue("$timestamp", row.Timestamp.ToString("O"));
        cmd.Parameters.AddWithValue("$outcome_rule", row.OutcomeRule ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$outcome_class", TaskOutcomeClassifier.FormatClass(row.OutcomeClass));
        cmd.Parameters.AddWithValue("$dispatch_lane", row.DispatchLane ?? (object)DBNull.Value);
        return (await cmd.ExecuteNonQueryAsync(cancellationToken), 0);
    }

    private static async Task<(int RowsWritten, long SerializedBytes)> UpsertModelFitHistoryRowsAsync(
        SqliteConnection conn,
        GoalSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var rowsWritten = 0;
        var kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([snapshot], []));
        foreach (var row in ModelFitHistory.FromGoals(kernel.Goals))
        {
            var written = await UpsertModelFitHistoryRowAsync(conn, row, cancellationToken);
            rowsWritten += written.RowsWritten;
        }

        return (rowsWritten, 0);
    }

    private static ModelFitHistoryRow ReadModelFitHistoryRow(SqliteDataReader reader)
    {
        return new ModelFitHistoryRow(
            reader.GetString(0),
            reader.GetString(1),
            Enum.Parse<AgentRole>(reader.GetString(2)),
            reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : Enum.Parse<TaskComplexity>(reader.GetString(5)),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            Enum.Parse<WorkTaskStatus>(reader.GetString(7)),
            ModelFitHistory.NormalizeSelfRating(reader.GetString(8)),
            DateTimeOffset.Parse(reader.GetString(9), null, System.Globalization.DateTimeStyles.RoundtripKind),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            TaskOutcomeClassifier.ParseClass(reader.IsDBNull(11) ? null : reader.GetString(11)),
            reader.IsDBNull(12) ? null : reader.GetString(12));
    }

    private void RunNonQuery(SqliteConnection conn, string sql)
    {
        _statementObserver?.Invoke(sql);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static async Task RunNonQueryAsync(
        SqliteConnection conn,
        string sql,
        CancellationToken cancellationToken,
        int? commandTimeoutSeconds = null)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (commandTimeoutSeconds is { } timeout)
            cmd.CommandTimeout = timeout;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions { WriteIndented = false };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

// The loop owns this memory. It holds snapshots from storage, before kernel repairs or tick mutations.
internal sealed class GoalReloadMemory
{
    internal sealed record Entry(long Version, GoalSnapshot Snapshot);

    private ImmutableDictionary<string, Entry> _entries =
        ImmutableDictionary.Create<string, Entry>(StringComparer.Ordinal);

    internal static async Task<(AgentOrchestratorKernel Kernel, int Reloaded, int Reused)> LoadAsync(
        IOrchestratorStateQueries repository, IReadOnlyCollection<GoalId> goalIds, GoalReloadMemory? memory,
        Action<string, TimeSpan>? timing = null)
    {
        if (memory is not null && repository is SqliteOrchestratorStateRepository sqliteRepository)
            return await sqliteRepository.LoadGoalsReusingUnchangedAsync(goalIds, memory, timing: timing);

        var kernel = await repository.LoadGoalsAsync(goalIds);
        return (kernel, kernel.Goals.Count, 0);
    }

    internal ImmutableDictionary<string, Entry> Entries => Volatile.Read(ref _entries);

    internal void Replace(ImmutableDictionary<string, Entry> entries) => Volatile.Write(ref _entries, entries);
}

internal sealed record TerminalMetadataCacheKey(string Id, string UpdatedAt);

internal sealed record TerminalGoalMetadataValues(
    string? ResultCommit,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? TerminatedAt);

public sealed record GoalSummary
{
    private readonly string? _resultCommit;
    private readonly DateTimeOffset? _createdAt;
    private readonly DateTimeOffset? _terminatedAt;
    private readonly Lazy<TerminalGoalMetadataValues>? _terminalMetadata;

    public GoalSummary(
        string Id,
        string Status,
        string Objective,
        string UpdatedAt,
        string? ResultCommit = null,
        DateTimeOffset? CreatedAt = null,
        DateTimeOffset? TerminatedAt = null,
        string? Condition = null,
        GoalPromotionFact? PromotionFact = null)
        : this(Id, Status, Objective, UpdatedAt, ResultCommit, CreatedAt, TerminatedAt, Condition, terminalMetadataLoader: null)
    {
        if (PromotionFact is not null && PromotionFact.GoalId != Id)
            throw new ArgumentException("Promotion fact must belong to the summarized goal.", nameof(PromotionFact));
        this.PromotionFact = PromotionFact;
    }

    internal GoalSummary(
        string Id,
        string Status,
        string Objective,
        string UpdatedAt,
        string? ResultCommit,
        DateTimeOffset? CreatedAt,
        DateTimeOffset? TerminatedAt,
        string? Condition,
        Func<TerminalGoalMetadataValues>? terminalMetadataLoader)
    {
        this.Id = Id;
        this.Status = Status;
        this.Objective = Objective;
        this.UpdatedAt = UpdatedAt;
        _resultCommit = ResultCommit;
        _createdAt = CreatedAt;
        _terminatedAt = TerminatedAt;
        this.Condition = Condition;
        _terminalMetadata = terminalMetadataLoader is null
            ? null
            : new Lazy<TerminalGoalMetadataValues>(terminalMetadataLoader, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string Id { get; init; }
    public string Status { get; init; }
    public string Objective { get; init; }
    public string UpdatedAt { get; init; }
    public string? ResultCommit => _terminalMetadata?.Value.ResultCommit ?? _resultCommit;
    public DateTimeOffset? CreatedAt => _terminalMetadata?.Value.CreatedAt ?? _createdAt;
    public DateTimeOffset? TerminatedAt => _terminalMetadata?.Value.TerminatedAt ?? _terminatedAt;
    public string? Condition { get; init; }
    public GoalPromotionFact? PromotionFact { get; init; }

    public bool Equals(GoalSummary? other) =>
        other is not null &&
        Id == other.Id &&
        Status == other.Status &&
        Objective == other.Objective &&
        UpdatedAt == other.UpdatedAt &&
        ResultCommit == other.ResultCommit &&
        CreatedAt == other.CreatedAt &&
        TerminatedAt == other.TerminatedAt &&
        Condition == other.Condition &&
        PromotionFact == other.PromotionFact;

    public override int GetHashCode() => HashCode.Combine(HashCode.Combine(
        Id, Status, Objective, UpdatedAt, ResultCommit, CreatedAt, TerminatedAt, Condition), PromotionFact);

    public void Deconstruct(
        out string Id,
        out string Status,
        out string Objective,
        out string UpdatedAt,
        out string? ResultCommit,
        out DateTimeOffset? CreatedAt,
        out DateTimeOffset? TerminatedAt,
        out string? Condition) =>
        (Id, Status, Objective, UpdatedAt, ResultCommit, CreatedAt, TerminatedAt, Condition) =
            (this.Id, this.Status, this.Objective, this.UpdatedAt, this.ResultCommit, this.CreatedAt, this.TerminatedAt, this.Condition);
}

public sealed record QuarantinedGoalSummary(string Id, string Error);
