namespace Mcg.AgentOrchestrator.Core;

// Identity of one requesting round at one candidate and execution basis, not an execution attempt.
public sealed record FindingEvidenceBatchIdentity(
    TaskId RequestingTaskId, string CandidateSha, string RoundFingerprint, string ExecutionBasisIdentity)
{
    public string Key => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new[]
        { RequestingTaskId.Value, CandidateSha.ToLowerInvariant(), RoundFingerprint, ExecutionBasisIdentity }))));
}

public sealed record FindingEvidenceBatchClosure(FindingEvidenceBatchIdentity Identity, string Accounting);
