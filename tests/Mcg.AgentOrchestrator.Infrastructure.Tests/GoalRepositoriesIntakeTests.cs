using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: registry, SQLite store and both real git repositories are case-owned.
public sealed class GoalRepositoriesIntakeTests
{
    [Fact]
    public async Task Goal_DeclaredRepositories_PersistsRootsAcrossReload()
    {
        using var fixture = new GoalRepositoriesTestFixture();
        var context = fixture.Context();
        Assert.True(CliCommandHandlers.Execute(["goal", "Implement a fixture change", "--pipeline",
            "developer-reviewer", "--repositories", "default,crosswalk"], context));
        var goal = Assert.IsType<Mcg.AgentOrchestrator.Core.Goal>(context.CurrentGoal);
        Assert.Equal(["crosswalk", "default"], goal.Repositories!.Repositories.Keys);
        Assert.Equal(fixture.Home.CheckoutPath, goal.Repositories.CheckoutRoots["default"]);
        Assert.Equal(fixture.Crosswalk.CheckoutPath, goal.Repositories.CheckoutRoots["crosswalk"]);
        var repository = new SqliteOrchestratorStateRepository(fixture.Workspace.SqliteStatePath);
        await repository.SaveAsync(fixture.Kernel);

        var reloaded = (await repository.LoadAsync()).GetGoal(goal.Id);

        Assert.Equal(goal.Repositories.Keys, reloaded.Repositories!.Keys);
        Assert.Equal(fixture.Home.CheckoutPath, reloaded.Repositories.CheckoutRoots["default"]);
        Assert.Equal(fixture.Crosswalk.CheckoutPath, reloaded.Repositories.CheckoutRoots["crosswalk"]);
    }

    [Theory]
    [InlineData("default,missing-project", "missing-project")]
    [InlineData("crosswalk", "default")]
    [InlineData("default,,crosswalk", "empty")]
    [InlineData("", "requires")]
    public void Goal_InvalidDeclaration_RejectsBeforeCreation(string value, string reason)
    {
        using var fixture = new GoalRepositoriesTestFixture();
        var context = fixture.Context();
        var error = Assert.Throws<ArgumentException>(() => CliCommandHandlers.Execute(
            ["goal", "Implement a fixture change", "--repositories", value], context));
        Assert.Contains(reason, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fixture.Kernel.Goals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Goal_ImplicitOrExplicitDefault_KeepsLegacyDeclaration(bool explicitDefault)
    {
        using var fixture = new GoalRepositoriesTestFixture();
        var context = fixture.Context();
        var parts = new List<string> { "goal", "Implement a fixture change", "--pipeline", "developer-reviewer" };
        if (explicitDefault) parts.AddRange(["--repositories", "default"]);
        Assert.True(CliCommandHandlers.Execute(parts, context));
        Assert.Null(context.CurrentGoal!.Repositories);
    }

    [Fact]
    public void Resolve_RepeatedMixedCaseKeys_UsesCanonicalSet()
    {
        using var fixture = new GoalRepositoriesTestFixture();
        var declaration = GoalRepositoriesArgument.Resolve(["goal", "change", "--repositories",
            "DEFAULT,Crosswalk,crosswalk,default"], fixture.Workspace, fixture.Registry);
        Assert.Equal(["crosswalk", "default"], declaration!.Keys);
    }
}
