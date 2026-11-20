using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Collection(TestCollections.CliProcessEnvironment)]
public sealed class GoalIntakeInvalidCoverageKeyTests : CliCommandTestBase
{
    private const string InvalidCoverageMessage = "--backlog-coverage must be full or slice.";

    // Version 1 fingerprints for the same objective with no assigned agents.
    private const string FullFingerprint = "3f9b54135bb4ece2c003504cac28f9a66a6b023b588d8776a945363cd716f3d4";
    private const string SliceFingerprint = "7c21f19d1825bf1b030a8af4a88e6336019a931ba2820a490d1675fdd3eef30a";
    private const string PaddedSliceFingerprint = "b97402add4d71cd833b9fbc62336f84a518d5e7a06d239bc53141b249bec4e95";

    [Theory]
    [InlineData("partial")]
    [InlineData("UNKNOWN")]
    [InlineData("   ")]
    public void Map_rejects_unsupported_supplied_coverage(string coverage)
    {
        var workspace = OrchestratorWorkspace.ForDirectory(CreateTempDirectory());

        var error = Assert.Throws<ArgumentException>(() => GoalIntakeRequestMapper.Map(
            ["goal", "Coverage fingerprint", "--request-key", "coverage-key", "--backlog-coverage", coverage],
            workspace, []));

        Assert.Equal(InvalidCoverageMessage, error.Message);
    }

    [Theory]
    [InlineData("full", FullFingerprint)]
    [InlineData("FULL", FullFingerprint)]
    [InlineData("slice", SliceFingerprint)]
    [InlineData("Slice", SliceFingerprint)]
    [InlineData("sLiCe", SliceFingerprint)]
    [InlineData(" sLiCe ", PaddedSliceFingerprint)]
    public void Map_preserves_fingerprint_for_valid_coverage(string coverage, string expectedFingerprint)
    {
        var workspace = OrchestratorWorkspace.ForDirectory(CreateTempDirectory());

        var request = GoalIntakeRequestMapper.Map(
            ["goal", "Coverage fingerprint", "--request-key", "coverage-key", "--backlog-coverage", coverage],
            workspace, []);

        Assert.NotNull(request);
        Assert.Equal(expectedFingerprint, request.Fingerprint);
    }

    [Fact]
    public async Task Invalid_coverage_leaves_key_available_for_corrected_retry()
    {
        const string requestKey = "backlog-coverage-key";
        var workspace = CreateRefinedWorkspace(CreateTempDirectory());
        var item = await new BacklogStore(workspace.BacklogStorePath).AddAsync("Keyed backlog source");
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var requests = new GoalIntakeRequestStore(workspace.SqliteStatePath);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        string[] args =
        [
            "backlog-intake", item.Id, "--create-simple-goal", "--request-key", requestKey,
            "--backlog-coverage", "partial"
        ];

        var error = Assert.Throws<ArgumentException>(() => CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            args, repository, workspace, ref agents, providers, ref profiles, ref currentGoal)));

        Assert.Equal(InvalidCoverageMessage, error.Message);
        Assert.Null(requests.Get(requestKey));
        Assert.Empty((await repository.LoadAsync()).Goals);

        args[^1] = "slice";
        _ = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            args, repository, workspace, ref agents, providers, ref profiles, ref currentGoal));

        var goal = Assert.Single((await repository.LoadAsync()).Goals);
        var request = requests.Get(requestKey);
        Assert.NotNull(request);
        Assert.Equal(GoalIntakeRequestStates.Created, request.State);
        Assert.Equal(goal.Id.Value, request.GoalId);
    }
}
