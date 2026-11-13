using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Each row owns its stores; console capture follows the existing CLI collection.
[Collection(CliTestCollections.ConsoleSerialized)]
public sealed class CliAttentionCrossGoalItemTests : CliTaskQueryTestSupport
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task OpenVerifyAppearsOnceBeforeWaitsThroughSelectiveRead(bool show, bool withWait)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var kernel = new AgentOrchestratorKernel();
            var live = kernel.CreateGoal("Live goal");
            kernel.ActivateGoal(live.Id, AgentCatalog.Default().Agents);
            var unrelated = kernel.CreateGoal("Unrelated goal");
            kernel.ActivateGoal(unrelated.Id, AgentCatalog.Default().Agents);
            if (withWait)
                kernel.RequestHumanInput(live.Id, null, "Operator evidence needed",
                    kind: HumanWaitKind.ProspectiveAcceptanceEvidence);
            Assert.Equal(GoalStatus.Active, live.Status);
            var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
            var item = await store.RaiseAsync(CollaborationItemType.Verify, null, "Cross-goal verification", "Check the miss");
            var repository = new SelectiveQueries(kernel);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            string[] args = show ? ["attention", "show"] : ["attention"];

            var output = CaptureConsole(() => Assert.True(CliAttentionQueryCommand.TryExecute(args, repository,
                workspace, new InMemoryModelProviderRegistry([]), ref agents, ref profiles, ref currentGoal)));

            var lines = output.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
            var row = Assert.Single(lines.Where(line => line.Contains("goal=cross-goal", StringComparison.Ordinal)));
            Assert.Equal($"  [Verify] {item.Id[..8]} status=Raised goal=cross-goal", row);
            Assert.Single(lines.Where(line => line == $"    dismiss: attention dismiss --item {item.Id}"));
            Assert.Single(lines.Where(line => line == "    subject: Cross-goal verification"));
            Assert.DoesNotContain("Attention queue: 0 open", output, StringComparison.Ordinal);
            if (withWait)
            {
                var waitIndex = Array.FindIndex(lines, line => line.StartsWith("Human waits:", StringComparison.Ordinal));
                Assert.True(waitIndex >= 0, "The open human wait must be rendered.");
                Assert.True(Array.IndexOf(lines, row) < waitIndex, "Cross-goal items must precede human waits.");
                Assert.Equal(new[] { live.Id }, Assert.Single(repository.LoadCalls));
            }
            else
            {
                Assert.DoesNotContain("Human waits:", output, StringComparison.Ordinal);
                Assert.Empty(repository.LoadCalls);
            }
            Assert.Equal(0, repository.Probe.FullLoadAttempts);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class SelectiveQueries(AgentOrchestratorKernel kernel) : IOrchestratorStateQueries
    {
        public ProbeStateRepository Probe { get; } = new(kernel) { ThrowOnOutbox = true, SupportsAttentionProjection = true };
        public List<GoalId[]> LoadCalls { get; } = [];

        public Task<AttentionReadProjection?> ReadAttentionProjectionAsync(CancellationToken cancellationToken = default) =>
            Probe.ReadAttentionProjectionAsync(cancellationToken);

        public Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(CancellationToken cancellationToken = default) =>
            Probe.ListGoalMetadataAsync(cancellationToken);

        public Task<IReadOnlyList<HumanInputRequestSnapshot>> ListOpenHumanInputRequestsAsync(
            CancellationToken cancellationToken = default) => Probe.ListOpenHumanInputRequestsAsync(cancellationToken);

        public Task<IReadOnlyList<GoalId>> ListGateTestObligationGoalIdsAsync(
            IReadOnlyCollection<GoalId>? goalIds = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GoalId>>([]);

        public Task<AgentOrchestratorKernel> LoadGoalsAsync(
            IReadOnlyCollection<GoalId> goalIds, CancellationToken cancellationToken = default)
        {
            LoadCalls.Add(goalIds.ToArray());
            return Probe.LoadGoalsAsync(goalIds, cancellationToken);
        }
    }
}
