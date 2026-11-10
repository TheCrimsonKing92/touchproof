namespace Mcg.AgentOrchestrator.Core;

// An operator decision about exact file scope for one integration round, never landing authority.
public enum IntegrationScopeAction { Approve, OriginalOnly, Revoke }

public sealed record IntegrationScopeAuthorization(
    string GoalId, string RoundId, string OriginalHead, string MainSha,
    IntegrationScopeAction Action, IReadOnlyList<string> Paths, string Reason,
    string? ApprovalId = null);
