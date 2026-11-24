using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

// Owns CLI workspace publication and applying its result to the acceptance transaction.
internal static class CliWorkspaceMerge
{
    internal static (AcceptanceMergeCommitResult Result, GoalWorktreeMergeResult? Merge) FinalizeAcceptance(
        CliExecutionContext context, Goal goal, string? worktreePath, string? testedWorktreeHead,
        AcceptanceMergeGuardSnapshot? expectedMergeGuard, bool passingGateReceiptRecorded,
        GateReadyCandidateRevisionPair? preparedPair, CliFastForwardReceipt? recovery)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var root = context.Workspace.ExecutionDirectory;
        GoalWorktreeMergeResult? merge = null;
        var result = context.FinalizeAcceptanceMerge(new AcceptanceMergeCommitRequest(
            goal.Id,
            expectedMergeGuard ?? AcceptanceMergeGuard.Capture(context.Kernel, goal.Id),
            testedWorktreeHead,
            passingGateReceiptRecorded,
            Merge,
            "Acceptance completed goal after merge; cleanup deferred to conductor sweep."));
        timer.Stop();
        context.PhaseTimings.Record("workspace-merge", timer.Elapsed,
            ("goal", goal.Id.Value[..8]), ("fastForwarded", result.FastForwarded),
            ("guardAborted", result.GuardAborted), ("message", result.Message));
        return (result, merge);

        AcceptanceMergeCommitResult Merge()
        {
            if (recovery is not null && !MatchesFastForwardCandidate(context, goal, worktreePath, recovery))
            {
                const string reason = "The candidate changed while CLI checkout synchronization was pending.";
                return new(false, reason, new(AcceptanceMergeGuardMismatchKind.BaselineRepair,
                    "CLI fast-forward recovery", recovery.PromotedRevision, reason));
            }

            var pendingRollback = recovery is null
                ? GoalRollbackPlanner.CapturePendingAcceptance(root, goal.Id, context.Workspace.IntegrationBranch)
                : new GoalRollbackAcceptanceRange(goal.Id.Value, goal.Id.Value[..8], context.Worktrees.BranchName(goal.Id),
                    recovery.PriorRevision, recovery.PromotedRevision, recovery.PriorRevision, recovery.PublishedAt);
            string? repairBlockReason = null;
            GoalPromotionFact? publication;
            merge = recovery is not null
                ? FinishFastForward(context, goal, recovery, null, out publication)
                : MergeWithPublication(context, goal, out publication,
                    () => PostLandingCanaryFactory.BuildMutationBlockReason(context.Workspace),
                    out repairBlockReason, preparedPair, fromAcceptance: true);
            if (repairBlockReason is not null)
                return new(false, repairBlockReason, new(AcceptanceMergeGuardMismatchKind.BaselineRepair,
                    "Baseline repair admission", "current validation and normal acceptance", repairBlockReason));
            if (merge is null) return new(true, null);
            if (merge.FastForwarded && pendingRollback is not null)
                GoalRollbackPlanner.RecordAcceptance(root, pendingRollback);
            return new(merge.FastForwarded, FormatResult(merge), Publication: publication,
                CheckoutPending: !merge.FastForwarded && merge.FastForwardReceipt is not null);
        }
    }

    internal static bool MatchesFastForwardCandidate(
        CliExecutionContext context, Goal goal, string? worktreePath, CliFastForwardReceipt receipt) =>
        CliCommandHandlers.TryResolveGitHead(context, worktreePath) == receipt.PromotedRevision &&
        ReadRef(context.Workspace.ExecutionDirectory, $"refs/heads/{context.Worktrees.BranchName(goal.Id)}") ==
            receipt.PromotedRevision;

    private static string FormatResult(GoalWorktreeMergeResult merge) =>
        merge.FastForwarded || merge.FastForwardReceipt is not null
            ? merge.Message
            : $"{merge.Message} command: {merge.SuggestedCommand}";

    internal static bool CompleteWorkspaceMerge(CliExecutionContext context, Goal goal,
        GoalWorktreeMergeResult? merge, GoalPromotionFact? publication)
    {
        Console.WriteLine(merge is null ? "Goal has no workspace branch to merge." : FormatResult(merge));
        if (merge?.FastForwarded == true)
        {
            var mergeChangedFiles = merge.ChangedFiles
                ?? throw new InvalidOperationException(
                    "Workspace merge advanced main without an authoritative changed-file receipt.");
            RunPostLandingCanary(context, goal, mergeChangedFiles, merge.FastForwardReceipt);
            if (merge.FastForwardReceipt is { } receipt)
                GoalWorktrees.AcknowledgeCliFastForward(context.Workspace.ExecutionDirectory, goal.Id, receipt);
        }
        if (merge is { FastForwarded: false, FastForwardReceipt: not null })
            Console.WriteLine("Resolve the checkout issue in the repository root, then rerun workspace merge.");
        return merge?.FastForwarded == true && publication is not null;
    }

    internal static void RunPostLandingCanary(CliExecutionContext context, Goal goal,
        IReadOnlyList<string> changedFiles, CliFastForwardReceipt? receipt)
    {
        var landingSha = receipt?.PromotedRevision
            ?? CliCommandHandlers.TryResolveGitHead(context, context.Workspace.ExecutionDirectory);
        var landing = new ConductorLandingReceipt(goal.Id.Value, changedFiles, landingSha);
        var disposition = context.PostLandingCanary?.HandleLanding(landing)
            ?? PostLandingCanaryFactory.HandleLandingAfterMainAdvanced(context.Workspace, landing, Console.WriteLine);
        if (receipt is not null && disposition == PostLandingCanaryDisposition.Deferred)
        {
            var events = new SqliteRunEventStore(context.Workspace.RunEventStorePath, ensureSchema: false);
            if (events.ReadByEventIdAsync(PostLandingCanaryEventIds.Queued(receipt.PromotedRevision))
                .GetAwaiter().GetResult() is null)
                throw new IOException($"Post-landing canary for {receipt.PromotedRevision} was not durably queued. Rerun the landing command.");
        }
    }

    // Capture publication at the successful CLI merge boundary. A later HEAD or
    // recovery clock cannot establish when the original duty became due.
    internal static GoalWorktreeMergeResult? MergeWithPublication(
        CliExecutionContext context, Goal goal, out GoalPromotionFact? publication,
        Func<string?> mutationBlocker, out string? repairBlockReason,
        GateReadyCandidateRevisionPair? testedPair = null, bool fromAcceptance = false)
    {
        publication = null;
        repairBlockReason = null;
        var root = context.Workspace.ExecutionDirectory;
        if (GoalWorktrees.ReadCliFastForward(root, goal.Id) is { } pending)
        {
            if (fromAcceptance || pending.FromAcceptance)
            {
                repairBlockReason = "An earlier CLI fast-forward requires reconciliation. Rerun acceptance.";
                return new(false, context.Worktrees.BranchName(goal.Id), repairBlockReason, null);
            }
            return FinishFastForward(context, goal, pending, null, out publication);
        }

        var duties = goal.OutstandingPostLandingDuties;
        var branch = context.Worktrees.BranchName(goal.Id);
        var mainRef = $"refs/heads/{context.Workspace.IntegrationBranch}";
        var target = RootCheckoutLandingGuard.ReadSymbolicHead(static (dir, args) => GitCli.Run(dir, args), root);
        var canonicalRepair = OpenRepairStore(context.Workspace)?.IsCanonicalRepair(goal.Id.Value) == true;
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
            ? GoalWorktrees.TryAdvanceFastForwardRefs(root, goal.Id, target, priorMain, candidate, CheckMutation,
                context.DiagnosticsClock ?? new SystemClock(), fromAcceptance)
            : context.Worktrees.TryFastForwardMerge(root, goal.Id, CheckMutation);
        repairBlockReason = merge?.RevisionPairChanged == true ? merge.Message : repairBlock;
        if (merge?.FastForwardReceipt is { } receipt)
            return FinishFastForward(context, goal, receipt, merge.ChangedFiles, out publication);
        if (merge?.FastForwarded == true && requiresIntegrationTarget)
        {
            // An already-merged branch may recover a known fact, but never invent one.
            publication = new SqliteOrchestratorStateRepository(context.Workspace.SqliteStatePath)
                .ReadPromotionFactAsync(goal.Id).GetAwaiter().GetResult();
            if (publication is not null && duties.Count > 0)
                context.Kernel.BindPostLandingPublication(goal.Id, publication);
        }
        return merge;

        string? CheckMutation()
        {
            var blocked = mutationBlocker();
            if (!string.IsNullOrWhiteSpace(blocked)) return blocked;
            return repairBlock = CheckRepairAdmission(context, goal, pair, requireNormalAcceptance: true, out _);
        }
    }

    internal static GoalWorktreeMergeResult FinishFastForward(
        CliExecutionContext context, Goal goal, CliFastForwardReceipt receipt,
        IReadOnlyList<string>? changedFiles, out GoalPromotionFact? publication)
    {
        var workspace = context.Workspace;
        var duties = goal.OutstandingPostLandingDuties;
        publication = null;
        if (receipt.TargetRef == $"refs/heads/{workspace.IntegrationBranch}" &&
            (duties.Count > 0 || OpenRepairStore(workspace)?.IsCanonicalRepair(goal.Id.Value) == true))
        {
            var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            publication = repository.ReadPromotionFactAsync(goal.Id).GetAwaiter().GetResult();
            if (publication is null)
            {
                publication = new GoalPromotionFact(goal.Id.Value, receipt.PromotedRevision, receipt.PriorRevision,
                    receipt.PromotedRevision, receipt.PublishedAt, GoalPromotionFact.CliFastForwardProvenance);
                repository.RecordPromotionFacts([publication]);
            }
        }
        var merge = GoalWorktrees.SynchronizeCliFastForward(workspace.ExecutionDirectory, goal.Id, receipt, changedFiles);
        if (merge.FastForwarded && publication is not null && duties.Count > 0)
            context.Kernel.BindPostLandingPublication(goal.Id, publication);
        return merge;
    }

    private static SoloBaselineRepairStore? OpenRepairStore(OrchestratorWorkspace workspace, bool retain = false)
    {
        var path = Path.Combine(workspace.OrchestratorDirectory, SoloBaselineRepairStore.FileName);
        return retain || File.Exists(path) ? new SoloBaselineRepairStore(path) : null;
    }

    internal static void RetainRepairEvidence(CliExecutionContext context, Goal goal,
        AcceptanceVerificationSummary? completion = null)
    {
        var workspace = context.Workspace;
        if (goal.LatestAcceptanceFailure?.SoloFailureReceipt is null)
        {
            if (completion?.RequiresSoloFailureReceipt == true &&
                SoloAcceptanceFailureReceiptReader.ReadRepairPartition(goal, null, completion) is { } unresolved)
                OpenRepairStore(workspace, retain: true)!.Retain(goal.Id.Value, unresolved.Findings);
            return;
        }

        var root = workspace.ExecutionDirectory;
        GateReadyCandidateRevisionPair ReadCurrent() => new(ReadRef(root, $"refs/heads/{context.Worktrees.BranchName(goal.Id)}"),
            ReadRef(root, $"refs/heads/{workspace.IntegrationBranch}"));
        SoloBaselineRepairCoordinator.RetainSoloRepairPartition(OpenRepairStore(workspace, retain: true)!, goal,
            () => SoloAcceptanceFailureHandler.ReadContext(goal, root, ReadCurrent()), ReadCurrent, completion);
    }

    internal static void RetainVerificationFailure(
        CliExecutionContext context, Goal goal, IReadOnlyList<string> failedChecks,
        string? testedWorktreeHead, string? testedMainHead, AcceptanceVerificationResult? verification)
    {
        var baseline = CleanTestBaseline.Unattested(testedMainHead);
        var attributions = verification is { Passed: false }
            ? CleanTestBaseline.Attribute(baseline, failedChecks, [], goal.Id, testedMainHead ?? "", verification.Checks)
            : null;
        var attestation = attributions is null ? null : CleanTestBaseline.FormatFailureAttestation(
            CleanTestBaseline.WithExecutedBaselineAttestation(baseline, failedChecks, goal.Id, verification?.Checks));
        context.Kernel.RecordAcceptanceFailure(goal.Id, failedChecks, testedWorktreeHead, testedMainHead,
            attributions, attestation, verification?.AttemptId, verification?.SoloFailureReceipt);
        if (verification is { Passed: false } &&
            (verification.RequiresSoloFailureReceipt || verification.SoloFailureReceipt is not null))
        {
            var completion = AcceptanceVerificationSummary.FromVerification(verification, verification.Checks ?? [],
                failedChecks, testedWorktreeHead, testedMainHead, verification.TestResultPaths, attributions, attestation);
            RetainRepairEvidence(context, goal, completion);
        }
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

    internal static GoalOperationJournalEntry? FindRecoverableNormalGate(
        string root, Goal goal, GateReadyCandidateRevisionPair pair)
    {
        if (pair is not { BranchRevision: { } candidate, MainRevision: { } main }) return null;
        var journal = GoalOperationJournal.ReadActive(root, goal.Id);
        var latest = GoalOperationJournal.NewestAcceptanceOutcomeForCandidate(journal, candidate, main);
        return latest is { Operation: "acceptance", Status: GoalOperationStatus.Completed } &&
               latest.GoalId == goal.Id && GoalOperationJournalGatePassedOutcome.Matches(latest)
            ? latest
            : null;
    }

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
        else if (result.Message is not null && !result.CheckoutPending)
        {
            kernel.RecordAcceptanceFailure(request.GoalId, ["merge"]);
        }
    }
}
