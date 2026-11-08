using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    internal Func<Goal, string, (KeyedRepositoryEvidenceTarget? Target, string Failure)> ResolveKeyedEvidenceTarget { get; set; } =
        (_, _) => (null, "keyed evidence target resolver was not configured");

    internal Func<Goal, KeyedRepositoryEvidenceTarget, string, DotnetBuildEnvironmentLease?, bool, CancellationToken,
        FindingEvidenceNegativeControl?, IReadOnlyList<string>?, FindingEvidenceMutation?, SourceRevertedEvidenceContinuation?, FocusedEvidenceRunResult>
        RunKeyedFocusedEvidence { get; set; } = (_, _, request, _, _, _, _, _, _, _) =>
            new FocusedEvidenceRunResult(request, false, false, "keyed evidence runner was not configured", []);

    private readonly Func<Goal, string, DotnetBuildEnvironmentLease?, bool, CancellationToken,
        FindingEvidenceNegativeControl, IReadOnlyList<string>?, FindingEvidenceMutation?, SourceRevertedEvidenceContinuation?, FocusedEvidenceRunResult> _runNegativeControlFocusedEvidence;

    internal KeyedRepositoryEvidenceTarget? ResolveAttemptEvidenceTarget(Goal goal, ConductorParallelAcceptanceAttempt attempt)
    {
        if (attempt.FocusedEvidenceRepository is null) return null;
        var (target, failure) = ResolveKeyedEvidenceTarget(goal, attempt.FocusedEvidenceRepository);
        if (target is null)
            throw new InvalidOperationException($"Cannot resolve focused evidence repository '{attempt.FocusedEvidenceRepository}': {failure}");
        if (!string.Equals(target.CandidateHead, attempt.BranchHeadSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Repository candidate changed before focused evidence attempt launch.");
        return target;
    }

    private Func<Goal, string, DotnetBuildEnvironmentLease?, CancellationToken, FocusedEvidenceRunResult>
        SelectFindingEvidenceRunner(FindingEvidenceNegativeControl? mode, bool runBaselineArm,
            IReadOnlyList<string>? revertPaths = null, FindingEvidenceMutation? mutation = null,
            KeyedRepositoryEvidenceTarget? repositoryTarget = null)
    {
        if (repositoryTarget is not null)
        {
            FocusedEvidenceRunResult Run(Goal goal, string request, DotnetBuildEnvironmentLease? lease,
                bool baseline, CancellationToken token, FindingEvidenceNegativeControl selectedControl,
                IReadOnlyList<string>? paths, FindingEvidenceMutation? selectedMutation, SourceRevertedEvidenceContinuation? continuation) =>
                RunKeyedFocusedEvidence(goal, repositoryTarget, request, lease, baseline, token, selectedControl, paths, selectedMutation, continuation);
            return ConductorParallelAcceptanceAttemptCoordinator.SourceRevertFocusedRunner.TryCreate(
                mode, runBaselineArm, revertPaths, mutation, Run) ??
                ((goal, request, lease, token) => RunKeyedFocusedEvidence(
                    goal, repositoryTarget, request, lease, runBaselineArm, token, mode, revertPaths, mutation, null));
        }
        return ConductorParallelAcceptanceAttemptCoordinator.SourceRevertFocusedRunner.TryCreate(
            mode, runBaselineArm, revertPaths, mutation, _runNegativeControlFocusedEvidence) ?? (mode is { } control
            ? (goal, request, lease, token) =>
                _runNegativeControlFocusedEvidence(goal, request, lease, runBaselineArm, token, control, revertPaths, mutation, null)
            : runBaselineArm ? _runDualArmFocusedEvidence : _runFocusedEvidence);
    }

    internal ConductorParallelAcceptanceRunResult RunContinuableSourceRevertEvidence(
        ConductorParallelAcceptanceCandidate candidate, string request, DotnetBuildEnvironmentLease? lease,
        CancellationToken token, IReadOnlyList<string> paths, SourceRevertedEvidenceContinuation? continuation,
        KeyedRepositoryEvidenceTarget? repositoryTarget = null) =>
        ConductorParallelAcceptanceRunResult.Focused(candidate,
            repositoryTarget is null
                ? _runNegativeControlFocusedEvidence(candidate.Goal, request, lease, false, token,
                    FindingEvidenceNegativeControl.RevertSrc, paths, null, continuation)
                : RunKeyedFocusedEvidence(candidate.Goal, repositoryTarget, request, lease, false, token,
                    FindingEvidenceNegativeControl.RevertSrc, paths, null, continuation));

    private static FocusedEvidenceRunResult RetainNegativeControlEvidence(
        FocusedEvidenceRunResult next, FocusedEvidenceRunResult prior) =>
        ConductorParallelAcceptanceAttemptCoordinator.RetainNegativeControlEvidence(next, prior);
}
