namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record SelectedExecutionBackstopLanding(long Ordinal, string GoalId, string Commit, bool Applied, bool RecordUnreadable = false)
{
    public IReadOnlyList<string> SkippedClasses { get; init; } = [];
}
