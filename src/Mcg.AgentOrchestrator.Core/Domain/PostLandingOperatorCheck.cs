namespace Mcg.AgentOrchestrator.Core;

public static class PostLandingOperatorCheck
{
    public const string Availability = "post-landing";

    public static bool IsPostLandingCheck(HumanInputRequest request) =>
        request.Kind == HumanWaitKind.ProspectiveAcceptanceEvidence &&
        string.Equals(request.PlannerEvidence?.Availability, Availability, StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<HumanInputRequest> HoldingLanding(IEnumerable<HumanInputRequest> pending) =>
        pending.Where(request => !IsPostLandingCheck(request)).ToArray();

    public static string? DescribeOpen(IEnumerable<HumanInputRequest> pending)
    {
        var checks = pending.Where(IsPostLandingCheck).Select(request =>
            $"{request.Id.Value[..Math.Min(8, request.Id.Value.Length)]} " +
            $"(criterion {request.PlannerEvidence!.CriterionIndex}: {request.PlannerEvidence.Needed})").ToArray();
        return checks.Length == 0 ? null : "open post-landing operator check(s): " + string.Join("; ", checks);
    }

    public static string? DescribeOpen(
        IEnumerable<HumanInputRequest> pending,
        IEnumerable<CriterionEvidenceObligation> unpublishedDuties)
    {
        var requests = DescribeOpen(pending);
        var duties = unpublishedDuties.Where(duty => !string.IsNullOrWhiteSpace(duty.ExpectedCandidateSha)).Select(duty =>
            $"{duty.Id} (criterion {duty.CriterionNumber}: unpublished expected candidate " +
            $"{duty.ExpectedCandidateSha![..Math.Min(8, duty.ExpectedCandidateSha.Length)]})").ToArray();
        if (duties.Length == 0) return requests;
        return (requests is null ? "open post-landing operator check(s): " : requests + "; ") +
            string.Join("; ", duties);
    }
}
