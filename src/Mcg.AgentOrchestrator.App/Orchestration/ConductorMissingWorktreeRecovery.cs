using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorMissingWorktreeRecovery
{
    internal const string Operation = "conductor:workspace-recovery";

    internal static bool TryPrepareForDispatch(
        Goal goal, Func<Goal, GoalEvidenceOperationScope?>? prepare, DispatchStartFacts facts,
        out GoalEvidenceOperationScope? ownership, out DispatchStartFacts failure)
    {
        ownership = null;
        failure = facts;
        try
        {
            ownership = prepare?.Invoke(goal);
            return true;
        }
        catch (ConductorDriver.EvidenceMutationLeaseUnavailableException ex)
        {
            failure = facts with { WorkspacePreparationLeaseUnavailableMessage = ex.Message };
            return false;
        }
        catch (Exception ex)
        {
            failure = facts with { WorkspacePreparationFailureMessage = ex.Message };
            return false;
        }
    }

    // The returned lease is retained by the preparation caller, including normal integration,
    // candidate refresh, context/sandbox generation and the critical dispatch checkpoint.
    internal static GoalEvidenceOperationScope? Prepare(
        string executionDirectory, Goal goal, GoalEvidenceOperationCoordinator coordinator,
        Func<IReadOnlyList<Goal>> readGoals, Func<GoalId, bool> hasGateOwner,
        GoalWorktreeCleanupHooks? hooks = null, Action<string>? workspaceRestored = null)
    {
        if (GoalWorktrees.TryResolve(executionDirectory, goal.Id) is not null &&
            !HasUnreceiptedRecovery(executionDirectory, goal.Id)) return null;
        var start = coordinator.TryBegin(goal, "conductor:workspace-preparation");
        if (start.Scope is not { } ownership)
        {
            RecordReceipt(executionDirectory, goal, new GoalWorktreeRecoveryResult(goal.Id,
                GoalWorktrees.WorktreePath(executionDirectory, goal.Id), GoalWorktrees.BranchName(goal.Id), null,
                false, false, $"lifecycle owner unavailable: {start.LeaseFact?.RecoveryStatus}"));
            throw new ConductorDriver.EvidenceMutationLeaseUnavailableException(
                $"GOAL_OPERATION_BLOCKED goal={goal.Id.Value} operation={Operation} reason=owner-unavailable lease={start.LeaseFact?.RecoveryStatus}");
        }
        try
        {
            var result = GoalWorktrees.RecoverMissing(executionDirectory, goal.Id,
                () => OwnerRefusal(goal.Id, readGoals(), hasGateOwner),
                receipt => RecordReceipt(executionDirectory, goal, receipt), hooks);
            if (!result.Succeeded)
                throw new InvalidOperationException($"Goal worktree recovery refused: {result.Observation}");
            workspaceRestored?.Invoke(result.CanonicalPath);
            return ownership;
        }
        catch
        {
            ownership.Dispose();
            throw;
        }
    }

    private static bool HasUnreceiptedRecovery(string executionDirectory, GoalId goalId)
    {
        // A directory left by a failed receipt write must not bypass recovery on the next tick.
        // Journal history selects revalidation; only current state under the root lock grants it.
        var entries = GoalOperationJournal.ReadStrict(executionDirectory, goalId).Entries;
        var lastRecovery = -1;
        var lastBegin = -1;
        for (var index = 0; index < entries.Count; index++)
        {
            if (entries[index].Operation == Operation) lastRecovery = index;
            if (entries[index].Status == GoalOperationStatus.Begin &&
                entries[index].Operation is "conductor:workspace-preparation" or "conductor:workspace-create")
                lastBegin = index;
        }
        if (lastRecovery >= 0 && entries[lastRecovery].Status != GoalOperationStatus.Completed) return true;
        if (lastBegin < 0 || lastRecovery > lastBegin) return false;
        return !entries.Skip(lastBegin + 1).Any(entry => entry.Operation == entries[lastBegin].Operation &&
            entry.OperationInstanceId == entries[lastBegin].OperationInstanceId && entry.Status == GoalOperationStatus.Completed);
    }

    internal static void RecordReceipt(string executionDirectory, Goal goal, GoalWorktreeRecoveryResult receipt)
    {
        var detail = $"goal={receipt.GoalId.Value} path={receipt.CanonicalPath} branch={receipt.Branch} " +
            $"preserved_tip={receipt.PreservedTip ?? "unavailable"} recreated={receipt.Recreated} observation={receipt.Observation}";
        // Recovery's receipt must not terminalize the still-held preparation lease:
        // a terminal lease is eligible for reclamation by another operation.
        if (receipt.Succeeded) GoalOperationJournal.Completed(executionDirectory, goal, Operation, detail);
        else GoalOperationJournal.Failed(executionDirectory, goal, Operation, detail);
    }

    internal static string? OwnerRefusal(GoalId goalId, IReadOnlyList<Goal> goals, Func<GoalId, bool> hasGateOwner)
    {
        var matches = goals.Where(g => g.Id == goalId).ToArray();
        if (matches.Length != 1 || matches[0].Status != GoalStatus.Active)
            return "authoritative goal is absent, ambiguous or inactive";
        if (goals.Any(g => g.Id != goalId && GoalWorktrees.BranchName(g.Id) == GoalWorktrees.BranchName(goalId)))
            return "goal prefix is owned by another goal";
        if (GoalLiveDispatchPredicate.HasLiveDispatch(matches[0]) ||
            matches[0].Tasks.Any(task => task.CompletionBuildEvidence is { CustodyReleased: false }))
            return "dispatch or completion-build owner holds workspace";
        return hasGateOwner(goalId) ? "gate owner holds workspace" : null;
    }
}
