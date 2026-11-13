using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class MultiRepositoryAcceptanceGate(
    OrchestratorWorkspace workspace, OrchestratorProjectRegistry registry, IGoalAcceptanceVerifier verifier)
{
    internal MultiRepositoryAcceptanceReceiptStore Store { get; } = new(workspace.OrchestratorDirectory);

    internal AcceptanceVerificationSummary? ReadHomeVerification(GoalId goalId)
    {
        var outcome = Store.TryRead(goalId)?.Outcomes.SingleOrDefault(outcome => outcome.Key == "default");
        if (outcome is null) return null;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<AcceptanceVerificationSummary>(File.ReadAllText(outcome.EvidenceReference));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // The landing sink reports missing gate identity; it cannot infer selected execution.
            return null;
        }
    }

    internal MultiRepositoryAcceptanceResult Run(Goal goal, GoalLifecycleState state,
        Func<AcceptanceVerificationSummary> runHome, CancellationToken cancellationToken = default)
    {
        var captured = CurrentTuple(goal);
        if (captured.Tuple is null)
        {
            // Missing inputs cannot produce a canonical tuple. Keep keyed failure evidence without
            // inventing a candidate or publishing an incomplete aggregate receipt.
            var key = captured.Failure.Split(':')[0];
            if (goal.Repositories!.Keys.Contains(key, StringComparer.Ordinal)
                && (captured.Failure.Contains("missing-worktree", StringComparison.Ordinal)
                    || captured.Failure.Contains("MissingAcceptanceManifestException", StringComparison.Ordinal)))
            {
                var evidence = Store.WriteEvidence(goal.Id, Guid.NewGuid().ToString("N"), key, new { Error = captured.Failure });
                return Failure([new(key, false, evidence, captured.Failure)]);
            }
            return new(Waiting(state, captured.Failure));
        }
        var existing = Store.TryRead(goal.Id);
        if (IsPassedCurrent(goal, existing, captured.Tuple)) return new(null, Landable: existing);

        var attemptId = Guid.NewGuid().ToString("N");
        var bases = new Dictionary<string, string>(StringComparer.Ordinal);
        var baseErrors = new Dictionary<string, Exception>(StringComparer.Ordinal);
        var resolver = new MultiRepositoryIntegrationBases(workspace, registry);
        foreach (var key in goal.Repositories!.Keys)
        {
            try { bases[key] = resolver.Resolve(key).Tip; }
            catch (Exception ex) { baseErrors[key] = ex; }
        }
        var outcomes = new List<RepositoryAcceptanceOutcome>();
        var projectVerifier = new MultiRepositoryProjectVerifier(registry, verifier);
        foreach (var key in new[] { "default" }.Concat(goal.Repositories!.Keys.Where(key => key != "default")))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (baseErrors.TryGetValue(key, out var baseError)) throw baseError;
                if (key == "default")
                {
                    var summary = runHome();
                    outcomes.Add(new(key, summary.Passed, Store.WriteEvidence(goal.Id, attemptId, key, summary),
                        summary.FailureDetail ?? string.Join("; ", summary.RequiredUnmetCriteria.Select(check => check.Name + ": " + check.OutputTail))));
                }
                else
                {
                    var result = projectVerifier.Run(goal, key, cancellationToken);
                    outcomes.Add(new(key, result.Passed && !result.Skipped,
                        Store.WriteEvidence(goal.Id, attemptId, key, result), result.OutputTail));
                }
            }
            catch (Exception ex) when (ex is DotnetBuildSlotsBusyException or BuildLockBlockedException
                or AcceptanceInfrastructureDeferredException or AcceptanceAttemptCancelledException)
            {
                // These attempts did not produce a repository verdict. Leave the aggregate unpublished
                // so both conductor and CLI hold without recording a failure or retrying the Developer.
                return new(Waiting(state, key + ": " + ex.GetType().Name + ": " + ex.Message));
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                var detail = ex.GetType().Name + ": " + ex.Message;
                outcomes.Add(new(key, false, Store.WriteEvidence(goal.Id, attemptId, key, new { Error = detail }), detail));
            }
        }

        var receipt = new MultiRepositoryAcceptanceReceipt(goal.Id, captured.Tuple.Canonical, outcomes, DateTimeOffset.UtcNow, bases);
        Store.Write(receipt, goal.Repositories.Repositories);
        var current = CurrentTuple(goal);
        if (current.Tuple is null || !receipt.IsCurrentFor(current.Tuple))
            return new(Waiting(state, "candidate changed during acceptance; re-gate every declared repository. " + current.Failure));
        if (receipt.Passed) return new(null, Landable: receipt);

        return Failure(receipt.Outcomes.Where(outcome => !outcome.Passed).ToArray());
    }

    private static MultiRepositoryAcceptanceResult Failure(IReadOnlyList<RepositoryAcceptanceOutcome> failed)
    {
        var checks = failed.Select(outcome => new AcceptanceCheckResult(outcome.Key + ": acceptance", false, 1,
            "evidence=" + outcome.EvidenceReference + Environment.NewLine + outcome.Detail)).ToArray();
        return new(null, new AcceptanceVerificationSummary(false, checks,
            "Repository acceptance failed: " + string.Join(", ", failed.Select(outcome => outcome.Key)), checks.Select(check => check.Name).ToArray()));
    }

    internal MultiRepositoryAcceptanceResult Inspect(Goal goal, GoalLifecycleState state)
    {
        var current = CurrentTuple(goal);
        var receipt = Store.TryRead(goal.Id);
        return IsPassedCurrent(goal, receipt, current.Tuple) ? new(null, Landable: receipt)
            : new(Waiting(state, "a current passing repository receipt is required; run acceptance. " + current.Failure));
    }

    internal bool HasCurrentPass(Goal goal) => IsPassedCurrent(goal, Store.TryRead(goal.Id), CurrentTuple(goal).Tuple);

    private bool IsPassedCurrent(Goal goal, MultiRepositoryAcceptanceReceipt? receipt, RepositoryCandidateTuple? tuple)
    {
        if (tuple is null || receipt is not { Passed: true } || receipt.GoalId != goal.Id
            || !receipt.IsComplete(goal.Repositories!.Repositories) || !receipt.IsCurrentFor(tuple)
            || !AcceptancePrecheck.HasCompletedPassedVerificationForAllTasks(goal)) return false;
        return true; // Registry target changes are checked against recorded bases by the landing preflight.
    }

    private (RepositoryCandidateTuple? Tuple, string Failure) CurrentTuple(Goal goal)
    {
        var candidate = GoalRepositoryCandidateIdentity.Compute(goal, workspace, registry);
        return RepositoryCandidateTuple.TryParse(candidate.Identity?.RepositoryTupleCanonical, out var tuple)
            ? (tuple, string.Empty) : (null, candidate.Failure);
    }

    private static ConductorAdvanceOutcome.Held Waiting(GoalLifecycleState state, string reason) =>
        new(state, "multi-repository-acceptance-pending: " + reason);
}
