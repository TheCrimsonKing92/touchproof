using System.Text.Json;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ExperimentReplayCorpusImporter
{
    internal sealed record Round(string Project, string GoalId, string TaskId, DateTimeOffset DispatchAt,
        DateTimeOffset CompletedAt, string? BaseCommit, string? CandidateCommit, string? ObservedMain,
        ExperimentReplaySource Source);
    internal sealed record Gate(string GoalId, string CandidateCommit, string ObservedMain,
        ExperimentReplayGate Evidence, string? TaskId = null, DateTimeOffset? DispatchedAt = null,
        ExperimentReplaySource? DispatchLinkSource = null);
    internal sealed record Inventory(int Considered, int Added, int Replayable,
        IReadOnlyList<ExperimentReplayCase> Cases, IReadOnlyList<string> Diagnostics, string CorpusIdentity);

    internal static IReadOnlyList<ExperimentReplayCase> Map(IEnumerable<Round> rounds, IReadOnlyList<Gate> gates,
        DateTimeOffset cutoff)
    {
        var all = rounds.ToArray();
        var population = all.Where(r => r.CompletedAt <= cutoff && r.CompletedAt >= cutoff.AddDays(-30))
            .OrderByDescending(r => r.CompletedAt).ThenBy(r => r.TaskId, StringComparer.Ordinal)
            .ThenBy(r => r.DispatchAt).Take(100).ToArray();
        return population.Select(round =>
        {
            var reasons = new List<string>();
            if (!Commit(round.BaseCommit)) reasons.Add("dispatch base is unavailable");
            if (!Commit(round.CandidateCommit)) reasons.Add("candidate commit is unavailable");
            if (!Commit(round.ObservedMain)) reasons.Add("observed main was not recorded for this dispatch");
            // A unique commit tuple is not evidence that a gate followed this task/dispatch.
            var ambiguous = all.Count(r => r.GoalId == round.GoalId && r.TaskId == round.TaskId &&
                r.DispatchAt == round.DispatchAt) != 1;
            if (ambiguous) reasons.Add("task/dispatch provenance maps to multiple rounds");
            var linked = !ambiguous && Commit(round.CandidateCommit) && Commit(round.ObservedMain)
                ? gates.Where(g => g.TaskId == round.TaskId && g.DispatchedAt == round.DispatchAt &&
                    g.DispatchLinkSource is not null && g.GoalId == round.GoalId && g.CandidateCommit == round.CandidateCommit &&
                    g.ObservedMain == round.ObservedMain && g.Evidence.StartedAt >= round.CompletedAt)
                    .Select(g => g.Evidence with { Sources = g.Evidence.Sources.Append(g.DispatchLinkSource!).ToArray() })
                    .OrderBy(g => g.StartedAt).ThenBy(g => g.AttemptId, StringComparer.Ordinal).ToArray()
                : [];
            if (linked.Length == 0) reasons.Add("no exact-linked subsequent solo gate");
            if (linked.Any(g => g.Outcome == "unknown")) reasons.Add("gate outcome unavailable");
            var id = ExperimentReplayStore.Hash($"{round.Project}/{round.GoalId}/{round.TaskId}/{round.DispatchAt:O}");
            return new ExperimentReplayCase(id, round.Project, round.GoalId, round.TaskId, round.DispatchAt,
                round.CompletedAt, round.BaseCommit, round.CandidateCommit, round.ObservedMain,
                reasons.Count == 0 ? "replayable" : "unknown", reasons, linked, [round.Source]);
        }).ToArray();
    }

    internal static async Task<Inventory> ImportAsync(OrchestratorWorkspace workspace, ExperimentReplayStore store,
        DateTimeOffset cutoff, CancellationToken token)
    {
        var repository = SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath);
        var kernel = await repository.LoadAsync(token);
        var rounds = new List<Round>();
        var diagnostics = new List<string>();
        var evidenceDirectory = Path.Combine(workspace.OrchestratorDirectory, "experiment-replay", "evidence");
        foreach (var goal in kernel.Goals)
        {
            foreach (var round in WorkerRoundLedger.FromGoal(goal).Where(r => r.Role == AgentRole.Developer && r.EndedAt is not null))
            {
                var task = goal.Tasks.Single(t => t.Id.Value == round.TaskId);
                var dispatch = task.DispatchHistory.Where(d => d.DispatchedAt == round.DispatchedAt).ToArray();
                if (dispatch.Length != 1) { diagnostics.Add($"{round.TaskId}/{round.DispatchedAt:O}: ambiguous dispatch record"); continue; }
                var record = dispatch[0];
                // Ledger supplies inventory timing only. It never supplies gate linkage.
                var raw = JsonSerializer.Serialize(new { round, dispatch = record }, ExperimentReplayStore.Json);
                var source = ExperimentReplayEvidence.Write(evidenceDirectory, raw, ".json");
                var mainReceipt = record.PreDispatchIntegrationReceipt;
                var main = mainReceipt is not null && mainReceipt.GoalId == goal.Id && mainReceipt.TaskId == task.Id &&
                    mainReceipt.ResultingCandidateSha == record.BaseCommit ? mainReceipt.IntegratedMainSha : null;
                rounds.Add(new(workspace.ProjectName, goal.Id.Value, task.Id.Value, round.DispatchedAt,
                    round.EndedAt!.Value, record.BaseCommit, record.ResultCommit, main, source));
            }
        }
        var skipped = new List<ImpactReplayDiagnostic>();
        var retained = ImpactReplayAttemptSource.Read(workspace.OrchestratorDirectory, cutoff.AddDays(-30), skipped);
        diagnostics.AddRange(skipped.Select(s => $"{s.Source}: {s.Reason}"));
        var gates = ReadGates(retained, evidenceDirectory, diagnostics, token);
        var cases = Map(rounds, gates, cutoff);
        var added = cases.Count(store.Import);
        return new(cases.Count, added, cases.Count(c => c.Replayability == "replayable"), cases, diagnostics,
            ExperimentReplayStore.Hash(JsonSerializer.Serialize(cases, ExperimentReplayStore.Json)));
    }

    internal static IReadOnlyList<Gate> ReadGates(IReadOnlyList<ImpactReplayStoredAttempt> retained,
        string evidenceDirectory, List<string> diagnostics, CancellationToken token)
    {
        var gates = new List<Gate>();
        foreach (var gate in retained)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                // Source reader validates paired receipts; retain both bytes, not only its filtered projection.
                var metadata = gate.MetadataPath ?? throw new InvalidDataException("Validated gate source path unavailable.");
                var resultPath = metadata[..^".attempt.json".Length] + ".result.json";
                var result = JsonSerializer.Deserialize<ConductorParallelAcceptanceRunArtifact>(File.ReadAllText(resultPath), ExperimentReplayStore.Json)!;
                var sources = new List<ExperimentReplaySource>
                { ExperimentReplayEvidence.Copy(evidenceDirectory, metadata), ExperimentReplayEvidence.Copy(evidenceDirectory, resultPath) };
                var failures = new SortedSet<string>(StringComparer.Ordinal);
                var hasResults = false;
                var complete = gate.TestResultPaths.Count > 0;
                var fatal = false;
                foreach (var path in gate.TestResultPaths)
                {
                    var full = Path.IsPathRooted(path) ? path : Path.GetFullPath(path, gate.GoalDirectory);
                    if (!File.Exists(full)) { complete = false; diagnostics.Add($"{gate.AttemptId}: missing TRX {path}"); continue; }
                    sources.Add(ExperimentReplayEvidence.Copy(evidenceDirectory, full));
                    var tests = ExperimentReplayEvidence.Tests(full);
                    hasResults |= tests.Count > 0;
                    complete &= tests.Any(t => t.Outcome is "Passed" or "Failed");
                    fatal |= tests.Any(t => t.Outcome == "Error");
                    failures.UnionWith(tests.Where(t => t.Outcome == "Failed").Select(t => t.Identity));
                }
                var outcome = ClassifyOutcome(result.Acceptance?.Passed, result.FaultKind is not null || fatal,
                    complete && hasResults, failures.Count);
                gates.Add(new(gate.GoalId, gate.BranchHeadSha, gate.MainHeadSha,
                    new(gate.AttemptId, gate.StartedAt, outcome, failures.ToArray(), sources)));
                // Retained solo gate receipts do not name a Developer task/dispatch. Preserve inventory,
                // but never promote tuple equality to exact linkage for replay/catch scoring.
                diagnostics.Add($"{gate.AttemptId}: authoritative task/dispatch linkage was not recorded");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or JsonException or System.Xml.XmlException)
            { diagnostics.Add($"{gate.AttemptId}: retained evidence unreadable: {ex.Message}"); }
        }
        return gates;
    }

    private static bool Commit(string? value) => value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit);

    internal static string ClassifyOutcome(bool? passed, bool apparatusFailure, bool completeTestEvidence, int failedTests) =>
        apparatusFailure ? "apparatus-failed" : passed is null || !completeTestEvidence || passed == true && failedTests > 0 ? "unknown"
            : failedTests > 0 ? "test-failed" : passed == true ? "passed" : "non-test-or-unresolved-failed";
}
