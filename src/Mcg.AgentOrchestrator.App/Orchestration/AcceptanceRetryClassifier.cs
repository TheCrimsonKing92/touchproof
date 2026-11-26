using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record AcceptanceRetryDisposition(
    IReadOnlyList<AcceptanceCheckResult> ActionableCriteria,
    IReadOnlyList<ExcludedAcceptanceFailure> ExcludedFailures);

internal sealed record ExcludedAcceptanceFailure(
    string Identity,
    AcceptanceRetryExclusionKind Kind,
    string? ReceiptPointer = null);

internal enum AcceptanceRetryExclusionKind
{
    Inherited,
    UnconfirmedIntroduced
}

internal static class AcceptanceRetryClassifier
{
    internal static AcceptanceRetryDisposition Classify(
        IReadOnlyList<AcceptanceCheckResult> criteria)
    {
        var actionable = new List<AcceptanceCheckResult>();
        var excluded = new List<ExcludedAcceptanceFailure>();
        foreach (var criterion in criteria)
        {
            var failingIdentities = criterion.FailingTestIdentities?
                .Where(identity => !string.IsNullOrWhiteSpace(identity))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (failingIdentities is not { Length: > 0 })
            {
                // Check-level same-name co-failure is not proof that this exact failure existed at the
                // merge base. Identity-less failures therefore remain actionable.
                actionable.Add(criterion);
                continue;
            }

            var actionableIdentities = new List<string>();
            if (criterion.FailingTestAttributions is { Count: > 0 } testAttributions &&
                failingIdentities is { Length: > 0 })
            {
                var byIdentity = testAttributions
                    .GroupBy(attribution => attribution.TestIdentity, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
                foreach (var identity in failingIdentities)
                {
                    if (byIdentity.TryGetValue(identity, out var attribution) &&
                        (attribution.Origin == AcceptanceTestFailureOrigin.Inherited ||
                         (attribution.Origin == AcceptanceTestFailureOrigin.UnconfirmedIntroduced &&
                          attribution.CandidateRerun?.Outcome == "Passed" &&
                          !string.IsNullOrWhiteSpace(attribution.CandidateRerun.ReceiptPointer))))
                    {
                        excluded.Add(new ExcludedAcceptanceFailure(
                            identity,
                            attribution.Origin == AcceptanceTestFailureOrigin.Inherited
                                ? AcceptanceRetryExclusionKind.Inherited
                                : AcceptanceRetryExclusionKind.UnconfirmedIntroduced,
                            attribution.CandidateRerun?.ReceiptPointer));
                    }
                    else
                    {
                        actionableIdentities.Add(identity);
                    }
                }
            }
            else
            {
                // Only per-identity merge-base attribution can suppress a retry. Check-level baseline
                // co-failure does not establish that these identities are inherited.
                actionableIdentities.AddRange(failingIdentities);
            }

            if (actionableIdentities.Count > 0)
            {
                actionable.Add(criterion with { FailingTestIdentities = actionableIdentities });
            }
        }

        return new AcceptanceRetryDisposition(actionable, excluded);
    }
}
