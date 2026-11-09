using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// A member run owns an isolated partition and fresh build output, never a train identity.
internal static class MergeTrainMemberRecheckRunner
{
    internal delegate MergeTrainFocusedRecheckRunner.RunResult? Runner(MergeTrainMemberBinding member,
        string observedMainRevision, ConductorCohortFocusedSelection selection, CancellationToken token);

    internal static Runner Create(IGoalAcceptanceVerifier verifier, OrchestratorWorkspace workspace,
        GoalWorktreeCleanupHooks cleanupHooks, Func<string, CancellationToken, DotnetBuildEnvironmentLease> acquireLease) =>
        (member, main, selection, token) =>
        {
            using var partition = GoalWorktrees.CreateAcceptancePartitionWorkspace(workspace.ExecutionDirectory, main,
                new(member.GoalId, member.BranchRevision, member.CandidateRevision, member.LandingPaths,
                    member.ResourceKeys, member.ChangeRiskTier, member.AutoPromotionDisposition,
                    member.MergeStatus, member.MergeReason, member.BriefVersion),
                workspace.IntegrationBranch, cleanupHooks, requireCurrentMain: false);
            var runId = "train-member-recheck-" + Guid.NewGuid().ToString("N");
            using var lease = acquireLease(runId, token);
            using var build = new MergeTrainReproductionBuildScope(lease.Environment);
            var tree = MergeTrainReproductionTree.Read(partition.Path);
            if (tree != partition.TreeRevision) return null;
            var result = AcceptanceExecutionRunner.RunFocusedVerification(verifier, partition.Path, member.GoalId,
                selection.Request, lease.Environment.BuildPermitIndex, lease, runBaselineArm: false, token,
                integrationBranch: workspace.IntegrationBranch, projectHomeDirectory: workspace.ProjectHomeDirectoryOrNull,
                focusedRunBinding: new(build.Environment, runId));
            partition.AssertGoalBranchesUnchanged();
            var interpreted = ConductorAcceptanceCohortFocusedAttribution.Interpret(result, selection) with { TreeRevision = tree };
            if (result.OutcomeReason is FindingEvidenceOutcomeReason.CandidateInconclusive or FindingEvidenceOutcomeReason.ApparatusFailure)
                interpreted = interpreted with { Kind = ConductorCohortFocusedPassKind.InfrastructureFailure };
            var provenance = new MergeTrainReproductionProvenance(main, member.CandidateRevision, partition.TreeRevision, tree,
                build.Environment.ArtifactsPath, result.Checks.Select(check => check.ArtifactsPath ?? "").ToArray(),
                AcceptanceTrxCodeBaseReader.Read(interpreted.TestResultPaths));
            if (MergeTrainReproductionTree.Read(partition.Path) != tree ||
                MergeTrainReproductionEvidenceCheck.InvalidReason(provenance, member, main) is not null) return null;
            // The decision reads these files after this scope has disposed its build output.
            var evidenceRoot = Path.Combine(workspace.OrchestratorDirectory, "acceptance-gate-attempts", runId);
            Directory.CreateDirectory(evidenceRoot);
            var paths = interpreted.TestResultPaths.Select((source, index) =>
            {
                var destination = Path.Combine(evidenceRoot, $"member-{index}.trx");
                File.Copy(source, destination, overwrite: false);
                return destination;
            }).ToArray();
            return new(runId, main, interpreted with { TestResultPaths = paths },
                result.Arms?.Sum(arm => arm.NotRunSelections?.Count ?? 0) ?? 0, ReadCpuMilliseconds(result.Checks));
        };

    internal static long? ReadCpuMilliseconds(IReadOnlyList<AcceptanceCheckResult> checks)
    {
        if (checks.Count == 0) return null;
        long total = 0;
        foreach (var check in checks)
        {
            var cpu = GateLaneReceipt.FromSummary(check.Name, 0, TimeSpan.Zero, check.ResultSummary,
                GateLoadSample.Unavailable("not-sampled")).CpuMilliseconds;
            if (!cpu.IsAvailable || cpu.Value is not { } value || value < 0 || value >= long.MaxValue - total) return null;
            total += (long)value;
        }
        return total;
    }
}
