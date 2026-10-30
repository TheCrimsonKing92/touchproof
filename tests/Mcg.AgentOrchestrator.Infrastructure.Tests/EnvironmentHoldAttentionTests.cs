using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

// Parallel-safe: this test owns its SQLite directory and uses an injected probe; no ambient mutations.
public sealed class EnvironmentHoldAttentionTests
{
    [Fact]
    public async Task HoldWithoutQuestion_IsProjectedSelectedAndPrintedForOperator()
    {
        var directory = Directory.CreateTempSubdirectory("environment-hold-attention-");
        try
        {
            var (kernel, goal) = SimpleGoal();
            var gate = new EnvironmentContractGate(kernel, "project", directory.FullName, () => EnvironmentContractTestFixture.Contract,
                new EnvironmentContractTestFixture.Runner(), _ => EnvironmentContractTestFixture.Environment());
            gate.ProbeAtStart([goal]);
            Assert.Empty(kernel.ExportSnapshot().HumanInputRequests);
            var repository = new SqliteOrchestratorStateRepository(Path.Combine(directory.FullName, "state.db"));
            await repository.SaveAsync(kernel);
            var projection = Assert.IsType<AttentionReadProjection>(await repository.ReadAttentionProjectionAsync());
            var row = Assert.Single(projection.EnvironmentHolds!);
            Assert.Equal(goal.Id.Value, row.GoalId);
            Assert.Equal(EnvironmentHold.State, row.Hold.State);
            var metadata = await repository.ListGoalMetadataAsync();
            var ids = AttentionReadGoalSelection.ReadPostLandingDutyGoalIds(repository, metadata, projection);
            var selection = AttentionReadGoalSelection.Select(metadata, [], [], [], ids);
            Assert.Equal(goal.Id, Assert.Single(selection.GoalIds));
            using var output = new StringWriter();
            Assert.True(EnvironmentHoldAttentionView.Print([goal], output));
            Assert.Contains("owner=operator", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("cargo nextest --version", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("missing nextest output tail", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("retry automatically", output.ToString(), StringComparison.Ordinal);
            Assert.Single(goal.Tasks);
            Assert.NotNull(goal.CurrentHold);
        }
        finally { directory.Delete(recursive: true); }

    }
}
