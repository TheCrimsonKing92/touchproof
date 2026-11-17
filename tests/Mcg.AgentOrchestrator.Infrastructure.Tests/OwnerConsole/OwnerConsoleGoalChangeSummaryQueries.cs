using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using TestGoal = OwnerConsoleGoalChangeSummaryTestGoal;

internal sealed class OwnerConsoleGoalChangeSummaryQueries(TestGoal goal, Task? beforeRead = null) : IOrchestratorStateQueries
{
    internal TestGoal Goal = goal;
    internal List<string> ReadIds = [];
    internal int MetadataReads;
    public async Task<AgentOrchestratorKernel> LoadGoalsAsync(IReadOnlyCollection<GoalId> ids, CancellationToken cancellationToken = default)
    {
        if (beforeRead is not null) await beforeRead.WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        Assert.Single(ids); Assert.Equal(Goal.Id, ids.Single()); ReadIds.Add(ids.Single().Value);
        return AgentOrchestratorKernel.FromSnapshot(new([Goal.ToSnapshot()], []));
    }
    public Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(CancellationToken cancellationToken = default)
    { MetadataReads++; throw new InvalidOperationException("Unrelated metadata read"); }
}
