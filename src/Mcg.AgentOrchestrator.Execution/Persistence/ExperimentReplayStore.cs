using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Immutable source observations/registrations/executions; only run lifecycle is mutable.
public sealed class ExperimentReplayStore
{
    public const string DatabaseName = "experiment-replay.db";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _path;
    private readonly bool _readOnly;

    public ExperimentReplayStore(string path, bool readOnly = false)
    {
        _path = Path.GetFullPath(path);
        _readOnly = readOnly;
        using var connection = Open();
        if (StoreSchemaVersions.Verify(connection, StoreSchemaRegistry.ExperimentReplay) != StoreSchemaState.Current)
            throw new InvalidOperationException("Experiment replay store requires explicit setup with a compatible runtime.");
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path, Mode = _readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Pooling = false, DefaultTimeout = 30
        }.ToString());
        connection.Open();
        if (_readOnly) Execute(connection, "PRAGMA query_only=ON");
        return connection;
    }

    public static void Setup(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        var state = StoreSchemaVersions.Verify(connection, StoreSchemaRegistry.ExperimentReplay);
        if (state == StoreSchemaState.Newer) throw new InvalidOperationException("Replay schema is newer than this runtime.");
        if (state == StoreSchemaState.Current) return;
        Execute(connection, "BEGIN IMMEDIATE");
        try
        {
            Execute(connection, """
            CREATE TABLE IF NOT EXISTS replay_cases(id TEXT PRIMARY KEY, completed_at TEXT NOT NULL, project TEXT NOT NULL, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS replay_registrations(id TEXT PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS replay_runs(id TEXT PRIMARY KEY, registration_id TEXT NOT NULL UNIQUE, state TEXT NOT NULL, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS replay_executions(id TEXT PRIMARY KEY, run_id TEXT NOT NULL, ordinal INTEGER NOT NULL, json TEXT NOT NULL, UNIQUE(run_id,ordinal));
            CREATE INDEX IF NOT EXISTS replay_cases_inventory ON replay_cases(project,completed_at DESC,id);
            CREATE INDEX IF NOT EXISTS replay_executions_run ON replay_executions(run_id,ordinal);
            CREATE TABLE IF NOT EXISTS replay_faults(id TEXT PRIMARY KEY, run_id TEXT NOT NULL, case_id TEXT NOT NULL, shape TEXT NOT NULL, json TEXT NOT NULL, UNIQUE(run_id,shape));
            CREATE INDEX IF NOT EXISTS replay_faults_run ON replay_faults(run_id);
            """);
            StoreSchemaVersions.UpgradeToCurrent(connection, StoreSchemaRegistry.ExperimentReplay);
            Execute(connection, "COMMIT");
        }
        catch { Execute(connection, "ROLLBACK"); throw; }
    }

    public bool Import(ExperimentReplayCase value)
    {
        if (_readOnly) throw new InvalidOperationException("Read-only replay store.");
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT json FROM replay_cases WHERE id=$id";
        command.Parameters.AddWithValue("$id", value.Id);
        var json = JsonSerializer.Serialize(value, Json);
        if (command.ExecuteScalar() is string previous)
        {
            if (previous == json) return false;
            var old = JsonSerializer.Deserialize<ExperimentReplayCase>(previous, Json)!;
            var oldCore = old with { Gates = [], Reasons = [], Replayability = "" };
            var newCore = value with { Gates = [], Reasons = [], Replayability = "" };
            if (JsonSerializer.Serialize(oldCore, Json) != JsonSerializer.Serialize(newCore, Json) ||
                old.Gates.Any(g => !value.Gates.Any(n => n.AttemptId == g.AttemptId && JsonSerializer.Serialize(n, Json) == JsonSerializer.Serialize(g, Json))))
                throw new InvalidDataException($"Conflicting replay provenance: {value.Id}.");
            command.CommandText = "UPDATE replay_cases SET json=$json WHERE id=$id";
        }
        else
        {
            command.CommandText = "INSERT INTO replay_cases(id,completed_at,project,json) VALUES($id,$completed,$project,$json)";
            command.Parameters.AddWithValue("$completed", value.CompletedAt.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$project", value.Project);
        }
        command.Parameters.AddWithValue("$json", json);
        command.ExecuteNonQuery();
        transaction.Commit();
        return true;
    }

    public void Register(ExperimentReplayRegistration value)
    {
        if (value.RequiredCases != 20 || value.Repetitions != 3 || value.Warmups != 2 || value.CalibrationPairs != 10 ||
            value.BootstrapResamples != 10000 || value.Cases.Count != 20 || value.Cases.Select(c => c.Id).Distinct().Count() != 20 ||
            value.Cases.Any(c => c.Project != value.Project || c.Replayability != "replayable" ||
                c.CompletedAt > value.RegisteredAt || c.CompletedAt < value.RegisteredAt.AddDays(-30)) ||
            string.IsNullOrWhiteSpace(value.RuntimeIdentity) || string.IsNullOrWhiteSpace(value.ToolchainIdentity) ||
            string.IsNullOrWhiteSpace(value.ConfigurationIdentity) || string.IsNullOrWhiteSpace(value.ValueBoundEvidence.Sha256))
            throw new InvalidDataException("Registration requires the fixed pilot, pinned identities and 20 eligible cases.");
        Insert("replay_registrations", value.Id, value);
    }

    public void Start(ExperimentReplayRun run)
    {
        if (run.State != "running" || run.CompletedAt is not null) throw new InvalidOperationException("Invalid run start.");
        _ = Registration(run.RegistrationId);
        Insert("replay_runs", run.Id, run, ",registration_id,state", ",$registration,$state",
            ("$registration", run.RegistrationId), ("$state", run.State));
    }

    public void Finish(ExperimentReplayRun run)
    {
        if (_readOnly) throw new InvalidOperationException("Read-only replay store.");
        if (run.State is not ("complete" or "inconclusive" or "cancelled" or "apparatus-failed" or "incomplete") || run.CompletedAt is null)
            throw new InvalidOperationException("Invalid terminal replay state.");
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE replay_runs SET state=$state,json=$json WHERE id=$id AND registration_id=$registration AND state='running'";
        command.Parameters.AddWithValue("$id", run.Id);
        command.Parameters.AddWithValue("$registration", run.RegistrationId);
        command.Parameters.AddWithValue("$state", run.State);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(run, Json));
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Replay run is absent or already terminal.");
    }

    public void Record(ExperimentReplayExecution execution)
    {
        if (_readOnly) throw new InvalidOperationException("Read-only replay store.");
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO replay_executions(id,run_id,ordinal,json)
            SELECT $id,$run,$ordinal,$json WHERE EXISTS(SELECT 1 FROM replay_runs WHERE id=$run AND state='running')
            """;
        command.Parameters.AddWithValue("$id", execution.Id);
        command.Parameters.AddWithValue("$run", execution.RunId);
        command.Parameters.AddWithValue("$ordinal", execution.Slot.Ordinal);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(execution, Json));
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Cannot append measurements to an absent or terminal run.");
    }

    public IReadOnlyList<ExperimentReplayCase> Cases(string project, DateTimeOffset cutoff) =>
        Read<ExperimentReplayCase>("SELECT json FROM replay_cases WHERE project=$project AND completed_at<=$cutoff AND completed_at>=$since ORDER BY completed_at DESC,id LIMIT 100",
            ("$project", project), ("$cutoff", cutoff.ToUniversalTime().ToString("O")), ("$since", cutoff.AddDays(-30).ToUniversalTime().ToString("O")));
    public ExperimentReplayRegistration Registration(string id) => Read<ExperimentReplayRegistration>("SELECT json FROM replay_registrations WHERE id=$id", ("$id", id)).Single();
    public ExperimentReplayRun Run(string id) => Read<ExperimentReplayRun>("SELECT json FROM replay_runs WHERE id=$id", ("$id", id)).Single();
    public IReadOnlyList<ExperimentReplayExecution> Executions(string runId) =>
        Read<ExperimentReplayExecution>("SELECT json FROM replay_executions WHERE run_id=$id ORDER BY ordinal", ("$id", runId));
    public void RecordFault(ExperimentReplayFault fault) => Insert("replay_faults", fault.Id, fault,
        ",run_id,case_id,shape", ",$run,$case,$shape", ("$run", fault.RunId), ("$case", fault.CaseId), ("$shape", fault.Shape));
    public IReadOnlyList<ExperimentReplayFault> Faults(string runId) =>
        Read<ExperimentReplayFault>("SELECT json FROM replay_faults WHERE run_id=$id ORDER BY shape", ("$id", runId));

    private bool Insert<T>(string table, string id, T value, string columns = "", string values = "", params (string, object)[] parameters)
    {
        if (_readOnly) throw new InvalidOperationException("Read-only replay store.");
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var json = JsonSerializer.Serialize(value, Json);
        command.CommandText = $"SELECT json FROM {table} WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        if (command.ExecuteScalar() is string existing)
        {
            if (existing != json) throw new InvalidDataException($"Conflicting immutable replay identity: {id}.");
            return false;
        }
        command.CommandText = $"INSERT INTO {table}(id,json{columns}) VALUES($id,$json{values})";
        command.Parameters.AddWithValue("$json", json);
        foreach (var (name, item) in parameters) command.Parameters.AddWithValue(name, item);
        command.ExecuteNonQuery();
        transaction.Commit();
        return true;
    }

    private IReadOnlyList<T> Read<T>(string sql, params (string, object)[] parameters)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        using var reader = command.ExecuteReader();
        var results = new List<T>();
        while (reader.Read()) results.Add(JsonSerializer.Deserialize<T>(reader.GetString(0), Json) ?? throw new InvalidDataException("Null replay record."));
        return results;
    }

    private static void Execute(SqliteConnection connection, string sql)
    { using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static ExperimentReplaySource Source(string path) => new(Path.GetFullPath(path),
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
}
