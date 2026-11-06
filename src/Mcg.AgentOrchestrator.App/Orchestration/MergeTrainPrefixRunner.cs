using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class MergeTrainPrefixRunner
{
    internal static MergeTrainPrefixBisection.Runner Create(OrchestratorWorkspace workspace,
        IGoalAcceptanceVerifier verifier, GoalWorktreeCleanupHooks cleanupHooks, GoalId diagnosticGoal,
        Func<string, CancellationToken, DotnetBuildEnvironmentLease> acquireLease) =>
        (prefix, main, tests, token) =>
        {
            var selection = ConductorAcceptanceCohortFocusedAttribution.TrySelect(tests.ToArray(), []);
            if (selection is null) return null;
            using var partition = Compose(workspace, main, prefix, cleanupHooks);
            var runId = "train-bisection-" + Guid.NewGuid().ToString("N");
            using var lease = acquireLease(runId, token);
            using var build = new MergeTrainReproductionBuildScope(lease.Environment);
            var tree = MergeTrainReproductionTree.Read(partition.Path);
            if (tree != partition.TreeRevision) return null;
            var result = AcceptanceExecutionRunner.RunFocusedVerification(verifier, partition.Path, diagnosticGoal,
                selection.Request, lease.Environment.BuildPermitIndex, lease, runBaselineArm: false, token,
                integrationBranch: workspace.IntegrationBranch, projectHomeDirectory: workspace.ProjectHomeDirectoryOrNull,
                focusedRunBinding: new(build.Environment, runId));
            partition.AssertGoalBranchesUnchanged();
            var interpreted = ConductorAcceptanceCohortFocusedAttribution.Interpret(result, selection);
            if (interpreted.Kind != ConductorCohortFocusedPassKind.Executed ||
                result.OutcomeReason is FindingEvidenceOutcomeReason.CandidateInconclusive or FindingEvidenceOutcomeReason.ApparatusFailure ||
                result.Arms?.Any(arm => arm.NotRunSelections?.Count > 0) == true ||
                MergeTrainReproductionTree.Read(partition.Path) != tree) return null;
            // A fresh build scope and TRX codebase bind even a main-only run to its own tree/output.
            var candidate = prefix.LastOrDefault()?.CandidateRevision ?? main;
            var representative = new MergeTrainMemberBinding(diagnosticGoal, candidate, candidate, [], [],
                ChangeRiskTier.Behavior, ConductorTransitionDecision.Auto,
                "Clean", "NoConflictsDetected", BriefVersion: 1);
            var provenance = new MergeTrainReproductionProvenance(main, candidate, partition.TreeRevision, tree,
                build.Environment.ArtifactsPath, result.Checks.Select(check => check.ArtifactsPath ?? "").ToArray(),
                AcceptanceTrxCodeBaseReader.Read(interpreted.TestResultPaths));
            if (MergeTrainReproductionEvidenceCheck.InvalidReason(provenance, representative, main) is not null) return null;
            var execution = MergeTrainFocusedRecheckRunner.ReadExecution(interpreted.TestResultPaths);
            if (execution is null) return null;
            // Build output is disposable; routed evidence must remain readable after cleanup.
            var evidenceRoot = Path.Combine(workspace.OrchestratorDirectory, "acceptance-gate-attempts", runId);
            Directory.CreateDirectory(evidenceRoot);
            var paths = interpreted.TestResultPaths.Select((source, index) =>
            {
                var destination = Path.Combine(evidenceRoot, $"prefix-{prefix.Count}-{index}.trx");
                File.Copy(source, destination, overwrite: false);
                return destination;
            }).ToArray();
            return new(execution with { Provenance = provenance }, paths, runId);
        };

    private static AcceptanceCohortWorkspace Compose(OrchestratorWorkspace workspace, string main,
        IReadOnlyList<MergeTrainMemberBinding> prefix, GoalWorktreeCleanupHooks cleanupHooks)
    {
        if (prefix.Count == 1)
        {
            var member = prefix[0];
            return GoalWorktrees.CreateAcceptancePartitionWorkspace(workspace.ExecutionDirectory, main,
                new(member.GoalId, member.BranchRevision, member.CandidateRevision, member.LandingPaths,
                    member.ResourceKeys, member.ChangeRiskTier, member.AutoPromotionDisposition,
                    member.MergeStatus, member.MergeReason, member.BriefVersion),
                workspace.IntegrationBranch, cleanupHooks, requireCurrentMain: false);
        }
        if (prefix.Count > 1)
        {
            var train = GoalWorktrees.CreateMergeTrainWorkspace(workspace.ExecutionDirectory, main, prefix,
                workspace.IntegrationBranch, cleanupHooks);
            if (train.Ejections.Count > 0 || train.Members.Count != prefix.Count)
            {
                train.Dispose();
                throw new InvalidOperationException("Prefix composition lost a member.");
            }
            // Both disposable types use git worktree removal and the same cleanup-debt seam.
            return new(workspace.ExecutionDirectory, train.Path, train.CommitRevision, train.TreeRevision,
                train.OriginalBranchRevisions, cleanupHooks);
        }
        var path = Path.Combine(workspace.ExecutionDirectory, GoalWorktrees.DirectoryName, "p-" + Guid.NewGuid().ToString("N")[..12]);
        var added = GitCli.Run(workspace.ExecutionDirectory, "worktree", "add", "--detach", path, main);
        if (added.ExitCode != 0) throw new InvalidOperationException("Main baseline worktree unavailable: " + added.Error);
        var baseline = new AcceptanceCohortWorkspace(workspace.ExecutionDirectory, path, main,
            GoalWorktrees.ResolveRequiredRef(path, "HEAD^{tree}"), new Dictionary<GoalId, string>(), cleanupHooks);
        try
        {
            AcceptanceWorkspaceIntegrityPreparer.Prepare(path);
            return baseline;
        }
        catch { baseline.Dispose(); throw; }
    }
}
