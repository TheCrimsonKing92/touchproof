using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Operator-owned report artifact. No live orchestrator store, resume, or retention sweep is involved.
internal sealed class ImpactReplayCheckpointStore : IDisposable
{
    internal const string FileName = "impact-replay.checkpoint.db";
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter() } };
    private readonly SqliteConnection _connection;
    private readonly FileStream _writer;
    private readonly string _runId;
    private int _diagnostics;
    internal string Path { get; }

    internal ImpactReplayCheckpointStore(string outputDirectory, ImpactReplayRunIdentity identity,
        ImpactReplayRunProgress startup)
    {
        Directory.CreateDirectory(outputDirectory);
        Path = System.IO.Path.GetFullPath(System.IO.Path.Combine(outputDirectory, FileName));
        // A transient, non-reentrant OS lease also excludes a second invocation on the same thread.
        // DeleteOnClose releases it on process exit; completed evidence lives only in SQLite.
        _writer = new FileStream(Path + ".writer.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite,
            FileShare.Read, 1, FileOptions.DeleteOnClose);
        try
        {
            _runId = identity.RunId;
            _connection = Open(Path, SqliteOpenMode.ReadWriteCreate);
            var version = Version(_connection);
            if (version is not (0 or 1)) throw new InvalidDataException($"Unsupported impact replay checkpoint schema: {version}.");
            // An existing unversioned database is not an invitation to repair or adopt it.
            if (version == 0 && Scalar(_connection, "SELECT count(*) FROM sqlite_master WHERE type='table'") != 0)
                throw new InvalidDataException("Unversioned impact replay checkpoint.");
            Execute(_connection, """
                PRAGMA journal_mode=DELETE;
                PRAGMA synchronous=FULL;
                CREATE TABLE IF NOT EXISTS runs (
                  run_id TEXT PRIMARY KEY, identity_json TEXT NOT NULL, state TEXT NOT NULL,
                  progress_json TEXT NOT NULL, errors_json TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS cases (
                  run_id TEXT NOT NULL, ordinal INTEGER NOT NULL, kind TEXT NOT NULL,
                  attempt_json TEXT NOT NULL, payload_json TEXT NOT NULL,
                  PRIMARY KEY(run_id, ordinal));
                CREATE TABLE IF NOT EXISTS diagnostics (
                  run_id TEXT NOT NULL, ordinal INTEGER NOT NULL, payload_json TEXT NOT NULL,
                  PRIMARY KEY(run_id, ordinal));
                CREATE TABLE IF NOT EXISTS checkouts (
                  run_id TEXT NOT NULL, ordinal INTEGER NOT NULL, path TEXT NOT NULL,
                  cleanup_status TEXT NOT NULL, error TEXT, PRIMARY KEY(run_id, ordinal));
                PRAGMA user_version=1;
                """);
            Execute(_connection, "INSERT INTO runs VALUES ($run,$identity,'Running',$progress,'[]')",
                ("$run", _runId), ("$identity", Serialize(identity)), ("$progress", Serialize(startup)));
        }
        catch
        {
            _connection?.Dispose();
            _writer.Dispose();
            throw;
        }
    }

    internal void Observe(ImpactReplayRunProgress progress) => Observe(progress, null);

    private void Observe(ImpactReplayRunProgress progress, SqliteTransaction? transaction) =>
        ExecuteInTransaction(_connection, transaction, "UPDATE runs SET progress_json=$progress WHERE run_id=$run",
            ("$progress", Serialize(progress)), ("$run", _runId));

    internal void AddDiagnostic(ImpactReplayDiagnostic diagnostic, ImpactReplayRunProgress progress)
    {
        using var transaction = _connection.BeginTransaction();
        ExecuteInTransaction(_connection, transaction, "INSERT INTO diagnostics VALUES ($run,$ordinal,$payload)",
            ("$run", _runId), ("$ordinal", _diagnostics + 1), ("$payload", Serialize(diagnostic)));
        Observe(progress, transaction);
        transaction.Commit();
        _diagnostics++;
    }

    internal void CompleteCase(ImpactReplayStoredAttempt attempt, ImpactReplayCaseResult result, ImpactReplayRunProgress progress)
    {
        using var transaction = _connection.BeginTransaction();
        ExecuteInTransaction(_connection, transaction, "INSERT INTO cases VALUES ($run,$ordinal,$kind,$attempt,$payload)",
            ("$run", _runId), ("$ordinal", progress.CaseOrdinal!), ("$kind", result.Kind.ToString()),
            ("$attempt", Serialize(attempt)), ("$payload", Serialize(result)));
        Observe(progress, transaction);
        transaction.Commit();
    }

    internal void OwnCheckout(int ordinal, string path) =>
        Execute(_connection, "INSERT INTO checkouts VALUES ($run,$ordinal,$path,'pending',NULL)",
            ("$run", _runId), ("$ordinal", ordinal), ("$path", path));

    internal void Cleanup(int ordinal, string path, string status, string? error) =>
        Execute(_connection, "UPDATE checkouts SET cleanup_status=$status,error=$error WHERE run_id=$run AND ordinal=$ordinal AND path=$path",
            ("$run", _runId), ("$ordinal", ordinal), ("$path", path), ("$status", status), ("$error", error));

    internal void Finish(ImpactReplayRunState state, ImpactReplayRunProgress progress, IReadOnlyList<string> errors) =>
        Execute(_connection, "UPDATE runs SET state=$state,progress_json=$progress,errors_json=$errors WHERE run_id=$run",
            ("$run", _runId), ("$state", state.ToString()), ("$progress", Serialize(progress)), ("$errors", Serialize(errors)));

    internal void Reconcile(ImpactReplayReport report, ImpactReplayRunProgress progress)
    {
        var outcomes = ReadRows<ImpactReplayCaseResult>(_connection,
            "SELECT payload_json FROM cases WHERE run_id=$run ORDER BY ordinal", _runId);
        var diagnostics = ReadRows<ImpactReplayDiagnostic>(_connection,
            "SELECT payload_json FROM diagnostics WHERE run_id=$run ORDER BY ordinal", _runId);
        Equal(report.Attempts, outcomes.Where(o => o.Kind == ImpactReplayCaseKind.Replayable).Select(o => o.Record).ToArray());
        Equal(report.SkippedInputs, diagnostics.Concat(outcomes.Where(o => o.Kind == ImpactReplayCaseKind.Skipped).Select(o => o.Diagnostic!)).ToArray());
        Equal(report.UnreplayableAttempts, outcomes.Where(o => o.Kind == ImpactReplayCaseKind.Unreplayable).Select(o => o.Diagnostic).ToArray());
        if (progress.Completed != outcomes.Count || progress.Replayable != report.Attempts.Count ||
            progress.Skipped != report.SkippedInputs.Count || progress.Unreplayable != report.UnreplayableAttempts.Count)
            throw new InvalidDataException("Impact replay checkpoint counts do not reconcile.");
    }

    internal static IReadOnlyList<ImpactReplayCheckpointStatus> ReadStatus(string path)
    {
        // ReadOnly mode refuses missing files and never initializes, upgrades, or executes anything.
        using var connection = Open(System.IO.Path.GetFullPath(path), SqliteOpenMode.ReadOnly);
        var version = Version(connection);
        if (version != 1) throw new InvalidDataException($"Unsupported impact replay checkpoint schema: {version}.");
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT identity_json,state,progress_json,errors_json FROM runs ORDER BY rowid";
        using var reader = command.ExecuteReader();
        var statuses = new List<ImpactReplayCheckpointStatus>();
        while (reader.Read())
        {
            var state = reader.GetString(1);
            statuses.Add(new(Deserialize<ImpactReplayRunIdentity>(reader.GetString(0)), state,
                state is "Running" or "Finalizing" ? "incomplete/unknown" : state.ToLowerInvariant(),
                Deserialize<ImpactReplayRunProgress>(reader.GetString(2)),
                Deserialize<string[]>(reader.GetString(3))));
        }
        reader.Close();
        for (var index = 0; index < statuses.Count; index++)
        {
            using var cleanup = connection.CreateCommand();
            cleanup.CommandText = "SELECT path,cleanup_status,error FROM checkouts WHERE run_id=$run ORDER BY ordinal";
            cleanup.Parameters.AddWithValue("$run", statuses[index].Identity.RunId);
            using var cleanups = cleanup.ExecuteReader();
            var rows = new List<ImpactReplayCheckoutStatus>();
            while (cleanups.Read()) rows.Add(new(cleanups.GetString(0), cleanups.GetString(1),
                cleanups.IsDBNull(2) ? null : cleanups.GetString(2)));
            statuses[index] = statuses[index] with { Checkouts = rows };
        }
        return statuses;
    }

    internal static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
    private static T Deserialize<T>(string value) => JsonSerializer.Deserialize<T>(value, Json)
        ?? throw new InvalidDataException("Null impact replay checkpoint payload.");

    private static void Equal<T, TOther>(T expected, TOther actual)
    {
        if (Serialize(expected) != Serialize(actual))
            throw new InvalidDataException("Impact replay checkpoint outcomes do not reconcile.");
    }

    private static List<T> ReadRows<T>(SqliteConnection connection, string sql, string run)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$run", run);
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read()) rows.Add(Deserialize<T>(reader.GetString(0)));
        return rows;
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = mode, Pooling = false, DefaultTimeout = 5 }.ToString());
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }

    private static long Version(SqliteConnection connection) => Scalar(connection, "PRAGMA user_version");
    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    private static void Execute(SqliteConnection connection, string sql, params (string Name, object? Value)[] values)
        => ExecuteInTransaction(connection, null, sql, values);

    private static void ExecuteInTransaction(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, params (string Name, object? Value)[] values)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _connection.Dispose();
        _writer.Dispose();
    }
}

internal sealed record ImpactReplayCheckpointStatus(ImpactReplayRunIdentity Identity, string StoredState,
    string Completion, ImpactReplayRunProgress Progress, IReadOnlyList<string> Errors)
{
    public IReadOnlyList<ImpactReplayCheckoutStatus> Checkouts { get; init; } = [];
}

internal sealed record ImpactReplayCheckoutStatus(string Path, string CleanupStatus, string? Error);
