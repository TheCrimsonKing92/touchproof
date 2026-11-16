using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Gate observations classify the retained range without advancing backstop coverage.
internal sealed class SelectedExecutionGateMissDetector(SelectedExecutionBackstopCoordinator coordinator)
{
    internal sealed record Detection(CandidateRedBaselineVerdict? Verdict,
        IReadOnlyList<SelectedExecutionBackstopMissRecord> Misses,
        IReadOnlyList<SelectedExecutionBackstopLanding> UnclassifiableLandings,
        IReadOnlyList<string> UnmatchedTests);

    internal Task<Detection> ObserveAsync(string gateGoalId,
        FindingEvidenceArmReceipt candidate, FindingEvidenceArmReceipt baseline) =>
        coordinator.RecordGateMissesAsync(range => Detect(gateGoalId, candidate, baseline, range));

    internal static Detection Detect(string gateGoalId, FindingEvidenceArmReceipt candidate,
        FindingEvidenceArmReceipt baseline, IReadOnlyList<SelectedExecutionBackstopLanding> range)
    {
        if (candidate.Disposition != FindingEvidenceArmDisposition.Red ||
            baseline.Disposition != FindingEvidenceArmDisposition.Red)
            return new(null, [], [], []);

        var comparison = CandidateRedBaselineInheritance.Compare(candidate, baseline);
        if (comparison.Verdict != CandidateRedBaselineVerdict.Inherited)
            return new(comparison.Verdict, [], [], comparison.CandidateFailures);

        var misses = new List<SelectedExecutionBackstopMissRecord>();
        var unclassifiable = new List<SelectedExecutionBackstopLanding>();
        var matched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var landing in range.OrderBy(landing => landing.Ordinal))
        {
            // Classify separately because the existing classifier returns no landing attribution.
            var classification = SelectedExecutionBackstopMissClassifier.Classify(comparison.CandidateFailures, [landing]);
            unclassifiable.AddRange(classification.UnclassifiableLandings);
            if (!classification.IsMiss) continue;

            var failures = comparison.CandidateFailures.Except(classification.UnmatchedTests, StringComparer.Ordinal).ToArray();
            matched.UnionWith(failures);
            misses.Add(new("gate-" + landing.GoalId + "-" + landing.Commit,
                landing.Commit, landing.Commit, [landing.GoalId], failures, [],
                $"gate-detected gate_goal={gateGoalId} candidate={candidate.Sha} baseline={baseline.Sha}"));
        }
        return new(comparison.Verdict, misses, unclassifiable,
            comparison.CandidateFailures.Where(identity => !matched.Contains(identity)).ToArray());
    }
}
