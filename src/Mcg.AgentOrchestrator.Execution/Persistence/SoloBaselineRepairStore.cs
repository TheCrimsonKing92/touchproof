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
    internal bool MatchesAttributionPair(string candidate, string main) => Candidate == candidate && Main == main && Baseline == main;
    internal bool HasConflictingProofs => Reason == SoloBaselineRepairStore.ConflictingProofs;
}

internal sealed record SoloRepairGroup(string Key, string IntakePayload, string? RepairGoal);
internal sealed record SoloRepairCheckReceipt(string Outcome, string Detail, IReadOnlyList<SoloAcceptanceArtifact> Artifacts,
    IReadOnlyList<SoloAcceptanceArtifact>? CandidateArtifacts = null, string? Feedback = null,
    SoloAcceptanceArtifact? Proof = null);
internal sealed record SoloRepairVerifierResult(string Identity, SoloRepairCheckReceipt Receipt);

internal sealed class SoloBaselineRepairStore
{
    internal const string FileName = "solo-baseline-repair.db";
    internal const string ConflictingProofs = "Conflicting verifier proofs for the same revision pair and exact test.";

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
            CREATE TABLE IF NOT EXISTS solo_repair_check_execution (
                request_key TEXT PRIMARY KEY, result TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS solo_repair_conflicting_proof (
                goal TEXT NOT NULL, identity_key TEXT NOT NULL, proof TEXT NOT NULL,
                PRIMARY KEY (goal, identity_key, proof));
            """);
        command.ExecuteNonQuery();
    }

    internal static string Key(params string?[] fields) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(fields))).ToLowerInvariant();

    internal void Retain(string goal, IReadOnlyList<SoloRepairFinding> findings)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();
        foreach (var incoming in findings)
        {
            // Independently proven rows supersede missing evidence for the same revision pair.
            var finding = incoming;
            var rank = finding.Inherited ? 3 : finding.Reason is null ? 2 : finding.Request is not null ? 1 : 0;
            string? disputedGroup = null;
            var prior = rank is 2 or 3 ? ReadFinding(db, transaction, goal, finding.Key) : null;
            if (prior is not null && prior.Baseline == finding.Baseline)
            {
                if (prior.HasConflictingProofs)
                {
                    RecordConflictingProof(db, transaction, goal, finding);
                    continue;
                }
                if (prior.Reason is null && prior.Inherited != finding.Inherited)
                {
                    RecordConflictingProof(db, transaction, goal, prior);
                    RecordConflictingProof(db, transaction, goal, finding);
                    if (prior.Inherited) disputedGroup = prior.GroupKey;
                    finding = prior with { Inherited = false, Reason = ConflictingProofs, Feedback = null };
                    rank = 4;
                }
            }
            var preferExactMain = rank == 2 && prior is { Inherited: false, Reason: null } &&
                prior.Baseline != finding.Baseline && finding.Baseline == finding.Main;
            using var command = Command(db, """
                INSERT INTO solo_repair_finding VALUES ($goal, $key, $rank, $finding)
                ON CONFLICT (goal, identity_key) DO UPDATE SET rank = excluded.rank, finding = excluded.finding
                WHERE excluded.rank > solo_repair_finding.rank OR $preferExactMain
                """, ("$goal", goal), ("$key", finding.Key), ("$rank", rank),
                ("$finding", JsonSerializer.Serialize(finding)), ("$preferExactMain", preferExactMain));
            command.Transaction = transaction;
            var changed = command.ExecuteNonQuery() != 0;
            if (disputedGroup is not null) RemoveConflictingDependency(db, transaction, goal, disputedGroup);
            if (!finding.Inherited) continue;
            // The link is an acceptance obligation. Goal-wide dependencies would
            // also block Developer dispatch for introduced failures in this partition.
            using var link = Command(db, """
                INSERT OR IGNORE INTO solo_repair_dependency SELECT $goal, $group
                WHERE $goal <> COALESCE((SELECT repair_goal FROM solo_repair_group WHERE group_key = $group), '')
                """,
                ("$goal", goal), ("$group", finding.GroupKey));
            link.Transaction = transaction;
            if (changed) link.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    internal bool HasConflictingProofs(string goal, string key)
    {
        using var db = Open();
        return ReadFinding(db, null, goal, key)?.HasConflictingProofs == true;
    }

    private static SoloRepairFinding? ReadFinding(SqliteConnection db, SqliteTransaction? transaction, string goal, string key)
    {
        using var command = Command(db, "SELECT finding FROM solo_repair_finding WHERE goal = $goal AND identity_key = $key",
            ("$goal", goal), ("$key", key));
        command.Transaction = transaction;
        return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<SoloRepairFinding>(json) : null;
    }

    private static void RecordConflictingProof(SqliteConnection db, SqliteTransaction transaction, string goal,
        SoloRepairFinding finding)
    {
        using var command = Command(db, "INSERT OR IGNORE INTO solo_repair_conflicting_proof VALUES ($goal, $key, $proof)",
            ("$goal", goal), ("$key", finding.Key), ("$proof", JsonSerializer.Serialize(finding)));
        command.Transaction = transaction;
        command.ExecuteNonQuery();
    }

    private static void RemoveConflictingDependency(SqliteConnection db, SqliteTransaction transaction, string goal,
        string group)
    {
        using (var remaining = Command(db, "SELECT finding FROM solo_repair_finding WHERE goal = $goal AND rank = 3",
                   ("$goal", goal)))
        {
            remaining.Transaction = transaction;
            using var rows = remaining.ExecuteReader();
            while (rows.Read())
                if (JsonSerializer.Deserialize<SoloRepairFinding>(rows.GetString(0))?.GroupKey == group) return;
        }
        using var delete = Command(db, "DELETE FROM solo_repair_dependency WHERE goal = $goal AND group_key = $group",
            ("$goal", goal), ("$group", group));
        delete.Transaction = transaction;
        delete.ExecuteNonQuery();
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
        using var transaction = db.BeginTransaction();
        using var command = Command(db, """
            UPDATE solo_repair_group SET repair_goal = $goal
            WHERE group_key = $key AND (repair_goal IS NULL OR repair_goal = $goal)
            """, ("$key", key), ("$goal", goal));
        command.Transaction = transaction;
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException($"Conflicting repair goal for {key}.");
        using var clear = Command(db, "DELETE FROM solo_repair_dependency WHERE goal = $goal AND group_key = $key",
            ("$goal", goal), ("$key", key));
        clear.Transaction = transaction;
        clear.ExecuteNonQuery();
        transaction.Commit();
    }

    internal bool TryClaimCheck(string key)
    {
        using var db = Open();
        using var command = Command(db, "INSERT OR IGNORE INTO solo_repair_check VALUES ($key, NULL)", ("$key", key));
        return command.ExecuteNonQuery() == 1;
    }

    internal void RemoveSelfDependency(string group, string goal)
    {
        using var db = Open();
        using var command = Command(db, """
            DELETE FROM solo_repair_dependency WHERE goal = $goal AND group_key = $group
                AND EXISTS (SELECT 1 FROM solo_repair_group WHERE group_key = $group AND repair_goal = $goal)
            """, ("$goal", goal), ("$group", group));
        command.ExecuteNonQuery();
    }

    internal void CompleteCheck(string key, SoloRepairCheckReceipt result)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();
        using var command = Command(db, """
            UPDATE solo_repair_check SET result = $result WHERE request_key = $key AND result IS NULL
            """, ("$key", key), ("$result", JsonSerializer.Serialize(result)));
        command.Transaction = transaction;
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException($"Check claim already completed for {key}.");
        using var clear = Command(db, "DELETE FROM solo_repair_check_execution WHERE request_key = $key", ("$key", key));
        clear.Transaction = transaction;
        clear.ExecuteNonQuery();
        transaction.Commit();
    }

    internal void RecordCheckExecution(string key, SoloRepairCheckReceipt result)
    {
        using var db = Open();
        using var command = Command(db, "INSERT INTO solo_repair_check_execution VALUES ($key, $result)",
            ("$key", key), ("$result", JsonSerializer.Serialize(result)));
        command.ExecuteNonQuery();
    }

    internal SoloRepairCheckReceipt? ReadCheckExecution(string key)
    {
        using var db = Open();
        using var command = Command(db, "SELECT result FROM solo_repair_check_execution WHERE request_key = $key", ("$key", key));
        return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<SoloRepairCheckReceipt>(json) : null;
    }

    internal SoloRepairCheckReceipt? ReadCheck(string key)
    {
        using var db = Open();
        using var command = Command(db, "SELECT result FROM solo_repair_check WHERE request_key = $key", ("$key", key));
        return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<SoloRepairCheckReceipt>(json) : null;
    }

    internal static string NormalGateKey(string goal, string candidate, string main) => Key("normal-gate", goal, candidate, main);
    internal static string AttributionKey(string goal, SoloRepairFinding finding, string candidate, string main)
    {
        var moved = finding.Candidate != candidate || finding.Main != main;
        return Key(moved ? "attribution-current-pair" : "attribution",
            goal, finding.Check, finding.Identity, candidate, main);
    }
    internal static string ValidationKey(SoloRepairFinding finding, string main) => Key("validation", finding.GroupKey, main);

    internal static bool HasExactReplacement(SoloRepairFinding finding, IReadOnlyList<SoloRepairFinding> findings,
        string candidate, string main) =>
        findings.Any(other => other.Check == finding.Check &&
            (finding.Identity.Length == 0 && other.Identity.Length > 0 &&
                other.Candidate == finding.Candidate && other.Main == finding.Main ||
             finding.Identity.Length > 0 && other.Key != finding.Key && other.Inherited &&
                other.Identity == finding.Identity && other.Request == finding.Request &&
                other.MatchesAttributionPair(candidate, main)));

    internal bool IsEvidenceCurrent(string goal, IReadOnlyList<SoloRepairFinding> findings, string? candidate, string? main)
    {
        if (findings.Count == 0) return true;
        if (candidate is null || main is null) return false;
        return findings.All(finding => finding.Inherited
            ? ReadGroup(finding.GroupKey)?.RepairGoal is { } repair && repair != goal &&
                IsVerifiedPass(ValidationKey(finding, main), finding, main, main, attribution: false)
            : HasExactReplacement(finding, findings, candidate, main) ||
                ((finding.Candidate != candidate || finding.Main != main) &&
                 IsVerifiedPass(AttributionKey(goal, finding, candidate, main), finding, candidate, main, attribution: true)));
    }

    private bool IsVerifiedPass(string key, SoloRepairFinding finding, string checkout, string main, bool attribution) =>
        ReadVerifiedCheck(key, FocusedProofIdentity(key, finding, checkout, main, attribution, attribution))?.Outcome == "passed";

    internal string ResultDirectory(string key) => Path.Combine(Path.GetDirectoryName(_path)!, "solo-repair-results", key);

    internal static string FocusedProofIdentity(string key, SoloRepairFinding finding, string checkout, string main,
        bool runBaseline, bool allowCandidatePass) => Key("focused-repair-result-v1", key, checkout, main,
            runBaseline.ToString(), allowCandidatePass.ToString(), finding.Request, finding.Check, finding.Identity,
            finding.Project, finding.BaselineCheck);

    internal SoloRepairCheckReceipt? ReadVerifiedCheck(string key, string identity)
    {
        var stored = ReadCheck(key);
        if (stored is null || stored.Outcome == "unresolved") return stored;
        var root = ResultDirectory(key);
        if (stored.Proof is { } proof && proof.Path == Path.Combine(root, "focused-result.json"))
        {
            try
            {
                var verified = ReadVerifierResult(proof.Path, root, identity, proof.Sha256);
                if (verified.Outcome == stored.Outcome && verified.Artifacts.Count > 0)
                    return verified;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                return new("unresolved", ex.Message, []);
            }
        }
        return new("unresolved", "The completed check is missing matching focused verifier evidence.", []);
    }

    internal static SoloRepairCheckReceipt ReadVerifierResult(string path, string root, string identity, string? expectedDigest = null)
    {
        var bytes = File.ReadAllBytes(path);
        var digest = Digest(bytes);
        var result = JsonSerializer.Deserialize<SoloRepairVerifierResult>(bytes);
        if (result?.Identity != identity || result.Receipt?.Artifacts is null ||
            (expectedDigest is not null && expectedDigest != digest))
            throw new InvalidDataException("The focused verifier result does not match its repair request.");
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var artifact in result.Receipt.Artifacts.Concat(result.Receipt.CandidateArtifacts ?? []))
        {
            if (artifact is null || !Path.GetFullPath(artifact.Path).StartsWith(prefix,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidDataException("solo-foreign-artifact");
            if (Digest(File.ReadAllBytes(artifact.Path)) != artifact.Sha256)
                throw new InvalidDataException("solo-artifact-digest-mismatch");
        }
        return result.Receipt with { Proof = new(path, digest) };
    }

    internal static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

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
