using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

internal sealed class GoalRepositoriesTestFixture : IDisposable
{
    internal GoalRepositoryWorktreeProvisionerFixture Home { get; }
    internal GoalRepositoryWorktreeProvisionerFixture Crosswalk { get; }
    internal AgentOrchestratorKernel Kernel { get; } = new();
    internal OrchestratorWorkspace Workspace { get; }
    internal OrchestratorProjectRegistry Registry { get; }
    internal IReadOnlyList<AgentDefinition> Agents { get; } = AgentCatalog.Default().Agents;
    internal WorkerProfileCatalog Profiles { get; } = WorkerProfileCatalog.Default();
    internal static WorkerSandboxOptions DisabledSandbox => new(false,
        WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);

    internal GoalRepositoriesTestFixture(bool useAcceptanceGateSeed = false)
    {
        Home = new(startingPoint: useAcceptanceGateSeed ? MultiRepositoryAcceptanceGateSeedTemplate.RepositoryRole.Home : null);
        Crosswalk = new(startingPoint: useAcceptanceGateSeed ? MultiRepositoryAcceptanceGateSeedTemplate.RepositoryRole.Second : null);
        Registry = new OrchestratorProjectRegistry(Path.Combine(Home.Root, "registry"), Path.Combine(Home.Root, "data"));
        Registry.CreateProject("crosswalk", Crosswalk.CheckoutPath, "main");
        Workspace = OrchestratorWorkspace.ForDirectory(Home.CheckoutPath, Home.CheckoutPath, null, Registry)
            with { IntegrationBranch = "main" };
        StateDbMigrations.EnsureUpToDate(Workspace.SqliteStatePath);
        ModelFunctionCatalogStore.Save(Workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(ModelFunctionPurposes.SpecRefiner, ModelLane.CheapApi,
                new ModelProfile("fixture-refiner", "fixture-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Name: ModelFunctionPurposes.SpecRefiner)]));
    }

    internal GoalRepositoryDeclaration Declaration => new(["default", "crosswalk"],
        new Dictionary<string, string> { ["default"] = Home.CheckoutPath, ["crosswalk"] = Crosswalk.CheckoutPath });

    internal CliExecutionContext Context() => new(Kernel, Workspace, new InMemoryModelProviderRegistry([]),
        Agents, Profiles, null) { ProjectRegistry = Registry };

    internal Goal CreateGoal(AgentRole role = AgentRole.Developer, bool declared = true)
    {
        var task = new TaskSpec(TaskId.New(), "Implement a repository fixture change.", role);
        var goal = Kernel.CreateGoal("Repository fixture change", [task]);
        if (declared) Kernel.DeclareGoalRepositories(goal.Id, Declaration);
        Kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec("Repository fixture change",
            ["A fixture change exists."], VerificationClass.TestVerifiable, [], []));
        Kernel.ActivateGoal(goal.Id, Agents);
        return goal;
    }

    internal string CreateHomeWorktree(Goal goal)
    {
        var path = GoalWorktrees.WorktreePath(Home.CheckoutPath, goal.Id);
        Home.Git("worktree", "add", "-b", "goal/" + goal.Id.Value[..8], path, "main");
        Assert.True(File.Exists(Path.Combine(path, ".git")));
        return path;
    }

    public void Dispose()
    {
        try { Crosswalk.Dispose(); }
        finally { Home.Dispose(); }
    }
}
