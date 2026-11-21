using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

public sealed class WorkerUsageArchiveSampleTests
{
    private static readonly DateTimeOffset LandedAt = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Measure_ArchivedDispatches_PreservesWindowSamples()
    {
        // Parallel-safe: this test owns its state database.
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var statePath = Path.Combine(root, "state.db");
            var repository = InfrastructureTestSupport.CreateMigratedStateRepository(statePath);
            var goalId = GoalId.New().Value;
            TaskDispatchSnapshot[] dispatches =
            [
                Dispatch(-3, input: 800, turns: 80),
                Dispatch(-2, input: 40, turns: 8),
                Dispatch(-1, input: 20, turns: 4),
                Dispatch(0, input: 12, turns: 3),
                Dispatch(1, input: 8, turns: 1),
                Dispatch(2, input: 900, turns: 90)
            ];
            var task = new TaskSnapshot(TaskId.New().Value, "Completed implementation", AgentRole.Developer,
                WorkTaskStatus.Completed, null, null, null, null, dispatches[^1], null,
                DispatchHistory: dispatches, WorkerBuildInvocations: []);
            await repository.SaveGoalSnapshotsAsync([
                new GoalSnapshot(goalId, "Worker usage history", GoalStatus.Completed, [task],
                    [new ProgressEventSnapshot(goalId, null, ProgressKind.GoalPolicyDecision, "Completed", LandedAt.AddHours(1))])
            ]);

            var beforeArchival = WorkerUsageSampleSource.Read(statePath);
            AssertSamples(beforeArchival);

            Assert.Equal(1, await repository.ArchiveTerminalGoalSnapshotsAsync(LandedAt.AddDays(1), compressSnapshots: true));
            Assert.True(Assert.Single((await repository.LoadAsync()).Goals).IsMetadataOnly);

            AssertSamples(WorkerUsageSampleSource.Read(statePath));
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    private static void AssertSamples(WorkerUsageSampleSource source)
    {
        var input = new OutcomeMetricLine(CostGoalOutcomeMetric.WorkerInput, "decrease", "10%", 2, Role: AgentRole.Developer);
        var turns = input with { Name = CostGoalOutcomeMetric.WorkerTurns };
        var evaluatedAt = LandedAt.AddMinutes(1);

        Assert.Equal((new OutcomeMetricSample(30, 2), new OutcomeMetricSample(10, 2)),
            source.Measure(input, LandedAt, evaluatedAt));
        Assert.Equal((new OutcomeMetricSample(6, 2), new OutcomeMetricSample(2, 2)),
            source.Measure(turns, LandedAt, evaluatedAt));
    }

    private static TaskDispatchSnapshot Dispatch(int minute, long input, long turns) =>
        new("fixture", "fixture", "fixture", LandedAt.AddMinutes(minute),
            ProviderUsage: new DispatchProviderUsage(
                ProviderUsageValue.Reported(input), ProviderUsageValue.Reported(0), ProviderUsageValue.Reported(0))
            {
                Turns = ProviderUsageValue.Reported(turns)
            });
}
