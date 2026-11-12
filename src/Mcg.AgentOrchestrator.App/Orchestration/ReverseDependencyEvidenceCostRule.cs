using System.Globalization;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ReverseDependencyEvidenceCostRule
{
    internal sealed record Decision(bool Run, string Line);

    internal static Decision Evaluate(double selectionSeconds, double fullGateSeconds, double? failureRate)
    {
        var inputs = $"selection_seconds={Number(selectionSeconds)} full_gate_seconds={Number(fullGateSeconds)} " +
            $"candidate_failure_rate={(failureRate is { } rate ? Number(rate) : "unavailable")}";
        if (!double.IsFinite(selectionSeconds) || selectionSeconds < 0 ||
            !double.IsFinite(fullGateSeconds) || fullGateSeconds < 0 ||
            failureRate is { } invalid && (!double.IsFinite(invalid) || invalid < 0 || invalid > 1))
            return new(false, $"REVERSE_DEPENDENCY_EVIDENCE_COST {inputs} decision=skip reason=invalid input");
        if (failureRate is null)
            return new(false, $"REVERSE_DEPENDENCY_EVIDENCE_COST {inputs} decision=skip reason=rate could not be computed");
        var run = selectionSeconds <= failureRate.Value * fullGateSeconds;
        return new(run, $"REVERSE_DEPENDENCY_EVIDENCE_COST {inputs} decision={(run ? "run" : "skip")}");
    }

    private static string Number(double value) => value.ToString("G17", CultureInfo.InvariantCulture);
}
