using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalIntakeRequestMapperTests : CliCommandTestBase
{
    [Xunit.Fact]
    public void Map_without_epic_preserves_pre_change_fingerprint()
    {
        var workspace = OrchestratorWorkspace.ForDirectory(CreateTempDirectory());

        var request = GoalIntakeRequestMapper.Map(
            ["goal", "compat", "--request-key", "compat-key"], workspace, [])!;

        Xunit.Assert.Equal("ece42f02fd93e81bf8f5b5de3f9635c46de592fde4ad874f8e3d9f2b69367a6b", request.Fingerprint);
        Xunit.Assert.Null(request.RequestedEpicId);
    }

    [Xunit.Fact]
    public async Task Map_uses_canonical_epic_id_in_fingerprint()
    {
        var workspace = OrchestratorWorkspace.ForDirectory(CreateTempDirectory());
        var portfolio = new PortfolioStore(workspace.PortfolioStorePath);
        var epic = await portfolio.AddEpicAsync("Intake epic");
        var otherEpic = await portfolio.AddEpicAsync("Other epic");
        var args = new[] { "goal", "Map an epic", "--request-key", "epic-key" };

        var byId = GoalIntakeRequestMapper.Map([..args, "--epic", epic.Id], workspace, [])!;
        var byPrefix = GoalIntakeRequestMapper.Map([..args, "--epic", epic.Id[..^1]], workspace, [])!;
        var byTitle = GoalIntakeRequestMapper.Map([..args, $"--epic={epic.Title}"], workspace, [])!;
        var byOtherEpic = GoalIntakeRequestMapper.Map([..args, "--epic", otherEpic.Id], workspace, [])!;

        Xunit.Assert.All(new[] { byId, byPrefix, byTitle }, request =>
        {
            Xunit.Assert.Equal(epic.Id, request.RequestedEpicId);
            Xunit.Assert.Equal(byId.Fingerprint, request.Fingerprint);
        });
        Xunit.Assert.Equal(otherEpic.Id, byOtherEpic.RequestedEpicId);
        Xunit.Assert.NotEqual(byId.Fingerprint, byOtherEpic.Fingerprint);
    }

    [Xunit.Fact]
    public void Map_simple_aliases_have_equivalent_payload()
    {
        var workspace = OrchestratorWorkspace.ForDirectory(CreateTempDirectory());
        var agents = AgentCatalog.Default().Agents;

        var goalAlias = GoalIntakeRequestMapper.Map(
            ["goal", "One task", "--simple", "--request-key", "alias-a"],
            workspace,
            agents)!;
        var direct = GoalIntakeRequestMapper.Map(
            ["simple-goal", "One task", "--request-key", "alias-b"],
            workspace,
            agents)!;

        Xunit.Assert.Equal("simple", goalAlias.Mode);
        Xunit.Assert.Equal(goalAlias.Fingerprint, direct.Fingerprint);
    }

    [Xunit.Theory]
    [Xunit.InlineData("pipeline")]
    [Xunit.InlineData("run")]
    [Xunit.InlineData("coverage")]
    [Xunit.InlineData("agent")]
    public void Map_effectful_inputs_change_fingerprint(string changedInput)
    {
        var workspace = OrchestratorWorkspace.ForDirectory(CreateTempDirectory());
        var agents = AgentCatalog.Default().Agents;
        var baseline = new List<string>
        {
            "goal", "Effectful payload", "--request-key", "same-key",
            "--backlog-item", "abc", "--backlog-coverage", "slice"
        };
        var changed = new List<string>(baseline);
        switch (changedInput)
        {
            case "pipeline":
                changed.AddRange(["--pipeline", "five-role"]);
                break;
            case "run":
                changed.Add("--run");
                break;
            case "coverage":
                changed[changed.IndexOf("slice")] = "full";
                break;
            case "agent":
                changed.AddRange(["--developer", AgentCatalog.Default().GetRequired(AgentRole.Developer).Id.Value]);
                break;
        }

        var first = GoalIntakeRequestMapper.Map(baseline, workspace, agents)!;
        var second = GoalIntakeRequestMapper.Map(changed, workspace, agents)!;

        Xunit.Assert.NotEqual(first.Fingerprint, second.Fingerprint);
    }

    [Xunit.Fact]
    public async Task GoalIntake_epic_aliases_replay_and_a_different_epic_conflicts()
    {
        var workspace = CreateRefinedWorkspace(CreateTempDirectory());
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var portfolio = new PortfolioStore(workspace.PortfolioStorePath);
        var epic = await portfolio.AddEpicAsync("Intake epic");
        var otherEpic = await portfolio.AddEpicAsync("Other epic");
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        var args = new[] { "goal", "Implement keyed epic intake", "--request-key", "epic-alias" };
        bool Execute(params string[] epicArgs) => CliPersistentStateRunner.ExecuteCommand(
            [..args, ..epicArgs], repository, workspace, ref agents, providers, ref profiles, ref currentGoal);

        var queued = CaptureConsole(() => Xunit.Assert.False(Execute("--epic", epic.Id)));
        Xunit.Assert.Contains("\"state\":\"still-committing\"", queued);
        KeyedGoalCreationIntentTests.ApplyWorkspace(workspace);
        var original = Xunit.Assert.Single((await repository.LoadAsync()).Goals);
        _ = CaptureConsole(() => Xunit.Assert.False(Execute("--epic", epic.Id[..^1])));
        Xunit.Assert.Equal(original.Id, currentGoal!.Id);
        _ = CaptureConsole(() => Xunit.Assert.False(Execute($"--epic={epic.Title}")));
        Xunit.Assert.Equal(original.Id, currentGoal!.Id);

        var error = Xunit.Assert.Throws<InvalidOperationException>(() => Execute("--epic", otherEpic.Id));
        Xunit.Assert.StartsWith("GOAL_INTAKE_PAYLOAD_CONFLICT requestKey=epic-alias existingState=created", error.Message);
        Xunit.Assert.Contains($"requestedEpic={otherEpic.Id}", error.Message);
        Xunit.Assert.Equal(original.Id, Xunit.Assert.Single((await repository.LoadAsync()).Goals).Id);
        Xunit.Assert.Equal(epic.Id, (await portfolio.GetGoalMembershipAsync(original.Id.Value))!.EpicId);
    }

    [Xunit.Fact]
    public async Task GoalIntake_historical_epic_fingerprint_conflict_names_the_requested_epic()
    {
        const string requestKey = "historical-epic";
        var workspace = CreateRefinedWorkspace(CreateTempDirectory());
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var portfolio = new PortfolioStore(workspace.PortfolioStorePath);
        var epic = await portfolio.AddEpicAsync("Historical epic");
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        var args = new[] { "goal", "Implement historical epic intake", "--request-key", requestKey };

        // Historical intake stored the epic-free fingerprint even when it assigned an epic.
        _ = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            args, repository, workspace, ref agents, providers, ref profiles, ref currentGoal));
        KeyedGoalCreationIntentTests.ApplyWorkspace(workspace);
        var original = Xunit.Assert.Single((await repository.LoadAsync()).Goals);
        await portfolio.AssignGoalToEpicAsync(original.Id.Value, epic.Id);
        var requestStore = new GoalIntakeRequestStore(workspace.SqliteStatePath);
        var historicalRecord = requestStore.Get(requestKey);
        Xunit.Assert.Equal(original.Id.Value, historicalRecord!.GoalId);

        var error = Xunit.Assert.Throws<InvalidOperationException>(() => CliPersistentStateRunner.ExecuteCommand(
            [..args, "--epic", epic.Title], repository, workspace, ref agents, providers, ref profiles, ref currentGoal));

        Xunit.Assert.Equal(
            $"GOAL_INTAKE_PAYLOAD_CONFLICT requestKey={requestKey} existingState=created " +
            $"requestedEpic={epic.Id} detail=stored fingerprint differs from the request including this epic",
            error.Message);
        Xunit.Assert.Equal(historicalRecord, requestStore.Get(requestKey));
        Xunit.Assert.Equal(original.Id, Xunit.Assert.Single((await repository.LoadAsync()).Goals).Id);
        Xunit.Assert.Equal(epic.Id, (await portfolio.GetGoalMembershipAsync(original.Id.Value))!.EpicId);
    }

    [Xunit.Fact]
    public async Task GoalIntake_unmatched_epic_does_not_reserve_the_key()
    {
        const string requestKey = "unmatched-epic";
        var workspace = CreateRefinedWorkspace(CreateTempDirectory());
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var portfolio = new PortfolioStore(workspace.PortfolioStorePath);
        var epic = await portfolio.AddEpicAsync("Available epic");
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        var args = new[] { "goal", "Implement corrected epic intake", "--request-key", requestKey };
        bool Execute(string epicReference) => CliPersistentStateRunner.ExecuteCommand(
            [..args, "--epic", epicReference], repository, workspace, ref agents, providers, ref profiles, ref currentGoal);

        var error = Xunit.Assert.Throws<InvalidOperationException>(() => Execute("missing-epic"));

        Xunit.Assert.Equal("--epic 'missing-epic': No epic matched; nothing was created.", error.Message);
        var requestStore = new GoalIntakeRequestStore(workspace.SqliteStatePath);
        Xunit.Assert.Null(requestStore.Get(requestKey));
        Xunit.Assert.Empty((await repository.LoadAsync()).Goals);

        var queued = CaptureConsole(() => Xunit.Assert.False(Execute(epic.Title)));
        Xunit.Assert.Contains("\"state\":\"still-committing\"", queued);
        KeyedGoalCreationIntentTests.ApplyWorkspace(workspace);
        var created = Xunit.Assert.Single((await repository.LoadAsync()).Goals);
        Xunit.Assert.Equal(created.Id.Value, requestStore.Get(requestKey)!.GoalId);
        Xunit.Assert.Equal(epic.Id, (await portfolio.GetGoalMembershipAsync(created.Id.Value))!.EpicId);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task GoalIntake_created_replay_repairs_only_missing_epic_membership(bool alreadyAssigned)
    {
        const string requestKey = "interrupted-epic";
        var workspace = CreateRefinedWorkspace(CreateTempDirectory());
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var portfolio = new PortfolioStore(workspace.PortfolioStorePath);
        var epic = await portfolio.AddEpicAsync("Requested epic");
        var otherEpic = await portfolio.AddEpicAsync("Operator-assigned epic");
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        var args = new[] { "goal", "Recover interrupted epic intake", "--request-key", requestKey, "--epic", epic.Title, "--summary" };
        bool Execute() => CliPersistentStateRunner.ExecuteCommand(
            args, repository, workspace, ref agents, providers, ref profiles, ref currentGoal);

        var queued = CaptureConsole(() => Xunit.Assert.False(Execute()));
        Xunit.Assert.Contains("\"state\":\"still-committing\"", queued);
        using (GoalCreationSideEffectDelivery.UseBeforeEffectDelivery(_ =>
            throw new IOException("Injected interruption before epic assignment.")))
        {
            var application = KeyedGoalCreationIntentTests.ApplyWorkspace(workspace, expectedExitCode: 1);
            Xunit.Assert.Contains("GOAL_CREATE_DELIVERY_INCOMPLETE ", application);
            Xunit.Assert.Contains("Injected interruption before epic assignment.", application);
        }

        var created = Xunit.Assert.Single((await repository.LoadAsync()).Goals);
        var record = new GoalIntakeRequestStore(workspace.SqliteStatePath).Get(requestKey);
        Xunit.Assert.Equal(GoalIntakeRequestStates.Created, record!.State);
        Xunit.Assert.Equal(created.Id.Value, record.GoalId);
        Xunit.Assert.Null(await portfolio.GetGoalMembershipAsync(created.Id.Value));

        if (alreadyAssigned)
            await portfolio.AssignGoalToEpicAsync(created.Id.Value, otherEpic.Id);
        var expectedEpic = alreadyAssigned ? otherEpic : epic;
        var firstReplay = CaptureConsole(() => Xunit.Assert.False(Execute()));
        var membership = await portfolio.GetGoalMembershipAsync(created.Id.Value);
        Xunit.Assert.Equal(expectedEpic.Id, membership!.EpicId);
        Xunit.Assert.Equal(alreadyAssigned ? 0 : 1, firstReplay.Split("Assigned goal ").Length - 1);
        Xunit.Assert.Contains($"Epic: {expectedEpic.Id}", firstReplay);
        Xunit.Assert.Equal(created.Id, Xunit.Assert.Single((await repository.LoadAsync()).Goals).Id);

        var secondReplay = CaptureConsole(() => Xunit.Assert.False(Execute()));
        Xunit.Assert.DoesNotContain("Assigned goal ", secondReplay);
        Xunit.Assert.Equal(membership, await portfolio.GetGoalMembershipAsync(created.Id.Value));
        Xunit.Assert.Equal(created.Id, Xunit.Assert.Single((await repository.LoadAsync()).Goals).Id);
    }
}
