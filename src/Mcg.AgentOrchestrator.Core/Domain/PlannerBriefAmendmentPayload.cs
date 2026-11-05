namespace Mcg.AgentOrchestrator.Core;

// Request-time authority; an answer cannot change the authoritative brief.
public sealed record PlannerBriefAmendmentPayload(int CriterionIndex, int RequestedBriefVersion);

public enum PlannerBriefAmendmentDisposition
{
    Decline
}
