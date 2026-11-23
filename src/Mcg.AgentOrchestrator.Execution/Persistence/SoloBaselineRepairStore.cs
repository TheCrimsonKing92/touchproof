using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record SoloRepairFinding(string Candidate, string Main, string Baseline, string Check,
    string Identity, string? Project, string? Request, string? BaselineCheck, bool Inherited,
    string Attempt, SoloAcceptanceArtifact Proof, IReadOnlyList<SoloAcceptanceArtifact> CandidateArtifacts,
    IReadOnlyList<SoloAcceptanceArtifact> BaselineArtifacts, string? Reason, string? Feedback = null)
{
    internal string GroupKey => SoloBaselineRepairStore.Key(Baseline.ToLowerInvariant(), Check, Project,
        Request, BaselineCheck, Identity);
    internal string Key => SoloBaselineRepairStore.Key(Candidate, Main, Check, Identity);
}

internal sealed record SoloRepairGroup(string Key, string IntakePayload, string? RepairGoal);
internal sealed record SoloRepairCheckReceipt(string Outcome, string Detail, IReadOnlyList<SoloAcceptanceArtifact> Artifacts);

internal sealed class SoloBaselineRepairStore
{
    internal const string FileName = "solo-baseline-repair.db";

    private readonly string _path;

    internal SoloBaselineRepairStore(string path)
    {
        _path = path;
        using var db = Open();
        using var command = Command(db, """
            CREATE TABLE IF NOT EXISTS solo_repair_finding (
                goal TEXT NOT NULL, identity_key TEXT NOT NULL, rank INTEGER NOT NULL, finding TEXT NOT NULL,
                PRIMARY KEY (goal, identity_key));
            CREATE TABLE IF NOT EXISTS solo_repair_group (
                group_key TEXT PRIMARY KEY, intake_payload TEXT NOT NULL, repair_goal TEXT);
            CREATE TABLE IF NOT EXISTS solo_repair_dependency (
                goal TEXT NOT NULL, group_key TEXT NOT NULL, PRIMARY KEY (goal, group_key));
            CREATE TABLE IF NOT EXISTS solo_repair_check (
                request_key TEXT PRIMARY KEY, result TEXT);
            """);
        command.ExecuteNonQuery();
    }

    internal static string Key(params string?[] fields) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(fields))).ToLowerInvariant();

    internal void Retain(string goal, IReadOnlyList<SoloRepairFinding> findings)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();
        foreach (var finding in findings)
        {
            // Independently proven rows supersede missing evidence for the same revision pair.
            var rank = finding.Inherited ? 3 : finding.Reason is null ? 2 : finding.Request is not null ? 1 : 0;
            using var command = Command(db, """
                INSERT INTO solo_repair_finding VALUES ($goal, $key, $rank, $finding)
                ON CONFLICT (goal, identity_key) DO UPDATE SET rank = excluded.rank, finding = excluded.finding
                WHERE excluded.rank > solo_repair_finding.rank
                """, ("$goal", goal), ("$key", finding.Key), ("$rank", rank),
                ("$finding", JsonSerializer.Serialize(finding)));
            command.Transaction = transaction;
            command.ExecuteNonQuery();
            if (!finding.Inherited) continue;
            // The link is an acceptance obligation. Goal-wide dependencies would
            // also block Developer dispatch for introduced failures in this partition.
            using var link = Command(db, "INSERT OR IGNORE INTO solo_repair_dependency VALUES ($goal, $group)",
                ("$goal", goal), ("$group", finding.GroupKey));
            link.Transaction = transaction;
            link.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    internal IReadOnlyList<SoloRepairFinding> ReadFindings(string goal, bool introduced = false)
    {
        using var db = Open();
        const string columns = "SELECT finding FROM solo_repair_finding WHERE goal = $goal ";
        var match = introduced ? "AND rank = 2" : "AND rank <> 2";
        using var command = Command(db, $"{columns}{match} ORDER BY identity_key", ("$goal", goal));
        using var rows = command.ExecuteReader();
        var results = new List<SoloRepairFinding>();
        while (rows.Read()) results.Add(JsonSerializer.Deserialize<SoloRepairFinding>(rows.GetString(0))!);
        return results;
    }

    internal SoloRepairGroup? ReadGroup(string key)
    {
        using var db = Open();
        using var command = Command(db, """
            SELECT intake_payload, repair_goal FROM solo_repair_group WHERE group_key = $key
            """, ("$key", key));
        using var row = command.ExecuteReader();
        return row.Read() ? new(key, row.GetString(0), row.IsDBNull(1) ? null : row.GetString(1)) : null;
    }

    internal SoloRepairGroup ReserveGroup(string key, string intakePayload)
    {
        using var db = Open();
        using var command = Command(db, """
            INSERT OR IGNORE INTO solo_repair_group (group_key, intake_payload) VALUES ($key, $payload)
            """, ("$key", key), ("$payload", intakePayload));
        command.ExecuteNonQuery();
        return ReadGroup(key)!;
    }

    internal void AcknowledgeRepair(string key, string goal)
    {
        using var db = Open();
        using var command = Command(db, """
            UPDATE solo_repair_group SET repair_goal = $goal
            WHERE group_key = $key AND (repair_goal IS NULL OR repair_goal = $goal)
            """, ("$key", key), ("$goal", goal));
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException($"Conflicting repair goal for {key}.");
    }

    internal bool TryClaimCheck(string key)
    {
        using var db = Open();
        using var command = Command(db, "INSERT OR IGNORE INTO solo_repair_check VALUES ($key, NULL)", ("$key", key));
        return command.ExecuteNonQuery() == 1;
    }

    internal void CompleteCheck(string key, SoloRepairCheckReceipt result)
    {
        using var db = Open();
        using var command = Command(db, """
            UPDATE solo_repair_check SET result = $result WHERE request_key = $key AND result IS NULL
            """, ("$key", key), ("$result", JsonSerializer.Serialize(result)));
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException($"Check claim already completed for {key}.");
    }

    internal SoloRepairCheckReceipt? ReadCheck(string key)
    {
        using var db = Open();
        using var command = Command(db, "SELECT result FROM solo_repair_check WHERE request_key = $key", ("$key", key));
        return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<SoloRepairCheckReceipt>(json) : null;
    }

    internal static string NormalGateKey(string goal, string candidate, string main) => Key("normal-gate", goal, candidate, main);
    internal static string AttributionKey(string goal, SoloRepairFinding finding, string candidate, string main) =>
        Key("attribution", goal, finding.Check, finding.Identity, candidate, main);
    internal static string ValidationKey(SoloRepairFinding finding, string main) => Key("validation", finding.GroupKey, main);

    internal static bool HasExactReplacement(SoloRepairFinding finding, IReadOnlyList<SoloRepairFinding> findings) =>
        finding.Identity.Length == 0 && findings.Any(other => other.Check == finding.Check && other.Identity.Length > 0 &&
            other.Candidate == finding.Candidate && other.Main == finding.Main);

    internal bool IsEvidenceCurrent(string goal, IReadOnlyList<SoloRepairFinding> findings, string? candidate, string? main)
    {
        if (findings.Count == 0) return true;
        if (candidate is null || main is null) return false;
        return findings.All(finding => finding.Inherited
            ? ReadGroup(finding.GroupKey)?.RepairGoal is not null && ReadCheck(ValidationKey(finding, main))?.Outcome == "passed"
            : HasExactReplacement(finding, findings) || ReadCheck(AttributionKey(goal, finding, candidate, main))?.Outcome == "passed");
    }

    internal void AcknowledgeEffect(string key, string detail)
    {
        using var db = Open();
        using var command = Command(db, """
            INSERT OR IGNORE INTO solo_repair_check VALUES ($key, $result)
            """, ("$key", key), ("$result", JsonSerializer.Serialize(new SoloRepairCheckReceipt("passed", detail, []))));
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open() => SqliteStoreConnectionPolicy.Open(_path, readOnly: false,
        SqliteStoreDurability.Strict, foreignKeys: false, operationTag: nameof(SoloBaselineRepairStore));

    private static SqliteCommand Command(SqliteConnection db, string sql, params (string Name, object Value)[] parameters)
    {
        var command = db.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command;
    }
}
