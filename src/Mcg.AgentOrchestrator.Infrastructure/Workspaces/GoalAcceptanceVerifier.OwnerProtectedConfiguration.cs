using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class GoalAcceptanceVerifier
{
    private string? _ownerPolicyDecisionStoreDirectory;

    private bool HasOwnerPolicyApproval(string worktreePath, GoalId? goalId, IReadOnlyList<string>? changedFiles)
    {
        var candidateSha = ResolveGitScalar(worktreePath, "rev-parse", "HEAD");
        if (candidateSha is null) return false;
        if (HasOwnerPolicyApprovalForCandidate(goalId, candidateSha)) return true;
        if (goalId is null || _ownerPolicyDecisionStoreDirectory is null ||
            !Directory.Exists(_ownerPolicyDecisionStoreDirectory)) return false;
        var fingerprint = AcceptanceOwnerProtectedFingerprint.Compute(worktreePath, changedFiles,
            AcceptanceGitTextResolver.Resolve, "HEAD", readCommittedCandidate: false, IntegrationBranch);
        return fingerprint is not null && AcceptancePolicyChangeDecision.IsApprovedForFingerprintAsync(
            CollaborationItemStore.ForDirectory(_ownerPolicyDecisionStoreDirectory), goalId.Value, fingerprint)
            .GetAwaiter().GetResult();
    }

    internal bool HasOwnerPolicyApprovalForCandidateTests(GoalId goalId, string candidateSha) =>
        HasOwnerPolicyApprovalForCandidate(goalId, candidateSha);

    private bool HasOwnerPolicyApprovalForCandidate(GoalId? goalId, string? candidateSha)
    {
        if (goalId is null) return false;
        if (candidateSha is null || _ownerPolicyDecisionStoreDirectory is null ||
            !Directory.Exists(_ownerPolicyDecisionStoreDirectory)) return false;
        var store = CollaborationItemStore.ForDirectory(_ownerPolicyDecisionStoreDirectory);
        return AcceptancePolicyChangeDecision.IsApprovedAsync(store, goalId.Value, candidateSha)
            .GetAwaiter().GetResult();
    }

    private AcceptanceCheckResult? TryClassifyManifestTrust(
        string worktreePath,
        IReadOnlyList<string>? changedFiles) =>
        OwnerProtectedConfigurationPolicy.TryClassifyManifestTrustCore(worktreePath, changedFiles, AcceptanceGitTextResolver.Resolve, IntegrationBranch);

    private OwnerProtectedConfigurationPolicy.OwnerProtectedDecision EvaluateOwnerProtectedConfiguration(string worktreePath, GoalId? goalId,
        IReadOnlyList<string>? changedFiles, IReadOnlyList<AcceptanceOwnerProtectedCohortMember>? cohortMembers)
    {
        var store = _ownerPolicyDecisionStoreDirectory is { } directory && Directory.Exists(directory)
            ? CollaborationItemStore.ForDirectory(directory) : null;
        return OwnerProtectedConfigurationPolicy.EvaluateOwnerProtectedConfigurationCore(worktreePath, goalId, changedFiles, cohortMembers,
            AcceptanceGitTextResolver.Resolve, store,
            () => AcceptanceTestInventorySource.Read(worktreePath, IntegrationBranch, AcceptanceGitTextResolver.Resolve),
            () => ResolveGitScalar(worktreePath, "rev-parse", "HEAD"), IntegrationBranch);
    }
}
