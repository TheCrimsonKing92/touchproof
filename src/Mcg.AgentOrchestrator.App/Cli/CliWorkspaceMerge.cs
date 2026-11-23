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
        var needsPublication = duties.Any(item => item.Publication is null);
        var root = context.Workspace.ExecutionDirectory;
        // Publication authority requires the integration target. Goals without later
        // duties retain the existing worktree-service merge contract.
        if (duties.Count > 0)
        {
            var target = GitCli.Run(root, "symbolic-ref", "--quiet", "HEAD");
            if (!target.Succeeded || !string.Equals(target.Output.Trim(),
                    $"refs/heads/{context.Workspace.IntegrationBranch}", StringComparison.Ordinal))
            {
                return new GoalWorktreeMergeResult(false, context.Worktrees.BranchName(goal.Id),
                    $"Fast-forward blocked: HEAD must be attached to integration branch '{context.Workspace.IntegrationBranch}'.",
                    null);
            }
        }
        var priorMain = needsPublication ? ReadRevision("HEAD") : null;
        var candidate = needsPublication ? ReadRevision(context.Worktrees.BranchName(goal.Id)) : null;
        string? repairBlock = null;
        var merge = context.Worktrees.TryFastForwardMerge(root, goal.Id, () =>
        {
            var blocked = mutationBlocker();
            if (!string.IsNullOrWhiteSpace(blocked)) return blocked;
            return repairBlock = CheckRepairAdmission(context, goal, testedPair, requireNormalAcceptance: true, out _);
        });
        repairBlockReason = repairBlock;
        if (merge?.FastForwarded != true || !needsPublication) return merge;

        var at = (context.DiagnosticsClock ?? new SystemClock()).UtcNow;
        var promotedMain = ReadRevision("HEAD");
        var repository = new SqliteOrchestratorStateRepository(context.Workspace.SqliteStatePath);
        if (!string.Equals(priorMain, promotedMain, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(candidate, promotedMain, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("CLI publication does not match the captured goal candidate.");
            repository.RecordPromotionFacts([new GoalPromotionFact(goal.Id.Value, candidate!, priorMain!,
                promotedMain, at, GoalPromotionFact.CliFastForwardProvenance)]);
        }
        // An already-merged branch may recover a known fact, but never invent one.
        publication = repository.ReadPromotionFactAsync(goal.Id).GetAwaiter().GetResult();
        if (publication is not null) context.Kernel.BindPostLandingPublication(goal.Id, publication);
        return merge;

        string ReadRevision(string revision)
        {
            var result = GitCli.Run(root, "rev-parse", revision);
            if (!result.Succeeded || string.IsNullOrWhiteSpace(result.Output))
                throw new InvalidOperationException($"Cannot capture CLI publication revision '{revision}': {result.Error}");
            return result.Output.Trim();
        }
    }

    internal static string? CheckRepairAdmission(
        CliExecutionContext context, Goal goal, GateReadyCandidateRevisionPair? preparedPair,
        bool requireNormalAcceptance, out SoloBaselineRepairStore? repairStore, string? worktreePath = null)
    {
        repairStore = null;
        var workspace = context.Workspace;
        var storePath = Path.Combine(workspace.OrchestratorDirectory, SoloBaselineRepairStore.FileName);
        if (!File.Exists(storePath)) return null;

        var store = new SoloBaselineRepairStore(storePath);
        var root = workspace.ExecutionDirectory;
        var candidateRef = $"refs/heads/{context.Worktrees.BranchName(goal.Id)}";
        var mainRef = $"refs/heads/{workspace.IntegrationBranch}";
        var pair = preparedPair ?? ReadPair();
        var findings = SoloBaselineRepairCoordinator.SoloRepairFindings(store, goal, pair.BranchRevision, pair.MainRevision);
        if (findings.Count == 0) return null;

        if (pair.BranchRevision is not { } candidate || pair.MainRevision is not { } main ||
            !store.IsEvidenceCurrent(goal.Id.Value, findings, candidate, main))
            return "baseline repair and original-selection validation are pending for the current revision pair";

        if (requireNormalAcceptance &&
            store.ReadCheck(SoloBaselineRepairStore.NormalGateKey(goal.Id.Value, candidate, main))?.Outcome != "passed")
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
        static string? ReadRef(string directory, string reference) =>
            GitRevisionReader.Shared.TryResolve(directory, reference, out var revision) ? revision : null;
    }

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
