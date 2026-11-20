namespace Mcg.AgentOrchestrator.Core;

// Captures the criterion and proposal the Planner requested against a specific brief version.
public sealed record PlannerBriefAmendmentPayload(int CriterionIndex, int RequestedBriefVersion, string? ProposedText = null);

public enum PlannerBriefAmendmentDisposition
{
    Decline,
    Accept
}

internal static class PlannerBriefAmendmentDispositions
{
    internal static bool IsSupported(PlannerBriefAmendmentDisposition? disposition) =>
        disposition is PlannerBriefAmendmentDisposition.Decline or PlannerBriefAmendmentDisposition.Accept;

    internal static PlannerBriefAmendmentDisposition? ParseFlag(string? value) => value switch
    {
        null => null,
        _ when value.Equals("decline", StringComparison.OrdinalIgnoreCase) => PlannerBriefAmendmentDisposition.Decline,
        _ when value.Equals("accept", StringComparison.OrdinalIgnoreCase) => PlannerBriefAmendmentDisposition.Accept,
        _ => throw new ArgumentException("--amendment accepts only accept or decline.")
    };
}

public sealed class PlannerBriefAmendmentAcceptRejectedException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);
