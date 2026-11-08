using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Coverage depends directly on identity-bearing failures in the same proof.
internal static class SoloCoveredCheckInheritance
{
    internal static IReadOnlyList<AcceptanceCheckResult>? CoveredChecks(
        AcceptanceCheckResult check, IReadOnlyList<AcceptanceCheckResult> proofChecks)
    {
        if (check.CoveredBy is not { Count: > 0 } coveredBy || check.Passed || check.Advisory ||
            check.FailingTestIdentities is { Count: > 0 }) return null;

        var covered = new List<AcceptanceCheckResult>();
        foreach (var name in coveredBy.Distinct(StringComparer.Ordinal))
        {
            var matches = proofChecks.Where(candidate => !ReferenceEquals(candidate, check) &&
                !candidate.Passed && string.Equals(candidate.Name, name, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1 || matches[0].FailingTestIdentities is not { Count: > 0 }) return null;
            covered.Add(matches[0]);
        }
        return covered;
    }
}
