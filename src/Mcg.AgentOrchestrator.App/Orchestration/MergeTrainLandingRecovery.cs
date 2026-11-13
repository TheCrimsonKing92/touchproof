using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Restores landed effects only after every member's evidence mutation lease is held.
internal static class MergeTrainLandingRecovery
{
    internal static void Recover(
        AgentOrchestratorKernel kernel,
        OrchestratorWorkspace workspace,
        IGoalLifecycleEventWriter eventWriter,
        MergeTrainAcceptanceStore store,
        string integrationBranch,
        Func<Goal, string, IDisposable?> tryAcquireEvidenceMutationLease,
        Action<ConductorLandingReceipt, string, string> recordRecoveredLanding,
        Action<Goal, LandingResult> afterSuccessfulLanding)
    {
        var goalsById = kernel.Goals.ToDictionary(goal => goal.Id);
        foreach (var recovery in store.RecoverPreparedLandings(workspace.ExecutionDirectory, workspace.IntegrationBranch))
        {
            var goals = recovery.Receipt.Identity.Members
                .Select(member => goalsById.TryGetValue(member.GoalId, out var goal) ? goal : null)
                .ToArray();
            if (goals.Any(goal => goal is null))
            {
                continue;
            }

            var resolvedGoals = goals.Cast<Goal>().ToArray();
            var evidenceMutationLeases = new Stack<IDisposable>();
            try
            {
                foreach (var goal in resolvedGoals.OrderBy(goal => goal.Id.Value, StringComparer.Ordinal))
                {
                    var lease = tryAcquireEvidenceMutationLease(goal, "conductor:merge-train-recovery");
                    if (lease is null)
                    {
                        break;
                    }
                    evidenceMutationLeases.Push(lease);
                }
                if (evidenceMutationLeases.Count != resolvedGoals.Length)
                {
                    continue;
                }

                var changedFiles = recovery.Receipt.Identity.Members
                    .SelectMany(member => member.LandingPaths)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var evidenceDiagnostic = AcceptanceCriterionEvidence.RebindRecordAndDescribeOutstanding(
                    resolvedGoals,
                    goalId => recovery.Receipt.Identity.Members.Single(member => member.GoalId == goalId).CandidateRevision,
                    kernel,
                    $"merge-train-receipt:{recovery.Receipt.ReceiptId}", integrationBranch,
                    workspace.ExecutionDirectory);
                if (evidenceDiagnostic is not null)
                {
                    Console.WriteLine($"MERGE_TRAIN_RECOVERY_HELD train={recovery.Receipt.Identity.Value} detail={evidenceDiagnostic}");
                    continue;
                }
                foreach (var goal in resolvedGoals)
                {
                    GoalOperationJournal.Completed(
                        workspace.ExecutionDirectory,
                        goal,
                        "conductor:land",
                        $"Recovered merge train receipt {recovery.Receipt.ReceiptId} after main advanced.");
                    eventWriter.AppendGoalLanded(
                        goal.Id,
                        $"train/{recovery.Receipt.Identity.Value}",
                        GoalWorktrees.BranchName(goal.Id), LandingAdmissionReceipt.Recovered(
                            recovery.Receipt.Identity.Members.Single(member => member.GoalId == goal.Id).CandidateRevision));
                    StateEffectProposalApplier.ApplyLandedProposals(
                        kernel,
                        goal,
                        workspace,
                        changedFiles,
                        Console.WriteLine);
                    var landingResult = new LandingResult(
                        goal.Id.Value,
                        goal.Id.Value[..8],
                        new LandingDecision.Promote(),
                        $"train/{recovery.Receipt.Identity.Value}",
                        MainAdvanced: true,
                        "Recovered exact tested merge train landing after main advanced.",
                        recovery.CommitRevision,
                        changedFiles);
                    recordRecoveredLanding(
                        new ConductorLandingReceipt(goal.Id.Value, changedFiles, recovery.CommitRevision,
                            GateLanePlanPath: recovery.Receipt.GateLanePlanPath, PreviousMainSha: recovery.Receipt.Identity.ObservedMainRevision),
                        recovery.Receipt.Identity.Value,
                        recovery.Receipt.ReceiptId);
                    afterSuccessfulLanding(goal, landingResult);
                }
            }
            finally
            {
                while (evidenceMutationLeases.TryPop(out var lease))
                {
                    lease.Dispose();
                }
            }
        }
    }
}
