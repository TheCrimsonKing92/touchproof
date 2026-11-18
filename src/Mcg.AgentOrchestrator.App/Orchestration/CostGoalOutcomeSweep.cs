using System.Runtime.CompilerServices;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class CostGoalOutcomeSweep(
    string orchestratorDirectory,
    CostGoalOutcomeStore store,
    Func<(BacklogStore Backlog, ConductEventLogWriter Events)> openStores,
    Func<GoalId, string?> resolveLandedHomeCommit,
    Func<IEnumerable<(string Commit, DateTimeOffset At)>> readAdoptions,
    Func<string, string, bool> containsCommit,
    Action<string> reportFailure)
{
    private static readonly ConditionalWeakTable<ConductorBatchLoop, CostGoalOutcomeSweep> TickSweeps = new();
    private readonly object runGate = new();
    private readonly Dictionary<GoalId, string> reportedFailures = new();
    private (BacklogStore Backlog, ConductEventLogWriter Events)? stores;

    internal static void ObserveTick(ConductorBatchLoop loop, OrchestratorWorkspace? workspace,
        Func<DateTimeOffset> utcNow, ConductEventLogWriter? events)
    {
        if (workspace is null) return;

        void ReportFailure(string detail) => (events ??= new ConductEventLogWriter(workspace.ConductEventsLogPath))
            .Append("cost-goal-outcome-sweep", null, detail);

        try
        {
            TickSweeps.GetValue(loop, _ => CreateDefault(workspace, ReportFailure)).Run(utcNow);
        }
        catch (Exception ex)
        {
            AdoptedBuildPublicationReporting.Try(
                () => ReportFailure($"COST_GOAL_OUTCOME_SWEEP_FAILED source=tick reason={ex.GetType().Name}"),
                "cost-outcome-tick-sweep");
        }
    }

    internal void Run(Func<DateTimeOffset> utcNow)
    {
        lock (runGate)
        {
            var evaluatedAt = utcNow();
            var adoptions = new Lazy<(string Commit, DateTimeOffset At)[]>(() => readAdoptions()
                .Where(adoption => adoption.At <= evaluatedAt).Distinct().OrderBy(adoption => adoption.At).ToArray());
            var gates = new Lazy<OutcomeGateObservation[]>(() =>
                CostGoalOutcomeMetric.ReadRecordedGates(orchestratorDirectory, evaluatedAt).ToArray());
            var ancestry = new Dictionary<(string Ancestor, string Commit), bool>();

            bool Contains(string ancestor, string commit)
            {
                if (string.Equals(ancestor, commit, StringComparison.OrdinalIgnoreCase)) return true;
                var pair = (ancestor, commit);
                if (!ancestry.TryGetValue(pair, out var contained))
                    ancestry[pair] = contained = containsCommit(ancestor, commit);
                return contained;
            }

            foreach (var goalId in ReadFiledGoalIds())
            {
                try
                {
                    store.WithGoalLock(goalId, () => SweepGoal(goalId, evaluatedAt, adoptions, gates, Contains));
                    reportedFailures.Remove(goalId);
                }
                catch (Exception ex)
                {
                    var failure = $"{ex.GetType().Name}: {ex.Message}";
                    if (reportedFailures.GetValueOrDefault(goalId) != failure && AdoptedBuildPublicationReporting.Try(
                            () => reportFailure($"COST_GOAL_OUTCOME_SWEEP_FAILED goal={goalId} reason={ex.GetType().Name}"),
                            "cost-outcome-goal-sweep"))
                        reportedFailures[goalId] = failure;
                }
            }
        }
    }

    private void SweepGoal(GoalId goalId, DateTimeOffset evaluatedAt,
        Lazy<(string Commit, DateTimeOffset At)[]> adoptions, Lazy<OutcomeGateObservation[]> gates,
        Func<string, string, bool> contains)
    {
        var record = store.Read(goalId);
        if (record is null) return;
        var allFinal = record.FinalVerdicts.Count == record.Filing.Baselines.Count;
        if (allFinal && (record.Adoption is null ||
            record.FinalVerdicts.All(result => record.EmittedLines.Contains(result.LineIndex)) &&
            (record.BacklogNote is null || record.BacklogApplied))) return;

        var adoption = record.Adoption;
        if (adoption is null)
        {
            var landed = resolveLandedHomeCommit(goalId);
            if (string.IsNullOrWhiteSpace(landed)) return;
            var first = adoptions.Value.FirstOrDefault(candidate => contains(landed, candidate.Commit));
            if (first.Commit is null) return;
            adoption = new(landed, first.Commit, first.At);
            store.Write(goalId, record = record with { Adoption = adoption });
        }

        // A previous pass can have stored finals before a delivery failed. Resume those effects without reading gates.
        var results = allFinal ? record.FinalVerdicts.OrderBy(result => result.LineIndex).ToArray()
            : CostGoalOutcomeVerdict.Evaluate(record, gates.Value, adoption.LandedHomeCommit, contains,
                adoption.AdoptedAt, evaluatedAt);
        var (backlog, events) = stores ??= openStores();
        CostGoalOutcomeVerdict.Apply(goalId, results, store, backlog, events);
    }

    private IEnumerable<GoalId> ReadFiledGoalIds()
    {
        var directory = Path.Combine(orchestratorDirectory, "cost-goal-outcomes");
        string[] paths;
        try { paths = Directory.GetFiles(directory, "*.json"); }
        catch (DirectoryNotFoundException) { yield break; }
        foreach (var path in paths)
            if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id))
                yield return new GoalId(id.ToString("N"));
    }

    internal static CostGoalOutcomeSweep CreateDefault(OrchestratorWorkspace workspace, Action<string> reportFailure)
    {
        var directory = workspace.OrchestratorDirectory;
        var landings = new MultiRepositoryLandingRecordStore(directory);
        return new(directory, new CostGoalOutcomeStore(directory),
            () => (new BacklogStore(workspace.BacklogStorePath), new ConductEventLogWriter(workspace.ConductEventsLogPath)),
            goalId =>
            {
                if (landings.ReadLanding(goalId)?.LandedCommits.GetValueOrDefault("default") is { } homeCommit)
                    return homeCommit;
                if (File.Exists(workspace.SqliteStatePath) &&
                    SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath)
                        .ReadPromotionFactAsync(goalId).GetAwaiter().GetResult() is { } promotion)
                    return promotion.PromotedMainRevision;

                var journal = GoalOperationJournal.Read(workspace.ExecutionDirectory, goalId);
                if (GoalOperationJournal.HasRetiredTerminalDisposition(journal)) return null;
                var disposition = GoalOperationJournal.TryGetLatestTerminalDisposition(journal);
                if (disposition is { Kind: GoalTerminalDispositionKind.Landed, IntegrateSha: { } integrateSha })
                    return integrateSha;
                if (!GoalOperationJournal.HasCompletedLandingEvidence(journal)) return null;
                return GoalOperationJournal.TryGetLatestLandingIntent(journal)?.MergeCommitSha ??
                    ReadRecoveredLandingCommit(journal);
            },
            () => ReadAdoptions(workspace.ConductEventsLogPath),
            (ancestor, commit) =>
            {
                var result = GitCli.Run(workspace.ExecutionDirectory, "merge-base", "--is-ancestor", "--", ancestor, commit);
                if (result.ExitCode is 0 or 1) return result.ExitCode == 0;
                throw new InvalidOperationException($"Unable to check cost goal ancestry for {ancestor} in {commit}.");
            }, reportFailure);
    }

    private static string? ReadRecoveredLandingCommit(GoalOperationJournalSummary journal)
    {
        // Landing recovery records success before tombstoning its intent. Bind that success to the
        // preceding intent, since a failed publication can also leave a tombstone.
        var sawTombstone = false;
        var sawRecovery = false;
        for (var index = journal.Entries.Count - 1; index >= 0; index--)
        {
            var entry = journal.Entries[index];
            if (entry.Operation == GoalOperationJournal.LandingIntentOperation)
            {
                if (sawTombstone)
                    return sawRecovery && entry.Status == GoalOperationStatus.Completed ? entry.BranchHeadSha : null;
                if (entry.Status != GoalOperationStatus.Failed) return null;
                sawTombstone = true;
            }
            else if (sawTombstone && entry is { Operation: "conductor:landing-recovery", Status: GoalOperationStatus.Completed })
                sawRecovery = true;
        }
        return null;
    }

    private static IEnumerable<(string Commit, DateTimeOffset At)> ReadAdoptions(string logPath)
    {
        var directory = Path.GetDirectoryName(logPath)!;
        if (!Directory.Exists(directory)) yield break;
        foreach (var adoption in ReadAdoptionFile(logPath)) yield return adoption;
        foreach (var path in Directory.GetFiles(directory, "conduct-events-*.log"))
            foreach (var adoption in ReadAdoptionFile(path)) yield return adoption;
    }

    private static IEnumerable<(string Commit, DateTimeOffset At)> ReadAdoptionFile(string path)
    {
        FileStream stream;
        try { stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        catch (FileNotFoundException) { yield break; } // The active log may have just rotated.
        using (stream)
        using (var reader = new StreamReader(stream))
        {
            while (reader.ReadLine() is { } line)
                if (TryParseAdoption(line) is { } adoption) yield return adoption;
        }
    }

    private static (string Commit, DateTimeOffset At)? TryParseAdoption(string line)
    {
        const string prefix = "FLEET_BUILD_ADOPTED commit=";
        if (!line.Contains(prefix, StringComparison.Ordinal)) return null;
        try
        {
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("timestamp", out var timestamp) || timestamp.ValueKind != JsonValueKind.String ||
                !timestamp.TryGetDateTimeOffset(out var at) ||
                !root.TryGetProperty("detail", out var detail) || detail.ValueKind != JsonValueKind.String ||
                detail.GetString() is not { } text || !text.StartsWith(prefix, StringComparison.Ordinal)) return null;
            var end = text.IndexOf(' ', prefix.Length);
            var commit = end < 0 ? text[prefix.Length..] : text[prefix.Length..end];
            return commit.Length > 0 ? (commit, at) : null;
        }
        catch (JsonException) { return null; }
    }
}
