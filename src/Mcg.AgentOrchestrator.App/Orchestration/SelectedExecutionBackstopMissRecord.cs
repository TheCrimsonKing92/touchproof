namespace Mcg.AgentOrchestrator.App.Orchestration;

// Names a suspect range, never attributes a failure to an individual goal.
internal sealed record SelectedExecutionBackstopMissRecord(string Id, string CoveredFromCommit,
    string CoveredToCommit, IReadOnlyList<string> GoalIds, IReadOnlyList<string> FailingTests,
    IReadOnlyList<string> FailedChecks, string? GoalRangeNote)
{
    internal string CorrelationKey => "selected-execution-backstop-miss:" + Id;
}
