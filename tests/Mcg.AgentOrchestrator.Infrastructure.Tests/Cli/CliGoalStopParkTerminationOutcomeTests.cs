using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(CliTestCollections.ConsoleSerialized)]
public sealed class CliGoalStopParkTerminationOutcomeTests : CliGoalStopAliasTestSupport
{
    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Park_MixedReceipts_CountsConfirmedAndWarns(bool inMemory)
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = await CreateActiveSeed(root, 2);
            var kernel = await seed.Repository.LoadAsync();
            var calls = new List<int>();
            using var seam = RecordTerminations(calls, pid => pid == 900001
                ? GoalWorkerTerminationOutcome.ConfirmedTerminated
                : GoalWorkerTerminationOutcome.TerminationUnconfirmed);

            var result = RunPark(seed, kernel, inMemory);

            Xunit.Assert.Null(result.Error);
            Xunit.Assert.True(result.Changed);
            Xunit.Assert.Equal([900001, 900002], calls);
            Xunit.Assert.Equal(string.Join(Environment.NewLine,
                $"Goal parked {seed.GoalId.Value[..8]}.",
                "Cancelled running dispatches: 1", "Termination unconfirmed: 1",
                $"  {seed.TaskIds[1].Value[..8]} (pid 900002)",
                "Resolved human waits: 0", "Resolved attention items: 0",
                "Goal worktree is not safe to mutate while those workers may be running.", ""), result.Output);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Park_ConfirmedReceipts_PreservesOutput(bool inMemory)
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = await CreateActiveSeed(root, 2);
            var kernel = await seed.Repository.LoadAsync();
            var calls = new List<int>();
            using var seam = RecordTerminations(calls, _ => GoalWorkerTerminationOutcome.ConfirmedTerminated);

            var result = RunPark(seed, kernel, inMemory);

            Xunit.Assert.Null(result.Error);
            Xunit.Assert.True(result.Changed);
            Xunit.Assert.Equal([900001, 900002], calls);
            Xunit.Assert.Equal(string.Join(Environment.NewLine,
                $"Goal parked {seed.GoalId.Value[..8]}.", "Cancelled running dispatches: 2",
                "Resolved human waits: 0", "Resolved attention items: 0", ""), result.Output);
            Xunit.Assert.DoesNotContain("Termination unconfirmed:", result.Output);
            Xunit.Assert.DoesNotContain("not safe to mutate", result.Output);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Park_UnconfirmedReceipt_CommitsAndCompletes(bool inMemory)
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = await CreateActiveSeed(root, 1);
            var kernel = await seed.Repository.LoadAsync();
            var calls = new List<int>();
            using var seam = RecordTerminations(calls, _ => GoalWorkerTerminationOutcome.TerminationUnconfirmed);

            var result = RunPark(seed, kernel, inMemory);

            Xunit.Assert.Null(result.Error);
            Xunit.Assert.True(result.Changed);
            Xunit.Assert.Equal([900001], calls);
            if (inMemory)
            {
                var parked = kernel.GetGoal(seed.GoalId);
                Xunit.Assert.Equal(GoalStatus.Parked, parked.Status);
                Xunit.Assert.True(Xunit.Assert.Single(parked.Tasks).LastProcess!.WasCancelled);
            }
            else
            {
                var parked = (await seed.Repository.LoadGoalAsync(seed.GoalId))!;
                Xunit.Assert.Equal(GoalStatus.Parked, parked.Status);
                Xunit.Assert.True(Xunit.Assert.Single(parked.Tasks).LastProcess!.WasCancelled);
            }
            Xunit.Assert.Contains("Cancelled running dispatches: 0", result.Output);
            Xunit.Assert.Contains("Termination unconfirmed: 1", result.Output);
            Xunit.Assert.EndsWith("Goal worktree is not safe to mutate while those workers may be running." +
                Environment.NewLine, result.Output);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static CommandResult RunPark(ParkSeed seed, AgentOrchestratorKernel kernel, bool inMemory)
    {
        if (!inMemory) return RunCommand(StopParts(seed, "park"), seed.Repository, seed.Workspace);

        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? current = null;
        var changed = false;
        Exception? error = null;
        var output = CaptureConsole(() =>
        {
            try
            {
                changed = CliCommandDispatcher.ExecuteCommand(StopParts(seed, "park"), kernel, seed.Workspace,
                    ref agents, new InMemoryModelProviderRegistry([]), ref profiles, ref current);
            }
            catch (Exception exception) { error = exception; }
        });
        return new CommandResult(changed, output, error, current);
    }
}
