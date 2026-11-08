using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

/// <summary>Only the live conductor calls this mutator, under its goal evidence lease.</summary>
internal sealed class MultiRepositoryTupleLanding(OrchestratorWorkspace workspace, OrchestratorProjectRegistry registry)
{
    internal MultiRepositoryLandingRecordStore Store { get; } = new(workspace.OrchestratorDirectory);
    private MultiRepositoryAcceptanceReceiptStore Receipts => new(workspace.OrchestratorDirectory);
    private MultiRepositoryIntegrationBases Bases => new(workspace, registry);

    internal bool RequiresRecovery(Goal goal) => MultiRepositoryGoal.Applies(goal)
        && (Store.ReadFailure(goal.Id) is not null || Store.ReadAttempt(goal.Id) is not null && Store.ReadLanding(goal.Id) is null);

    internal GoalLifecycleFacts ProjectFacts(Goal goal, GoalLifecycleFacts facts) => RequiresRecovery(goal)
        ? facts with { IsMerged = false, IsRecorded = false, IsCleanedUp = false } : facts;

    internal MultiRepositoryTupleLandingResult Execute(Goal goal, GoalLifecycleState state,
        MultiRepositoryAcceptanceReceipt receipt, Func<LandingResult> landHome, Func<string?> mutationBlocker)
    {
        if (Store.ReadFailure(goal.Id) is { } failure)
            return new(Hold: MultiRepositoryLandingFailedHold.Create(state, failure));
        if (Store.ReadLanding(goal.Id) is { } completed) return new(Record: completed);
        if (Store.ReadAttempt(goal.Id) is { } interrupted)
            return Fail(state, interrupted, interrupted.Steps.LastOrDefault()?.Key ?? "default", "Interrupted tuple landing; compensating before operator repair.");
        if (!receipt.Passed || receipt.GoalId != goal.Id || !receipt.IsComplete(goal.Repositories!.Repositories))
            throw new InvalidOperationException("A complete passing tuple receipt is required for landing.");

        var order = goal.Repositories.DeclaredOrder.Where(key => key != "default").Append("default").ToArray();
        var candidateTips = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in order)
        {
            try
            {
                var target = Bases.Resolve(key);
                if (!receipt.IntegrationBases.TryGetValue(key, out var recorded) || recorded != target.Tip)
                    return Stale(goal, state, key, recorded, target.Tip);
                candidateTips[key] = RepositoryIntegrationLanding.Git(SourceRoot(goal, key), "rev-parse", "refs/heads/" + Branch(goal, key));
            }
            catch (Exception ex) { return Stale(goal, state, key, null, ex.Message); }
        }
        var currentCandidate = GoalRepositoryCandidateIdentity.Compute(goal, workspace, registry);
        if (currentCandidate.Identity?.RepositoryTupleCanonical != receipt.TupleCanonical)
            return Stale(goal, state, "tuple", receipt.TupleCanonical, currentCandidate.Failure);
        RepositoryCandidateTuple.TryParse(receipt.TupleCanonical, out var acceptedTuple);

        var attempt = new MultiRepositoryLandingAttempt(goal.Id, receipt.TupleCanonical, []);
        LandingResult? homeResult = null;
        foreach (var key in order)
        {
            try
            {
                var target = Bases.Resolve(key);
                if (target.Tip != receipt.IntegrationBases[key])
                {
                    if (attempt.Steps.Count == 0) return Stale(goal, state, key, receipt.IntegrationBases[key], target.Tip);
                    return Fail(state, attempt, key, "integration branch moved after preflight");
                }
                var branch = Branch(goal, key);
                var candidate = GoalRepositoryCandidateIdentity.Compute(goal, workspace, registry);
                if (!RepositoryCandidateTuple.TryParse(candidate.Identity?.RepositoryTupleCanonical, out var currentTuple)
                    || currentTuple!.Entries.Single(entry => entry.Key == key).Value.Canonical
                        != acceptedTuple!.Entries.Single(entry => entry.Key == key).Value.Canonical
                    || RepositoryIntegrationLanding.Git(SourceRoot(goal, key), "rev-parse", "refs/heads/" + branch) != candidateTips[key])
                {
                    if (attempt.Steps.Count == 0) return Stale(goal, state, key, receipt.TupleCanonical, "candidate changed");
                    return Fail(state, attempt, key, "candidate changed during tuple landing");
                }
                if (mutationBlocker() is { Length: > 0 } blocked)
                    return Fail(state, attempt, key, blocked);
                var step = new RepositoryLandingStep(key, target.Root, target.Branch, target.Tip,
                    candidateTips[key]);
                attempt = attempt with { Steps = attempt.Steps.Append(step).ToArray() };
                Store.Write(attempt); // Persist intent before calling an executor that can mutate then throw.
                string landed;
                if (key == "default")
                {
                    homeResult = landHome();
                    if (!homeResult.MainAdvanced) throw new InvalidOperationException(homeResult.Message);
                    landed = homeResult.MergeCommitSha ?? Bases.Resolve(key).Tip;
                }
                else landed = RepositoryIntegrationLanding.Execute(goal, target, step.CandidateTip);
                attempt = attempt with { Steps = attempt.Steps.Take(attempt.Steps.Count - 1).Append(step with { LandedTip = landed }).ToArray() };
                Store.Write(attempt);
            }
            catch (Exception ex) { return Fail(state, attempt, key, ex.GetType().Name + ": " + ex.Message); }
        }
        var record = new MultiRepositoryLandingRecord(goal.Id, receipt.TupleCanonical,
            attempt.Steps.ToDictionary(step => step.Key, step => step.LandedTip!, StringComparer.Ordinal),
            attempt.Steps.ToDictionary(step => step.Key, step => step.PreTip, StringComparer.Ordinal), order, DateTimeOffset.UtcNow);
        Store.Write(record);
        GoalOperationJournal.Completed(workspace.ExecutionDirectory, goal, "conductor:land", "Landed multi-repository tuple",
            mainHeadSha: record.LandedCommits["default"]);
        return new(record, homeResult);
    }

    private static string Branch(Goal goal, string key) => key == "default" ? GoalWorktrees.BranchName(goal.Id)
        : GoalRepositoryWorktreeLayout.BranchName(goal.Id, key);

    private string SourceRoot(Goal goal, string key) => key == "default" ? workspace.ExecutionDirectory
        : goal.Repositories!.CheckoutRoots[key];

    internal MultiRepositoryTupleLandingResult Recover(Goal goal, GoalLifecycleState state)
    {
        if (Store.ReadFailure(goal.Id) is { } failure) return new(Hold: MultiRepositoryLandingFailedHold.Create(state, failure));
        var attempt = Store.ReadAttempt(goal.Id) ?? throw new InvalidOperationException("No tuple attempt to recover.");
        return Fail(state, attempt, attempt.Steps.LastOrDefault()?.Key ?? "default", "Interrupted tuple landing; operator repair required.");
    }

    private MultiRepositoryTupleLandingResult Fail(GoalLifecycleState state, MultiRepositoryLandingAttempt attempt, string key, string error)
    {
        var commits = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var manual = new List<string>();
        foreach (var saved in attempt.Steps.Reverse())
        {
            try
            {
                var step = saved.LandedTip is null ? RepositoryLandingCompensator.IdentifyMutation(saved) : saved;
                if (step.LandedTip is not null)
                {
                    var compensation = RepositoryLandingCompensator.Revert(step);
                    if (compensation.Commits.Count > 0) commits.Add(step.Key, compensation.Commits);
                    if (compensation.Error is not null)
                    {
                        manual.Add(step.Key);
                        error += $"; {step.Key} compensation: {compensation.Error}";
                    }
                }
            }
            catch (Exception ex) { manual.Add(saved.Key); error += $"; {saved.Key} compensation: {ex.Message}"; }
        }
        var failure = new MultiRepositoryLandingFailure(attempt.GoalId, attempt.TupleCanonical, key, error, commits, manual, DateTimeOffset.UtcNow);
        Store.Write(failure);
        Receipts.MarkStale(attempt.GoalId);
        return new(Hold: MultiRepositoryLandingFailedHold.Create(state, failure));
    }

    private MultiRepositoryTupleLandingResult Stale(Goal goal, GoalLifecycleState state, string key, string? recorded, string current)
    {
        Receipts.MarkStale(goal.Id);
        return new(Hold: new ConductorAdvanceOutcome.Held(state,
            $"multi-repository-acceptance-stale: {key}: recorded={recorded ?? "missing"}, current={current}; re-gate every declared repository.")
        {
            Decision = new PolicyDecisionRecord("multi-repository-acceptance", "Hold", 1, "multi-repository-acceptance-stale", "next-tick",
                [new PolicyDecisionFact("repository", key), new PolicyDecisionFact("recorded_base", recorded ?? "missing"),
                 new PolicyDecisionFact("current_base", current)])
        });
    }
}
