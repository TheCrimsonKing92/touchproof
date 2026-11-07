using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static ExitReconcileBuildControlSupport;

// Parallel-safe: all negative stimuli affect only fixture-owned files and canonical state.
public sealed class ForeignStoreExitReconcileBuildTests
{
    [Theory]
    [InlineData("terminal")]
    [InlineData("batch")]
    [InlineData("preflight")]
    public async Task ProductionComposition_SeparateStore_CannotBorrowNativeSuccess(string path)
    {
        using var fixture = new ExitReconcileBuildFixture();
        var invocation = await fixture.CompleteNativeDispatch();
        Assert.Equal(WorkerBuildOutcome.Success, invocation.Outcome);
        var foreignRoot = Path.Combine(fixture.Build.Root, "foreign-authority");
        Directory.CreateDirectory(foreignRoot);
        var foreign = fixture.Workspace with
        {
            OrchestratorDirectory = foreignRoot,
            SqliteStatePath = Path.Combine(foreignRoot, "state.db"),
            LogDirectory = Path.Combine(foreignRoot, "logs")
        };
        var snapshot = fixture.Kernel.ExportGoalSnapshot(fixture.Build.GoalId);
        var foreignKernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([
            snapshot with { Tasks = snapshot.Tasks.Select(task => task with { WorkerBuildInvocations = [] }).ToArray() }
        ], []));
        _ = StateDbMigrations.EnsureUpToDate(foreign.SqliteStatePath);
        await new SqliteOrchestratorStateRepository(foreign.SqliteStatePath).SaveAsync(foreignKernel);

        fixture.Kernel = foreignKernel;
        int Reconcile() => path switch
        {
            "terminal" => fixture.TerminalSweep(foreign),
            "batch" => fixture.BatchRecovery(foreign),
            _ => fixture.DispatchPreflight(foreign)
        };

        Assert.Equal(1, Reconcile());
        AssertRejected(fixture, "worker_build_host=enrollment-invalid");
        Assert.Null(fixture.Task.CompletionBuildEvidence);
        var repository = new SqliteOrchestratorStateRepository(foreign.SqliteStatePath);
        await repository.SaveAsync(fixture.Kernel);
        fixture.Kernel = await repository.LoadGoalsAsync([fixture.Build.GoalId]);
        Assert.Equal(0, Reconcile());
        AssertRejected(fixture, "worker_build_host=enrollment-invalid");
        Assert.Null(fixture.Task.CompletionBuildEvidence);
        Assert.False(fixture.StoredInvocation().Consumed);
        Assert.Equal(invocation.Version, fixture.StoredInvocation().Version);
        Assert.Empty((await new SqliteOrchestratorStateRepository(foreign.SqliteStatePath)
            .LoadGoalsAsync([fixture.Build.GoalId])).GetTask(fixture.Build.GoalId, fixture.Build.TaskId).WorkerBuildInvocations);
    }
}
