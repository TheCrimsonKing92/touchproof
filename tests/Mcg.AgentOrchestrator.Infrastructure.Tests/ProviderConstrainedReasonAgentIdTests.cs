using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: per-test directories and injected probes, with no environment mutation or worker launch.
public sealed class ProviderConstrainedReasonAgentIdTests : WorkerDispatchTestSupport
{
    private static readonly WorkerSandboxOptions Sandbox = new(
        false, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);

    [Fact]
    public void DeveloperDispatchRecordsMatchingAgentAndParseableCascade()
    {
        var agent = ConstrainedAgent(AgentRole.Developer) with
        {
            Subscription = new SubscriptionLaunchProfile(
                "codex-cli", AgentCatalog.OpenAiSolSubscriptionModelAlias, AgentCatalog.RoutineSubscriptionReasoningEffort)
        };
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Update one label.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Fix a typo", [task]);
        kernel.RecordGoalPolicyDecision(
            goal.Id,
            "Intake pipeline decision (auto): developer-only; reasons: simple code objective; risk labels: small-task, low-risk.");
        kernel.ActivateGoal(goal.Id, [agent]);
        Assert.Equal(agent.Id, task.AssignedAgentId);

        var dispatch = PrepareDispatch(kernel, goal, task, agent, seedGitMarker: true);

        Assert.Equal(
            "provider-constrained: Developer remains on OpenAI (agent openai-developer); cascade=primary rule=first-round",
            dispatch.ModelSelectionReason);
        Assert.True(CascadeRouteMarker.TryParse(dispatch.ModelSelectionReason, out var marker));
        Assert.Equal(CascadeRouteMarker.Primary, marker.Decision);
    }

    [Fact]
    public void TesterSelectionNamesMatchingAgentBeforeCascade()
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Check the label.", AgentRole.Tester);
        var goal = kernel.CreateGoal("Verify a typo fix", [task]);
        var agent = ConstrainedAgent(AgentRole.Tester);
        kernel.ActivateGoal(goal.Id, [agent]);
        Assert.Equal(agent.Id, task.AssignedAgentId);

        var reason = Resolve(agent, goal, task).Reason;

        Assert.True(CascadeRouteMarker.TryParse(reason, out var marker));
        Assert.Equal(marker.AppendTo("provider-constrained: Tester remains on OpenAI (agent openai-tester)"), reason);
    }

    [Fact]
    public void DeveloperSelectionReportsCaseOnlyAssignmentDifferenceBeforeCascade()
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Update one label.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Fix a typo", [task]);
        var agent = ConstrainedAgent(AgentRole.Developer);
        var storedAgentId = new AgentId("OPENAI-DEVELOPER");
        kernel.ActivateGoal(goal.Id, [agent with { Id = storedAgentId }]);
        Assert.Equal(storedAgentId, task.AssignedAgentId);

        var reason = Resolve(agent, goal, task).Reason;

        Assert.True(CascadeRouteMarker.TryParse(reason, out var marker));
        Assert.Equal(marker.AppendTo(
            "provider-constrained: Developer remains on OpenAI (agent openai-developer, stored assignment OPENAI-DEVELOPER)"), reason);
    }

    [Theory]
    [InlineData("openai-planner", "(agent openai-planner)")]
    [InlineData("previous-planner", "(agent openai-planner, stored assignment previous-planner)")]
    [InlineData(null, "(agent openai-planner)")]
    [InlineData("", "(agent openai-planner)")]
    [InlineData(" \t", "(agent openai-planner)")]
    public void PlannerSelectionReportsOnlyDistinctNonblankAssignments(string? storedId, string expectedSuffix)
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Plan the requested source change.", AgentRole.Planner);
        var goal = kernel.CreateGoal("Plan a label update", [task]);
        var agent = ConstrainedAgent(AgentRole.Planner);
        if (storedId is not null)
        {
            kernel.ActivateGoal(goal.Id, [agent with { Id = new AgentId(storedId) }]);
        }
        Assert.Equal(storedId, task.AssignedAgentId?.Value);

        var reason = Resolve(agent, goal, task).Reason;

        Assert.Equal($"provider-constrained: Planner remains on OpenAI {expectedSuffix}", reason);
    }

    [Fact]
    public void ConstrainedPlannerDispatchKeepsItsOwnSubscriptionProfile()
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Plan the requested source change.", AgentRole.Planner);
        var goal = kernel.CreateGoal("Plan a label update", [task]);
        var agent = ConstrainedAgent(AgentRole.Planner);
        kernel.ActivateGoal(goal.Id, [agent]);

        var unconstrained = Resolve(agent with { IsProviderRoutingConstrained = false }, goal, task);
        Assert.StartsWith("light-role:", unconstrained.Reason, StringComparison.Ordinal);

        var dispatch = PrepareDispatch(kernel, goal, task, agent);

        Assert.Equal(agent.Model.ProviderName, dispatch.ProviderName);
        Assert.Equal(agent.Subscription!.WorkerProfileName, dispatch.WorkerName);
        Assert.Equal(agent.Subscription.ModelAlias, dispatch.ModelName);
    }

    private static AgentDefinition ConstrainedAgent(AgentRole role) =>
        AgentCatalog.Default().GetRequired(role) with { IsProviderRoutingConstrained = true };

    private static WorkerSubscriptionModelResolver.SubscriptionModelSelection Resolve(
        AgentDefinition agent, Goal goal, TaskSpec task) =>
        WorkerSubscriptionModelResolver.ResolveEffectiveSubscriptionModelSelection(
            agent, goal, task, WorkerProviderCatalog.Default(),
            sandboxProbe: () => Sandbox,
            claudeAuthProbe: DispatcherProviderProbeFakes.SignedInClaudeCli,
            profiles: DispatchTestProfiles(),
            commandExists: DispatcherProviderProbeFakes.ProviderCommandsPresent);

    private static TaskDispatchRecord PrepareDispatch(
        AgentOrchestratorKernel kernel, Goal goal, TaskSpec task, AgentDefinition agent, bool seedGitMarker = false)
    {
        var root = CreateTempDirectory();
        var workingDirectory = Path.Combine(root, "repo");
        Directory.CreateDirectory(workingDirectory);
        if (seedGitMarker)
        {
            File.WriteAllText(Path.Combine(workingDirectory, ".git"), "gitdir: ..");
        }

        WorkerProfileDispatcher.PrepareSubscriptionTask(
            kernel, goal, task, [agent], WorkerProfileCatalog.Default(),
            Path.Combine(root, "prompts"), workingDirectory, DateTimeOffset.Parse("2026-07-29T12:00:00Z"),
            sandboxOptions: Sandbox,
            claudeAuthProbe: DispatcherProviderProbeFakes.SignedInClaudeCli,
            commandExists: DispatcherProviderProbeFakes.ProviderCommandsPresent);

        var dispatch = task.LastDispatch;
        Assert.NotNull(dispatch);
        return dispatch;
    }
}
