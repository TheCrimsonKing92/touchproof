using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Gate observations classify the retained range without advancing backstop coverage.
internal sealed class SelectedExecutionGateMissDetector(
    SelectedExecutionBackstopCoordinator coordinator, Action<string>? progress = null)
{
    internal sealed record Detection(CandidateRedBaselineVerdict? Verdict,
        IReadOnlyList<SelectedExecutionBackstopMissRecord> Misses,
        IReadOnlyList<SelectedExecutionBackstopLanding> UnclassifiableLandings,
        IReadOnlyList<string> UnmatchedTests);

    internal Task<Detection> ObserveAsync(string gateGoalId,
        FindingEvidenceArmReceipt candidate, FindingEvidenceArmReceipt baseline) =>
        ObserveAsync(gateGoalId, candidate, baseline, null);

    private Task<Detection> ObserveAsync(string gateGoalId, FindingEvidenceArmReceipt candidate,
        FindingEvidenceArmReceipt baseline, Action<string>? step) =>
        coordinator.RecordGateMissesAsync(range =>
        {
            step?.Invoke("classify");
            var detection = Detect(gateGoalId, candidate, baseline, range);
            step?.Invoke("record");
            return detection;
        });

    // Finding-evidence routing is synchronous; finish the observation before returning its unchanged arms.
    internal static IReadOnlyList<FindingEvidenceArmReceipt> Observe(
        SelectedExecutionGateMissDetector? detector, GoalId goalId, TaskId taskId, string attempt,
        IReadOnlyList<FindingEvidenceArmReceipt> arms, Action<GoalId, TaskId, string> recordGoalEvent)
    {
        if (detector is null) return arms;
        var candidate = arms.SingleOrDefault(arm => arm.Arm == FindingEvidenceArm.Candidate);
        var baseline = arms.SingleOrDefault(arm => arm.Arm == FindingEvidenceArm.Baseline);
        if (candidate?.Disposition != FindingEvidenceArmDisposition.Red ||
            baseline?.Disposition != FindingEvidenceArmDisposition.Red) return arms;

        detector.ObserveBeforeDecision(goalId, taskId, attempt, candidate, baseline, recordGoalEvent);
        return arms;
    }

    private void ObserveBeforeDecision(GoalId goalId, TaskId taskId, string attempt,
        FindingEvidenceArmReceipt candidate, FindingEvidenceArmReceipt baseline,
        Action<GoalId, TaskId, string> recordGoalEvent)
    {
        var step = "read-retained-landings";
        try
        {
            ObserveAsync(goalId.Value, candidate, baseline, next => step = next).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            var line = $"SELECTED_EXECUTION_GATE_MISS result=detector-failed goal={goalId} " +
                $"attempt={attempt} step={step} detail={ex.GetType().Name}: {ex.Message}";
            progress?.Invoke(line);
            recordGoalEvent(goalId, taskId, line);
        }
    }

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
