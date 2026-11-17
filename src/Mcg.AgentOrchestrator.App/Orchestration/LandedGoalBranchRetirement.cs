using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class LandedGoalBranchRetirement
{
    public const string RepairKind = "completed-branch-landed-retired";

    internal sealed record Proof(string ReceiptKind, string ReceiptId, string CandidateRevision, string LandingCommit)
    {
        public string Describe(Goal goal, string cleanup) =>
            $"completed goal branch {GoalWorktrees.BranchName(goal.Id)} is not merged by ancestry; landed receipt={ReceiptKind}:{ReceiptId}; candidateRevision={CandidateRevision}; landingCommit={LandingCommit}; action=retire; cleanup={cleanup}";
    }

    public static string? TryGetLandedBranchTip(
        Goal goal,
        GoalBranchFacts branchFacts,
        string? branchTip,
        string? mainSha,
        GoalOperationJournalSummary journal,
        MergeTrainAcceptanceStore mergeTrainStore,
        CohortAcceptanceStore cohortStore,
        string executionDirectory,
        Func<string, IReadOnlyList<string>, GitCli.GitResult> gitRunner,
        out Proof? proof,
        GoalAncestryCache? ancestryCache = null)
        => TryGetLandedBranchTip(goal, branchFacts, branchTip, mainSha, journal, mergeTrainStore,
            cohortStore, executionDirectory, gitRunner, out proof, out _, ancestryCache);

    public static string? TryGetLandedBranchTip(
        Goal goal,
        GoalBranchFacts branchFacts,
        string? branchTip,
        string? mainSha,
        GoalOperationJournalSummary journal,
        MergeTrainAcceptanceStore mergeTrainStore,
        CohortAcceptanceStore cohortStore,
        string executionDirectory,
        Func<string, IReadOnlyList<string>, GitCli.GitResult> gitRunner,
        out Proof? proof,
        out string? failingCheck,
        GoalAncestryCache? ancestryCache = null)
    {
        proof = null;
        failingCheck = null;
        if (goal.Status is not (GoalStatus.Completed or GoalStatus.Verified))
        {
            failingCheck = "status";
            return null;
        }
        if (!branchFacts.HasGoalBranch)
        {
            failingCheck = "missing-branch";
            return null;
        }
        if (branchFacts.BranchAlreadyLanded)
        {
            failingCheck = "branch-already-landed";
            return null;
        }
        // Completed equivalent branches use the sweep's existing content retirement path.
        // Verified branches still need the durable landing proof below.
        if (goal.Status == GoalStatus.Completed && branchFacts.ContentState == GoalBranchContentState.EquivalentToMain)
        {
            failingCheck = "content-equivalent";
            return null;
        }
        if (string.IsNullOrWhiteSpace(branchTip))
        {
            failingCheck = "missing-tip";
            return null;
        }
        if (string.IsNullOrWhiteSpace(mainSha))
        {
            failingCheck = "missing-main";
            return null;
        }
        if (GoalOperationJournal.TryGetLatestLandingIntent(journal) is not { } intent)
        {
            failingCheck = "missing-landing-intent";
            return null;
        }

        var finalized = mergeTrainStore.ReadFinalizedLandingsForGoal(goal.Id)
            .Concat(cohortStore.ReadFinalizedLandingsForGoal(goal.Id))
            .Where(landing => string.Equals(landing.LandingCommit, intent.MergeCommitSha, StringComparison.OrdinalIgnoreCase))
            .OrderBy(landing => landing.ReceiptKind, StringComparer.Ordinal)
            .ThenBy(landing => landing.ReceiptId, StringComparer.Ordinal)
            .ToArray();
        bool? isAncestor = null;
        if (finalized.Length > 0)
        {
            isAncestor = AcceptanceCriterionEvidenceRecovery.IsAncestor(
                executionDirectory, intent.MergeCommitSha, mainSha, gitRunner, ancestryCache);
            if (!isAncestor.Value)
            {
                failingCheck = "landing-commit-not-on-main";
                return null;
            }
            var landing = finalized.FirstOrDefault(item =>
                string.Equals(item.CandidateRevision, branchTip, StringComparison.OrdinalIgnoreCase));
            if (landing is not null)
            {
                proof = new Proof(landing.ReceiptKind, landing.ReceiptId, branchTip, intent.MergeCommitSha);
                return branchTip;
            }
        }

        var train = mergeTrainStore.ReadPassedReceiptsForGoal(goal.Id)
            .OrderBy(receipt => receipt.ReceiptId, StringComparer.Ordinal)
            .FirstOrDefault(receipt => receipt.Identity.Members.Any(member =>
                member.GoalId == goal.Id && string.Equals(member.CandidateRevision, branchTip, StringComparison.OrdinalIgnoreCase)));
        if (train is not null)
            proof = new Proof("merge-train-receipt", train.ReceiptId, branchTip, intent.MergeCommitSha);
        else
        {
            var cohort = cohortStore.ReadPassedReceiptsForGoal(goal.Id)
                .OrderBy(receipt => receipt.ReceiptId, StringComparer.Ordinal)
                .FirstOrDefault(receipt => receipt.Identity.Members.Any(member =>
                    member.GoalId == goal.Id && string.Equals(member.CandidateRevision, branchTip, StringComparison.OrdinalIgnoreCase)));
            if (cohort is not null)
                proof = new Proof("cohort-receipt", cohort.ReceiptId, branchTip, intent.MergeCommitSha);
        }

        if (proof is null)
        {
            failingCheck = "no-matching-receipt";
            return null;
        }
        if (isAncestor ?? AcceptanceCriterionEvidenceRecovery.IsAncestor(
            executionDirectory, intent.MergeCommitSha, mainSha, gitRunner, ancestryCache))
            return branchTip;

        proof = null;
        failingCheck = "landing-commit-not-on-main";
        return null;
    }
}
