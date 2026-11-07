using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class MultiRepositoryAcceptanceGate(
    OrchestratorWorkspace workspace, OrchestratorProjectRegistry registry, IGoalAcceptanceVerifier verifier)
{
    internal MultiRepositoryAcceptanceReceiptStore Store { get; } = new(workspace.OrchestratorDirectory);

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
        if (IsPassedCurrent(goal, existing, captured.Tuple)) return LandingHold(goal, state);

        var attemptId = Guid.NewGuid().ToString("N");
        var outcomes = new List<RepositoryAcceptanceOutcome>();
        var projectVerifier = new MultiRepositoryProjectVerifier(registry, verifier);
        foreach (var key in new[] { "default" }.Concat(goal.Repositories!.Keys.Where(key => key != "default")))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
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
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                var detail = ex.GetType().Name + ": " + ex.Message;
                outcomes.Add(new(key, false, Store.WriteEvidence(goal.Id, attemptId, key, new { Error = detail }), detail));
            }
        }

        var receipt = new MultiRepositoryAcceptanceReceipt(goal.Id, captured.Tuple.Canonical, outcomes, DateTimeOffset.UtcNow);
        Store.Write(receipt, goal.Repositories.Repositories);
        var current = CurrentTuple(goal);
        if (current.Tuple is null || !receipt.IsCurrentFor(current.Tuple))
            return new(Waiting(state, "candidate changed during acceptance; re-gate every declared repository. " + current.Failure));
        if (receipt.Passed) return LandingHold(goal, state);

        return Failure(receipt.Outcomes.Where(outcome => !outcome.Passed).ToArray());
    }

    private static MultiRepositoryAcceptanceResult Failure(IReadOnlyList<RepositoryAcceptanceOutcome> failed)
    {
        var checks = failed.Select(outcome => new AcceptanceCheckResult(outcome.Key + ": acceptance", false, 1,
            "evidence=" + outcome.EvidenceReference + Environment.NewLine + outcome.Detail)).ToArray();
        return new(null, new AcceptanceVerificationSummary(false, checks,
            "Repository acceptance failed: " + string.Join(", ", failed.Select(outcome => outcome.Key)), checks.Select(check => check.Name).ToArray()));
    }

    internal MultiRepositoryAcceptanceResult LandingHold(Goal goal, GoalLifecycleState state)
    {
        var current = CurrentTuple(goal);
        var receipt = Store.TryRead(goal.Id);
        return new(IsPassedCurrent(goal, receipt, current.Tuple)
            ? MultiRepositoryLandingHold.Create(goal, state, receipt!)
            : Waiting(state, "a current passing repository receipt is required; run acceptance. " + current.Failure));
    }

    internal bool HasCurrentPass(Goal goal) => IsPassedCurrent(goal, Store.TryRead(goal.Id), CurrentTuple(goal).Tuple);

    private bool IsPassedCurrent(Goal goal, MultiRepositoryAcceptanceReceipt? receipt, RepositoryCandidateTuple? tuple)
    {
        if (tuple is null || receipt is not { Passed: true } || receipt.GoalId != goal.Id
            || !receipt.IsComplete(goal.Repositories!.Repositories) || !receipt.IsCurrentFor(tuple)
            || !AcceptancePrecheck.HasCompletedPassedVerificationForAllTasks(goal)) return false;
        try
        {
            foreach (var key in goal.Repositories.Keys.Where(key => key != "default"))
                _ = MultiRepositoryProjectVerifier.ResolveProject(registry, goal, key);
            return true;
        }
        catch (Exception) { return false; } // Re-run to capture the keyed resolution failure as evidence.
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
