namespace Mcg.AgentOrchestrator.Core;

// Authority belongs to the completed acceptance attempt. The digest binds its retained
// evidence, not a caller-supplied TRX path or an Introduced display label.
public sealed record SoloAcceptanceFailureReceipt(
    int ContractVersion, string GoalId, string AttemptId, string CandidateSha,
    string CandidateTreeSha, string MainSha, string BaselineSha,
    string ResultsRoot, SoloAcceptanceArtifact Evidence, string? CaptureError = null);

public sealed record SoloAcceptanceArtifact(string Path, string Sha256);
