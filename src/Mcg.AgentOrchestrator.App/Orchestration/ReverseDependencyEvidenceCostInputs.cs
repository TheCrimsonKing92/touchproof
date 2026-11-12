using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ReverseDependencyEvidenceCostInputs(
    double SelectionSeconds, double FullGateSeconds, double? FailureRate, string? UnavailableReason = null)
{
    internal sealed record Attempt(ConductorParallelAcceptanceAttempt Metadata,
        ConductorParallelAcceptanceRunArtifact? Result);

    internal static ReverseDependencyEvidenceCostInputs Compute(
        IReadOnlyList<string> selectionClasses, IReadOnlyList<string> gateClasses,
        IReadOnlyDictionary<string, double> medians, double defaultSeconds,
        IEnumerable<Attempt> attempts, DateTimeOffset now)
    {
        if (!double.IsFinite(defaultSeconds) || defaultSeconds <= 0 ||
            medians.Values.Any(value => !double.IsFinite(value) || value < 0))
            return Unavailable("invalid duration input");
        var fallback = medians.Count == 0 ? defaultSeconds : AcceptanceBalancedLanePacker.Median(medians.Values);
        var history = new AcceptanceClassDurationHistoryAdapter(medians);
        double Cost(IReadOnlyList<string> classes) => classes.Distinct(StringComparer.Ordinal)
            .Sum(name => history.PredictSeconds("Infrastructure.Tests:" + name) ?? fallback);
        var completed = attempts.Where(item => item.Metadata.Kind == "gate" &&
                item.Metadata.CompletedAt is { } time && time >= now.AddDays(-14) && time <= now)
            .DistinctBy(item => item.Metadata.AttemptId).ToArray();
        return new(Cost(selectionClasses), Cost(gateClasses), completed.Length == 0 ? null :
            (double)completed.Count(CandidateCausedFailure) / completed.Length);
    }

    private static bool CandidateCausedFailure(Attempt attempt)
    {
        if (attempt.Result is not { Kind: "accepted", FaultKind: null, InfrastructureReasonCode: null,
                Acceptance: { Passed: false } acceptance }) return false;
        return acceptance.UnmetCriteria.Any(check => !check.Passed && !check.Advisory &&
            check.FailureCauseEvidence?.Cause != AcceptanceFailureCause.EnvironmentalApparatus &&
            !(acceptance.CheckAttributions ?? []).Any(attribution => attribution.CheckName == check.Name &&
                attribution.Cause == AcceptanceFailureCause.EnvironmentalApparatus) &&
            ((check.FailingTestAttributions ?? []).Any(attribution =>
                attribution.Origin == AcceptanceTestFailureOrigin.Introduced) ||
             check.FailingTestAttributions is not { Count: > 0 } &&
             (acceptance.CheckAttributions ?? []).Any(attribution => attribution.CheckName == check.Name &&
                 attribution.Origin == AcceptanceFailureOrigin.Introduced)));
    }

    internal static ReverseDependencyEvidenceCostInputs Unavailable(string reason) =>
        new(double.NaN, double.NaN, null, reason);
}
