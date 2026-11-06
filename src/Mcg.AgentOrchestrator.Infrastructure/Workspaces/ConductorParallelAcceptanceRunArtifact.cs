namespace Mcg.AgentOrchestrator.App.Orchestration;

// Shared persisted result schema. Preserve its namespace for existing consumers.
internal sealed record ConductorParallelAcceptanceRunArtifact(
    string Kind,
    string? FaultKind,
    string? FaultMessage,
    string? State,
    string? Message,
    AcceptanceVerificationSummary? Acceptance,
    string? BranchHeadSha,
    string? MainHeadSha,
    string? EarlyOutcomeKind,
    string? EarlyOutcomeDetail,
    string DispatchKind = Mcg.AgentOrchestrator.Infrastructure.AcceptanceAttemptResultRecord.GateDispatchKind,
    Mcg.AgentOrchestrator.Infrastructure.FocusedEvidenceRunResult? FocusedEvidence = null,
    string? InfrastructureReasonCode = null,
    int? InfrastructureExitCode = null,
    string? InfrastructureOutputTail = null,
    Mcg.AgentOrchestrator.Infrastructure.BuildLockAttribution? InfrastructureBuildLockAttribution = null,
    string? FaultType = null,
    string? FaultStack = null,
    string? GatePhase = null,
    string? GateTarget = null);
