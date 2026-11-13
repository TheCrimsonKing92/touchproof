using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class ExperimentReplayProspectiveFeed
{
    internal sealed record Rejection(string CaseId, ExperimentReplayExecution ExecutionA,
        ExperimentReplayExecution ExecutionB, string Reason);
    internal sealed record Result(bool Admitted, ExperimentReplaySequentialStopRule.Decision? Decision,
        IReadOnlyList<Rejection> Rejected);

    private readonly ExperimentReplaySequentialStopRule _rule;
    private readonly List<Rejection> _rejected = [];

    internal ExperimentReplayProspectiveFeed(ExperimentReplaySequentialStopRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        _rule = rule;
    }

    internal Result Offer(ExperimentReplayExecution executionA, ExperimentReplayExecution executionB)
    {
        ArgumentNullException.ThrowIfNull(executionA);
        ArgumentNullException.ThrowIfNull(executionB);

        var reason = !ExperimentReplayStatistics.Valid(executionA) || !ExperimentReplayStatistics.Valid(executionB)
            ? "invalid-measurement"
            : !string.Equals(executionA.Slot.CaseId, executionB.Slot.CaseId, StringComparison.Ordinal)
                ? "case-mismatch"
                : executionA.Slot.Arm != "A" || executionB.Slot.Arm != "B" ? "arm-mismatch" : null;
        if (reason is not null)
        {
            _rejected.Add(new(executionA.Slot.CaseId, executionA, executionB, reason));
            return new(false, _rule.Current, _rejected.AsReadOnly());
        }

        var difference = executionB.Resources!.CpuMilliseconds - (double)executionA.Resources!.CpuMilliseconds;
        var decision = _rule.Observe(difference);
        return new(true, decision, _rejected.AsReadOnly());
    }
}
