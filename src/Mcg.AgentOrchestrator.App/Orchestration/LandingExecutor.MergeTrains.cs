using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static partial class LandingExecutor
{
    internal static AcceptanceCohortLandingResult ExecuteMergeTrain(
        AgentOrchestratorKernel kernel,
        IReadOnlyList<Goal> goals,
        OrchestratorWorkspace workspace,
        MergeTrainReceipt receipt,
        string trainCommitRevision,
        MergeTrainAcceptanceStore store,
        ConductorAutonomyPolicy policy,
        IGoalLifecycleEventWriter? eventWriter = null,
        Func<string?>? mutationBlocker = null,
        IClock? promotionClock = null)
    {
        ArgumentNullException.ThrowIfNull(goals);
        ArgumentNullException.ThrowIfNull(receipt);
        if (goals.Count is < 2 or > 3 ||
            goals.Select(goal => goal.Id).Distinct().Count() != goals.Count ||
            goals.Count != receipt.Identity.Members.Count)
        {
            throw new ArgumentException("Merge train landing requires the exact two or three distinct tested goals.", nameof(goals));
        }
        if (!receipt.HasAuthoritativeLandingEvidence)
        {
            throw new InvalidOperationException("Only an exact passing merge train receipt can authorize landing.");
        }
        if (MergeTrainFocusedRecheckReceiptAcceptance.IsComposite(receipt) &&
            !MergeTrainFocusedRecheckReceiptAcceptance.Accepts(receipt, store))
            throw new InvalidOperationException("Merge train focused recheck receipt has invalid composite evidence.");

        var executionDirectory = workspace.ExecutionDirectory;
        var blockReason = mutationBlocker?.Invoke();
        if (!string.IsNullOrWhiteSpace(blockReason))
        {
            return new AcceptanceCohortLandingResult(
                AcceptanceCohortLandingOutcome.RetryableHold,
                $"Merge train landing held at mutation boundary: {blockReason}");
        }
        var liveMain = GoalWorktrees.ResolveRequiredRef(executionDirectory, $"refs/heads/{workspace.IntegrationBranch}");
        if (!liveMain.Equals(receipt.Identity.ObservedMainRevision, StringComparison.Ordinal))
        {
            return new AcceptanceCohortLandingResult(
                AcceptanceCohortLandingOutcome.StateInvalidated,
                "Merge train receipt invalidated because main changed after the gate.");
        }
        var commit = MergeTrainMemberBinding.NormalizeRevision(trainCommitRevision, nameof(trainCommitRevision));
        var tree = GoalWorktrees.ResolveRequiredRef(executionDirectory, $"{commit}^{{tree}}");
        if (!tree.Equals(receipt.Identity.TrainTreeRevision, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Merge train landing commit does not contain the exact tested tree.");
        }

        for (var index = 0; index < goals.Count; index++)
        {
            var member = receipt.Identity.Members[index];
            if (goals[index].Id != member.GoalId)
            {
                throw new InvalidOperationException("Merge train goal order differs from the tested receipt.");
            }
            var liveBranch = GoalWorktrees.ResolveRequiredRef(
                executionDirectory,
                $"refs/heads/{GoalWorktrees.BranchName(member.GoalId)}");
            if (!liveBranch.Equals(member.BranchRevision, StringComparison.Ordinal) ||
                !liveBranch.Equals(member.CandidateRevision, StringComparison.Ordinal))
            {
                return new AcceptanceCohortLandingResult(
                    AcceptanceCohortLandingOutcome.StateInvalidated,
                    $"Merge train receipt invalidated because goal {member.GoalId.Value[..8]} changed after the gate.");
            }
        }

        var evidenceGaps = new List<KeyValuePair<GoalId, string>>();
        foreach (var goal in goals)
        {
            var pendingInput = EvaluatePendingHumanInput(kernel, goal, workspace,
                receipt.Identity.Members.Single(member => member.GoalId == goal.Id).CandidateRevision).HoldDescription;
            if (pendingInput is not null)
                evidenceGaps.Add(new(goal.Id, pendingInput));
        }

        foreach (var goal in goals)
        {
            var gap = AcceptanceCriterionEvidence.DescribeTrainOperatorEvidenceGap(goal);
            if (gap is not null)
            {
                evidenceGaps.Add(new(goal.Id, $"goal {goal.Id.Value[..8]}: {gap}"));
            }
        }

        foreach (var goal in goals)
        {
            var evidenceDiagnostic = AcceptanceCriterionEvidence.RebindRecordAndDescribeOutstanding(
                goal,
                receipt.Identity.Members.Single(member => member.GoalId == goal.Id).CandidateRevision,
                kernel,
                $"merge-train-receipt:{receipt.ReceiptId}", workspace.IntegrationBranch,
                executionDirectory);
            if (evidenceDiagnostic is not null)
                evidenceGaps.Add(new(goal.Id, $"goal {goal.Id.Value[..8]}: {evidenceDiagnostic}"));
        }

        foreach (var goal in goals)
        {
            var completionRefusal = kernel.DescribeMergeEvidenceCompletionRefusal(goal.Id);
            if (completionRefusal is not null)
            {
                evidenceGaps.Add(new(goal.Id, completionRefusal));
            }
        }

        var changedFiles = receipt.Identity.Members
            .SelectMany(member => member.LandingPaths)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var ownership = RepositoryOwnershipMap.GuardWriteSet(changedFiles);
        if (ownership.RequiresOperatorApproval && !policy.AllowsAutonomousHighRiskOwnership)
        {
            return new AcceptanceCohortLandingResult(
                AcceptanceCohortLandingOutcome.RetryableHold,
                "Merge train landing requires operator approval for ownership-protected paths.");
        }

        var landingDenylist = LoadLandingDenylist(executionDirectory, liveMain);
        var decisions = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        var denylistApprovals = receipt.Identity.Members.ToDictionary(member => member.GoalId,
            member => LandingDenylistOwnerGate.Evaluate(executionDirectory, landingDenylist.Denylist,
                decisions, member.GoalId.Value, liveMain, member.CandidateRevision, member.LandingPaths));
        var denylistHolds = goals.Where(goal => !denylistApprovals[goal.Id].Approved).ToArray();
        foreach (var goal in denylistHolds)
            OperatorInbox.RecordLandingEscalation(workspace, goal, denylistApprovals[goal.Id].HoldReason, IntegrationBranchName);
        if (denylistHolds.Length > 0)
            return new AcceptanceCohortLandingResult(AcceptanceCohortLandingOutcome.RetryableHold,
                string.Join("; ", denylistHolds.Select(goal => denylistApprovals[goal.Id].HoldReason)));

        var currentIntegration = RunGit(
            executionDirectory,
            "rev-parse", "--verify", "--quiet", $"refs/heads/{IntegrationBranchName}");
        string? priorIntegrationRevision = null;
        if (currentIntegration.ExitCode == 0 && !string.IsNullOrWhiteSpace(currentIntegration.Output))
        {
            priorIntegrationRevision = currentIntegration.Output.Trim();
            if (RunGit(executionDirectory, "merge-base", "--is-ancestor", priorIntegrationRevision, liveMain).ExitCode != 0)
            {
                return new AcceptanceCohortLandingResult(
                    AcceptanceCohortLandingOutcome.RetryableHold,
                    "Merge train landing held because integration contains state not present on bound main.");
            }
        }

        var checkoutInspection = RootCheckoutLandingGuard.Inspect(
            GitRunner, executionDirectory, workspace.IntegrationBranch, liveMain, commit);
        if (checkoutInspection.Outcome == RootCheckoutLandingGuard.Outcome.Hold)
        {
            foreach (var goal in goals)
                OperatorInbox.RecordLandingEscalation(workspace, goal, checkoutInspection.Reason!, IntegrationBranchName);
            return new AcceptanceCohortLandingResult(
                AcceptanceCohortLandingOutcome.RetryableHold, $"Merge train landing held: {checkoutInspection.Reason}");
        }

        // Whole-train blockers take precedence over actionable member attribution.
        if (evidenceGaps.Count > 0)
            return new AcceptanceCohortLandingResult(AcceptanceCohortLandingOutcome.RetryableHold,
                $"Merge train landing held before mutation because {string.Join("; ", evidenceGaps.Select(item => item.Value))}",
                EvidenceGaps: new MergeTrainLandingEvidenceGaps(evidenceGaps));

        store.PrepareLanding(receipt, commit, priorIntegrationRevision);
        foreach (var goal in goals)
        {
            GoalOperationJournal.RecordLandingIntent(
                executionDirectory,
                goal,
                GoalWorktrees.BranchName(goal.Id),
                $"train/{receipt.Identity.Value}",
                commit,
                "LandingExecutor.ExecuteMergeTrain",
                boundMainRevision: liveMain,
                previousIntegrationRevision: priorIntegrationRevision);
        }

        blockReason = mutationBlocker?.Invoke();
        if (string.IsNullOrWhiteSpace(blockReason))
            blockReason = DescribeGroupedPendingHumanInput(kernel, goals, workspace,
                goalId => receipt.Identity.Members.Single(member => member.GoalId == goalId).CandidateRevision);
        if (!string.IsNullOrWhiteSpace(blockReason))
        {
            foreach (var goal in goals)
            {
                GoalOperationJournal.TombstoneLandingIntent(
                    executionDirectory,
                    goal,
                    $"merge train landing mutation blocked after intent write: {blockReason}");
            }
            return new AcceptanceCohortLandingResult(
                AcceptanceCohortLandingOutcome.RetryableHold,
                $"Merge train landing held at mutation boundary: {blockReason}");
        }

        var integrationUpdate = priorIntegrationRevision is null
            ? RunGit(executionDirectory, "update-ref", $"refs/heads/{IntegrationBranchName}", commit)
            : RunGit(executionDirectory, "update-ref", $"refs/heads/{IntegrationBranchName}", commit, priorIntegrationRevision);
        if (integrationUpdate.ExitCode != 0)
        {
            foreach (var goal in goals)
            {
                GoalOperationJournal.TombstoneLandingIntent(
                    executionDirectory,
                    goal,
                    $"merge train integration ref update failed: {integrationUpdate.Error}");
            }
            return new AcceptanceCohortLandingResult(
                AcceptanceCohortLandingOutcome.RetryableHold,
                $"Merge train integration ref update failed: {integrationUpdate.Error}");
        }

        var mainUpdate = RunGit(executionDirectory, "update-ref", $"refs/heads/{workspace.IntegrationBranch}", commit, receipt.Identity.ObservedMainRevision);
        if (mainUpdate.ExitCode != 0)
        {
            _ = priorIntegrationRevision is null
                ? RunGit(executionDirectory, "update-ref", "-d", $"refs/heads/{IntegrationBranchName}", commit)
                : RunGit(executionDirectory, "update-ref", $"refs/heads/{IntegrationBranchName}", priorIntegrationRevision, commit);
            foreach (var goal in goals)
            {
                GoalOperationJournal.TombstoneLandingIntent(
                    executionDirectory,
                    goal,
                    $"merge train main compare-and-swap failed: {mainUpdate.Error}");
            }
            return new AcceptanceCohortLandingResult(
                AcceptanceCohortLandingOutcome.StateInvalidated,
                $"Merge train receipt invalidated at the main compare-and-swap boundary: {mainUpdate.Error}");
        }
        string? checkoutFailure = null;
        if (checkoutInspection.Outcome == RootCheckoutLandingGuard.Outcome.FastForward &&
            RootCheckoutLandingGuard.IsOnBranch(GitRunner, executionDirectory, workspace.IntegrationBranch))
        {
            var checkout = RunGit(executionDirectory, "read-tree", "-m", "-u", receipt.Identity.ObservedMainRevision, commit);
            if (checkout.ExitCode != 0)
            {
                checkoutFailure = $"Main advanced to the exact merge train commit, but checkout refresh failed: {checkout.Error}";
            }
        }

        RecordSuccessfulPromotion(workspace, kernel,
            receipt.Identity.Members.Select(member => (member.GoalId, member.CandidateRevision)).ToArray(),
            receipt.Identity.ObservedMainRevision, commit, GoalPromotionFact.MergeTrainProvenance, promotionClock);
        if (checkoutFailure is not null) throw new InvalidOperationException(checkoutFailure);

        var coverage = store.FinalizeLanding(receipt.Identity.Value, receipt.ReceiptId);
        foreach (var goal in goals)
        {
            GoalOperationJournal.Completed(
                executionDirectory,
                goal,
                "conductor:land",
                $"Merge train receipt {receipt.ReceiptId} landed exact tree {receipt.Identity.TrainTreeRevision}.");
            if (eventWriter is not null)
                eventWriter.AppendGoalLanded(
                    goal.Id,
                    $"train/{receipt.Identity.Value}",
                    GoalWorktrees.BranchName(goal.Id), BuildLandingAdmission(landingDenylist,
                        receipt.Identity.Members.Single(member => member.GoalId == goal.Id).CandidateRevision,
                        denylistApprovals[goal.Id]));
            else
                kernel.RecordGoalLandedFromMergeEvidence(
                    goal.Id,
                    GoalWorktrees.BranchName(goal.Id),
                    commit,
                    commit);
            kernel.CompleteGoalFromMergeEvidence(
                goal.Id,
                commit,
                $"Landed by merge train receipt {receipt.ReceiptId}.");
            StateEffectProposalApplier.ApplyLandedProposals(kernel, goal, workspace, changedFiles, Console.WriteLine);
        }
        return new AcceptanceCohortLandingResult(
            AcceptanceCohortLandingOutcome.Advanced,
            $"Landed exact tested merge train tree for {string.Join(',', goals.Select(goal => goal.Id.Value[..8]))}.",
            commit,
            coverage.Select(item => new AcceptanceCohortCoverage(item.GoalId, item.TrainId, item.ReceiptId, item.Landed)).ToArray());
    }
}

// Maps a train landing to member results and completes its post-landing effects.
// Evidence attribution, ejection and the decision to re-gate remain with the conductor.
internal static class MergeTrainLandingCompletion
{
    internal static Dictionary<string, ConductorAdvanceResult> Hold(
        IReadOnlyList<Goal> goals, ConductorAutonomyPolicy policy, string detail) =>
        goals.ToDictionary(
            goal => goal.Id.Value,
            goal => new ConductorAdvanceResult(goal.Id.Value, goal.Id.Value[..8], policy.Name,
                new ConductorAdvanceOutcome.Held(GoalLifecycleState.Verified, detail)),
            StringComparer.Ordinal);

    internal static ConductorMergeTrainRunResult Complete(
        MergeTrainReceipt receipt, AcceptanceCohortLandingResult landing, IReadOnlyList<Goal> goals,
        IReadOnlyList<string> changedFiles, MergeTrainAcceptanceStore store, ConductorAutonomyPolicy policy,
        int attempt, IReadOnlyList<MergeTrainEjection> ejections, Action<ConductorLandingReceipt>? landingSink,
        Action<Goal, LandingResult> afterSuccessfulLanding)
    {
        if (!landing.MainAdvanced)
            throw new InvalidOperationException("Merge train landing completion requires an advanced main.");
        foreach (var goal in goals)
        {
            var result = new LandingResult(
                goal.Id.Value,
                goal.Id.Value[..8],
                new LandingDecision.Promote(),
                $"train/{receipt.Identity.Value}",
                true,
                landing.Message,
                landing.CommitRevision,
                changedFiles);
            landingSink?.Invoke(new ConductorLandingReceipt(goal.Id.Value, changedFiles, landing.CommitRevision,
                GateLanePlanPath: receipt.GateLanePlanPath, PreviousMainSha: receipt.Identity.ObservedMainRevision));
            afterSuccessfulLanding(goal, result);
        }
        store.CompleteLandingEffects(receipt.Identity.Value, receipt.ReceiptId);
        return new ConductorMergeTrainRunResult(
            receipt,
            goals.ToDictionary(
                goal => goal.Id.Value,
                goal => new ConductorAdvanceResult(goal.Id.Value, goal.Id.Value[..8], policy.Name,
                    new ConductorAdvanceOutcome.Executed(
                        GoalLifecycleState.Verified,
                        $"Landed by merge train receipt {receipt.ReceiptId}.")),
                StringComparer.Ordinal),
            ejections,
            $"outcome=passed attempts={attempt + 1} landings={goals.Count} receipt={receipt.ReceiptId}", landing.Outcome);
    }
}
