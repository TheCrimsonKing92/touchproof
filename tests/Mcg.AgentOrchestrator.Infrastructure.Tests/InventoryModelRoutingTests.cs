using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe. Each case uses a private workspace and prepares commands without starting provider processes.
public sealed class InventoryModelRoutingTests
{
    [Fact]
    public void ReassignAgentDispatchesInventoryModelWithoutAddingRosterSeat()
    {
        var fixture = new Fixture();
        ModelInventoryStore.Save(fixture.Workspace.ModelInventoryPath,
            new([new("Anthropic", "claude-haiku-5-5", "claude-cli", ModelDiscoverySource.DeclarationProbe,
                DateTimeOffset.UnixEpoch, ProbeStatus: ModelProbeStatus.Succeeded)], []));

        Assert.True(fixture.Execute(["reassign-agent", "1", "--model", "anthropic/CLAUDE-HAIKU-5-5", "--effort", "low"]));
        var restored = AgentOrchestratorKernel.FromSnapshot(fixture.Kernel.ExportSnapshot());
        var goal = restored.GetGoal(fixture.Goal.Id);
        var task = Assert.Single(goal.Tasks);
        Assert.Equal(fixture.Task.AssignedAgentId, task.AssignedAgentId);
        Assert.Equal("claude-haiku-5-5", InventoryModelAssignment.Parse(task.AssignedAgentId)!.Model);
        Assert.DoesNotContain(fixture.Agents, agent => agent.Id == task.AssignedAgentId);
        Assert.Empty(OfflineAssignedAgentRedelegation.Apply(restored, goal, fixture.Agents, [task.Id]).Blocked);
        var inventory = ModelInventoryStore.Load(fixture.Workspace.ModelInventoryPath);
        Assert.IsType<DispatchReadinessReady>(ConductorDriver.EvaluateConductorReadiness(
            restored, goal, fixture.Agents, fixture.Profiles, DateTimeOffset.UnixEpoch, inventory));
        var blocked = Assert.IsType<DispatchReadinessBlocked>(ConductorDriver.EvaluateConductorReadiness(
            restored, goal, fixture.Agents, fixture.Profiles, DateTimeOffset.UnixEpoch, ModelInventory.Empty));
        Assert.Contains("InventoryAssignmentIneligible", blocked.Reason, StringComparison.Ordinal);

        restored.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "codex-cli", "old command", fixture.Workspace.ExecutionDirectory, DateTimeOffset.UnixEpoch));
        var dispatch = new GoalDispatchOperations(resolveHome: _ => OrchestratorHome.ForInstallRoot(fixture.Workspace.RootDirectory))
            .RefreshPreparedDispatchBeforeStart(
            restored, fixture.Workspace, goal, task, fixture.Agents, fixture.Profiles,
            sandboxOptions: new(false, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget));

        Assert.NotNull(dispatch.Task.LastDispatch);
        Assert.Equal("claude-cli", dispatch.Task.LastDispatch.WorkerName);
        Assert.Equal("claude-haiku-5-5", dispatch.Task.LastDispatch.ModelName);
        Assert.Equal("low", dispatch.Task.LastDispatch.ReasoningEffort);
        Assert.Contains("--model 'claude-haiku-5-5'", dispatch.Task.LastDispatch.Command, StringComparison.Ordinal);
        var automatic = restored.AddTask(goal.Id, AgentRole.Developer, "Another Developer task", fixture.Agents);
        Assert.Equal(fixture.RosterAgent.Id, automatic.AssignedAgentId);
        Assert.DoesNotContain(fixture.Agents, agent => agent.Model.ModelName == "claude-haiku-5-5");
    }

    [Fact]
    public void UnknownModelReturnsTypedHoldWithoutChangingAssignment()
    {
        var fixture = new Fixture();
        var revision = fixture.Task.ConductorRoutingRevision;
        var error = CaptureConsoleError(() =>
            Assert.False(fixture.Execute(["reassign-agent", "1", "--model", "anthropic/not-listed"])));

        Assert.Contains("AGENT_REASSIGNMENT_HOLD code=ModelNotInInventory", error, StringComparison.Ordinal);
        Assert.Contains("anthropic/not-listed", error, StringComparison.Ordinal);
        Assert.Equal(fixture.RosterAgent.Id, fixture.Task.AssignedAgentId);
        Assert.Equal(revision, fixture.Task.ConductorRoutingRevision);
        Assert.Empty(fixture.Task.DispatchHistory);
    }

    [Fact]
    public void IntakePreservesProfileChoiceAndIneligibleAssignmentCannotDispatch()
    {
        var fixture = new Fixture();
        const string model = "vendor/namespaced-model";
        var entries = new[] { "codex-cli", WorkerProfileDispatcher.OpenAiLunaSubscriptionProfileName }
            .Select(profile => new ModelInventoryEntry("OpenAI", model, profile, ModelDiscoverySource.CodexCache, DateTimeOffset.UnixEpoch))
            .ToArray();
        ModelInventoryStore.Save(fixture.Workspace.ModelInventoryPath, new(entries, []));
        var error = CaptureConsoleError(() =>
            Assert.False(fixture.Execute(["reassign-agent", "1", "--model", "openai/" + model])));
        Assert.Contains("code=AmbiguousProfile", error, StringComparison.Ordinal);
        Assert.Equal(fixture.RosterAgent.Id, fixture.Task.AssignedAgentId);

        var overrides = CliCommandHandlers.ApplyRoleAgentOverrides(
            ["simple-goal", "Test intake", "--developer", "model:openai/" + model + "@low", "--profile", "codex-cli"],
            fixture.Agents, fixture.Workspace, fixture.Profiles);
        var selected = Assert.Single(overrides, agent => agent.Role == AgentRole.Developer);
        var intakeTask = new TaskSpec(TaskId.New(), "Test intake", AgentRole.Developer);
        var intakeGoal = fixture.Kernel.CreateGoal("Test intake", [intakeTask]);
        fixture.Kernel.ActivateGoal(intakeGoal.Id, overrides);
        Assert.Equal(selected.Id, intakeTask.AssignedAgentId);
        Assert.True(fixture.Execute(["reassign-agent", "1", "--model", "openai/" + model, "--profile", "codex-cli", "--effort", "low"]));
        var assignment = InventoryModelAssignment.Parse(fixture.Task.AssignedAgentId)!;
        Assert.Equal(model, assignment.Model);
        Assert.Equal("codex-cli", assignment.Profile);
        Assert.Equal("low", assignment.Effort);

        ModelInventoryStore.Save(fixture.Workspace.ModelInventoryPath, new([entries[1]], []));
        var hold = Assert.Throws<DispatchAssignmentHoldException>(() => new GoalDispatchOperations().SubscriptionDispatchTask(
            fixture.Kernel, fixture.Workspace, fixture.Goal, fixture.Task, fixture.Agents, fixture.Profiles));
        Assert.Equal(DispatchAssignmentHoldCode.InventoryAssignmentIneligible, hold.Hold.Code);
        Assert.Contains("OpenAI/" + model, hold.Hold.Message, StringComparison.Ordinal);
        Assert.Equal(selected.Id, fixture.Task.AssignedAgentId);
        Assert.True(fixture.Execute(["reassign-agent", "1", fixture.RosterAgent.Id.Value]));
        Assert.Equal(fixture.RosterAgent.Id, fixture.Task.AssignedAgentId);
    }

    private sealed class Fixture
    {
        public OrchestratorWorkspace Workspace { get; } = OrchestratorWorkspace.ForDirectory(CreateTempDirectory());
        public AgentOrchestratorKernel Kernel { get; } = new();
        public WorkerProfileCatalog Profiles = WorkerProfileCatalog.Default();
        public IReadOnlyList<AgentDefinition> Agents = AgentCatalog.Default().Agents;
        public AgentDefinition RosterAgent => Agents.Single(agent => agent.Role == AgentRole.Developer);
        public TaskSpec Task { get; } = new(TaskId.New(), "Update the parser", AgentRole.Developer);
        public Goal Goal { get; }

        public Fixture()
        {
            Goal = Kernel.CreateGoal("Update the parser", [Task]);
            Kernel.SetGoalRefinedSpec(Goal.Id, new("Update the parser", ["Parser handles the input."],
                VerificationClass.TestVerifiable, [], []));
            Kernel.ActivateGoal(Goal.Id, Agents);
        }

        public bool Execute(string[] args)
        {
            Goal? current = Goal;
            return CliCommandDispatcher.ExecuteCommand(CliArgumentParser.NormalizeArgs(args), Kernel,
                Workspace, ref Agents, new InMemoryModelProviderRegistry([]), ref Profiles, ref current);
        }
    }
}
