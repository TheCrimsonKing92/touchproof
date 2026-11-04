namespace Mcg.AgentOrchestrator.Core;

// Evidence about source structure, never a compiler outcome or completion verdict.
public enum WorkerBuildStructuralStatus { Unavailable, Passed, Violation, NotApplicable }

public sealed record WorkerBuildStructuralCheck(WorkerBuildStructuralStatus Status, string Diagnostic,
    string InvocationId, string DispatchId, string TreeDigest, string ScopeIdentity,
    string? ProjectHomeDirectory, string PolicyDigest)
{
    public bool AllowsVerificationSuccess => Status is WorkerBuildStructuralStatus.Passed or WorkerBuildStructuralStatus.NotApplicable;

    public bool IsWellFormed => Enum.IsDefined(Status) && !string.IsNullOrWhiteSpace(Diagnostic) &&
        !string.IsNullOrWhiteSpace(InvocationId) && !string.IsNullOrWhiteSpace(DispatchId) &&
        !string.IsNullOrWhiteSpace(TreeDigest) && !string.IsNullOrWhiteSpace(ScopeIdentity) &&
        !string.IsNullOrWhiteSpace(PolicyDigest);

    public bool IsBoundTo(WorkerBuildInvocation invocation) =>
        IsWellFormed &&
        InvocationId == invocation.Id && DispatchId == invocation.DispatchId &&
        TreeDigest == invocation.TreeDigest && ScopeIdentity == invocation.ScopeIdentity &&
        !string.IsNullOrWhiteSpace(PolicyDigest);
}

// Host-selected policy expectations survive deletion of policy from the candidate.
public sealed record WorkerBuildStructuralPolicy(string? ProjectHomeDirectory, string IntegrationBranch,
    bool RequiresSizeAuthority, bool RequiresManifest, string? RequiredManifestPath = null, string? BaselineRevision = null);
