namespace Mcg.AgentOrchestrator.Infrastructure;

// A host-consumption classification, never inferred from worker output or exception text.
public enum WorkerBuildEvidenceGap
{
    Unclassified,
    MissingApplicableInvocation,
    // Invalid authority/history is a refusal, never authorization for apparatus recovery.
    Rejected
}
