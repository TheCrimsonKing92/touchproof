using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

// Owns CLI workspace publication and applying its result to the acceptance transaction.
internal static class CliWorkspaceMerge
{
    // Capture publication at the successful CLI merge boundary. A later HEAD or
    // recovery clock cannot establish when the original duty became due.
    internal static GoalWorktreeMergeResult? MergeWithPublication(
        CliExecutionContext context, Goal goal, out GoalPromotionFact? publication,
        Func<string?> mutationBlocker, out string? repairBlockReason,
        GateReadyCandidateRevisionPair? testedPair = null)
    {
        publication = null;
        repairBlockReason = null;
        var duties = goal.OutstandingPostLandingDuties;
        var root = context.Workspace.ExecutionDirectory;
        var branch = context.Worktrees.BranchName(goal.Id);
        var mainRef = $"refs/heads/{context.Workspace.IntegrationBranch}";
        var target = RootCheckoutLandingGuard.ReadSymbolicHead(static (dir, args) => GitCli.Run(dir, args), root);
        var canonicalRepair = OpenRepairStore(context.Workspace)?.IsCanonicalRepair(goal.Id.Value) == true;
        var needsPublication = canonicalRepair || duties.Any(item => item.Publication is null);
        var requiresIntegrationTarget = canonicalRepair || duties.Count > 0;
        if ((requiresIntegrationTarget && target != mainRef) ||
            (target is null && ReadRef(root, "HEAD") is not null))
        {
            return new GoalWorktreeMergeResult(false, branch,
                $"Fast-forward blocked: HEAD must be attached to integration branch '{context.Workspace.IntegrationBranch}'.",
                null);
        }

        var pair = testedPair ?? new GateReadyCandidateRevisionPair(ReadRef(root, $"refs/heads/{branch}"),
            target is null ? null : ReadRef(root, target));
        if (target is not null && pair is not { BranchRevision: not null, MainRevision: not null })
        {
            return new(false, branch, "Fast-forward blocked: cannot resolve the candidate and target revisions.", null);
        }
        string? repairBlock = null;
        var merge = target is not null && pair is { BranchRevision: { } candidate, MainRevision: { } priorMain }
            ? GoalWorktrees.TryFastForwardMergeExact(root, goal.Id, target, priorMain, candidate, CheckMutation)
            : context.Worktrees.TryFastForwardMerge(root, goal.Id, CheckMutation);
        repairBlockReason = merge?.RevisionPairChanged == true ? merge.Message : repairBlock;
        if (merge?.FastForwarded != true || !needsPublication) return merge;

        var repository = new SqliteOrchestratorStateRepository(context.Workspace.SqliteStatePath);
        publication = repository.ReadPromotionFactAsync(goal.Id).GetAwaiter().GetResult();
        if (merge.AdvancedTarget && publication is null)
        {
            var at = (context.DiagnosticsClock ?? new SystemClock()).UtcNow;
            publication = new GoalPromotionFact(goal.Id.Value, pair.BranchRevision!, pair.MainRevision!,
                pair.BranchRevision!, at, GoalPromotionFact.CliFastForwardProvenance);
            repository.RecordPromotionFacts([publication]);
        }
        // An already-merged branch may recover a known fact, but never invent one.
        if (publication is not null && duties.Count > 0) context.Kernel.BindPostLandingPublication(goal.Id, publication);
        return merge;

        string? CheckMutation()
        {
            var blocked = mutationBlocker();
            if (!string.IsNullOrWhiteSpace(blocked)) return blocked;
            return repairBlock = CheckRepairAdmission(context, goal, pair, requireNormalAcceptance: true, out _);
        }
    }

    private static SoloBaselineRepairStore? OpenRepairStore(OrchestratorWorkspace workspace, bool retain = false)
    {
        var path = Path.Combine(workspace.OrchestratorDirectory, SoloBaselineRepairStore.FileName);
        return retain || File.Exists(path) ? new SoloBaselineRepairStore(path) : null;
    }

    internal static void RetainRepairEvidence(CliExecutionContext context, Goal goal)
    {
        if (goal.LatestAcceptanceFailure?.SoloFailureReceipt is null) return;
        var workspace = context.Workspace;
        var root = workspace.ExecutionDirectory;
        GateReadyCandidateRevisionPair ReadCurrent() => new(ReadRef(root, $"refs/heads/{context.Worktrees.BranchName(goal.Id)}"),
            ReadRef(root, $"refs/heads/{workspace.IntegrationBranch}"));
        SoloBaselineRepairCoordinator.RetainSoloRepairPartition(OpenRepairStore(workspace, retain: true)!, goal,
            () => SoloAcceptanceFailureHandler.ReadContext(goal, root, ReadCurrent()), ReadCurrent);
    }

    internal static bool ReportRepairHold(CliExecutionContext context, Goal goal, GateReadyCandidateRevisionPair? pair,
        string? worktreePath, bool requireNormalAcceptance, out SoloBaselineRepairStore? repairStore)
    {
        var blocked = CheckRepairAdmission(context, goal, pair, requireNormalAcceptance, out repairStore, worktreePath);
        if (blocked is null) return false;
        Console.WriteLine($"Acceptance blocked: {blocked}.");
        return true;
    }

    private static string? CheckRepairAdmission(
        CliExecutionContext context, Goal goal, GateReadyCandidateRevisionPair? preparedPair,
        bool requireNormalAcceptance, out SoloBaselineRepairStore? repairStore, string? worktreePath = null)
    {
        repairStore = null;
        var workspace = context.Workspace;
        RetainRepairEvidence(context, goal);
        if (OpenRepairStore(workspace) is not { } store) return null;

        var root = workspace.ExecutionDirectory;
        var candidateRef = $"refs/heads/{context.Worktrees.BranchName(goal.Id)}";
        var mainRef = $"refs/heads/{workspace.IntegrationBranch}";
        var pair = preparedPair ?? ReadPair();
        var findings = SoloBaselineRepairCoordinator.SoloRepairFindings(store, goal, pair.BranchRevision, pair.MainRevision);
        if (findings.Count == 0) return null;

        if (pair.BranchRevision is not { } candidate || pair.MainRevision is not { } main ||
            !store.IsEvidenceCurrent(goal.Id.Value, findings, candidate, main))
            return "baseline repair and original-selection validation are pending for the current revision pair";

        if (requireNormalAcceptance && !HasPassedNormalGate(store, goal, pair))
            return "normal acceptance is pending for the current revision pair";

        if (worktreePath is not null)
        {
            if (ReadRef(worktreePath, "HEAD") != candidate ||
                GitCli.Run(root, "merge-base", "--is-ancestor", main, candidate) is not { Succeeded: true, DrainTimedOut: false })
                return "the candidate must be prepared on the current integration revision";
        }
        else if (GitCli.Run(root, "symbolic-ref", "--quiet", "HEAD") is not { Succeeded: true, DrainTimedOut: false } target ||
                 target.Output.Trim() != mainRef)
        {
            return $"HEAD must be attached to integration branch '{workspace.IntegrationBranch}'";
        }

        if (ReadPair() != pair)
            return "the revision pair changed after baseline repair validation or normal acceptance";

        repairStore = store;
        return null;

        GateReadyCandidateRevisionPair ReadPair() => new(ReadRef(root, candidateRef), ReadRef(root, mainRef));
    }

    internal static bool HasPassedNormalGate(SoloBaselineRepairStore? store, Goal goal, GateReadyCandidateRevisionPair? pair) =>
        pair is { BranchRevision: { } candidate, MainRevision: { } main } &&
        store?.ReadCheck(SoloBaselineRepairStore.NormalGateKey(goal.Id.Value, candidate, main))?.Outcome == "passed";

    private static string? ReadRef(string directory, string reference) =>
        GitRevisionReader.Shared.TryResolve(directory, reference, out var revision) ? revision : null;

    internal static bool ClearSupersededAcceptanceFailureForCandidate(
        CliExecutionContext context,
        Goal goal,
        string? branchHeadSha,
        string? mainHeadSha)
    {
        if (goal.LatestAcceptanceFailure is not { } failure ||
            string.IsNullOrWhiteSpace(failure.BranchHeadSha) ||
            string.IsNullOrWhiteSpace(failure.MainHeadSha))
        {
            return false;
        }

        if (string.Equals(failure.BranchHeadSha, branchHeadSha, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(failure.MainHeadSha, mainHeadSha, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (goal.Status == GoalStatus.AcceptanceFailed)
        {
            return context.Kernel.RestoreVerifiedForSupersededAcceptanceFailure(
                goal.Id,
                branchHeadSha,
                mainHeadSha);
        }

        context.Kernel.ClearAcceptanceFailure(goal.Id);
        return true;
    }

    internal static void ApplyAcceptanceResult(
        AgentOrchestratorKernel kernel,
        AcceptanceMergeCommitRequest request,
        AcceptanceMergeCommitResult result)
    {
        if (result.FastForwarded)
        {
            if (result.Publication is { } publication)
                kernel.BindPostLandingPublication(request.GoalId, publication);
            kernel.ClearAcceptanceFailure(request.GoalId);
            kernel.CompleteGoal(request.GoalId, request.CompletionReason);
        }
        else if (result.Message is not null)
        {
            kernel.RecordAcceptanceFailure(request.GoalId, ["merge"]);
        }
    }
}
