using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: each test owns its registry, project and settings files; no global state changes.
public sealed class ProjectForgeIdentityAnswerCliTests
{
    private static readonly DateTimeOffset Recorded = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Answer_ExternalSettings_PersistsFactAndReachesWorkspace()
    {
        using var home = new ProjectOnboardingFixture("no-solution");
        using var source = new ProjectOnboardingFixture("solution");
        var registry = new OrchestratorProjectRegistry(Path.Combine(home.Root, "registry"), Path.Combine(home.Root, "data"));
        registry.CreateProject("sample", source.Root);
        var settings = Path.Combine(home.Root, "identity.json");
        File.WriteAllText(settings, "{\"kind\":\"github-app\"}");
        using var output = new StringWriter();

        var exit = ProjectAnswerCommand.Execute(
            ["project", "answer", "sample", "forge-identity", settings, "--reason", "bot publishing"],
            registry, home.Root, null, output, () => Recorded);

        Assert.Equal(0, exit);
        Assert.Contains("Recorded owner answer: forge-identity = " + settings, output.ToString());
        var reopened = new OrchestratorProjectRegistry(registry.RegistryDirectory, registry.DataRootDirectory);
        var project = reopened.GetRequiredProject("sample");
        Assert.Equal(settings, project.ForgeIdentity);
        Assert.Equal(new OwnerAnswer("forge-identity", settings, "bot publishing", Recorded), project.ForgeIdentityAnswer);
        Assert.Equal(settings, project.ResolveWorkspace().ForgeIdentity);
        Assert.Equal(settings, Assert.Single(reopened.ListProjects()).ForgeIdentity);
        Assert.Equal("direct", project.Delivery);
        Assert.Null(project.DeliveryAnswer);
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("missing")]
    [InlineData("directory")]
    [InlineData("repository")]
    [InlineData("worktree")]
    public void Answer_InvalidLocation_RefusesWithoutChangingRegistry(string location)
    {
        using var home = new ProjectOnboardingFixture("no-solution");
        using var source = new ProjectOnboardingFixture("solution");
        var registry = new OrchestratorProjectRegistry(Path.Combine(home.Root, "registry"), Path.Combine(home.Root, "data"));
        registry.CreateProject("sample", source.Root);
        var settings = location switch
        {
            "relative" => "identity.json",
            "directory" => home.Root,
            "repository" or "worktree" => Path.Combine(source.Root, "identity.json"),
            _ => Path.Combine(home.Root, "missing.json")
        };
        if (location is "repository" or "worktree")
        {
            File.WriteAllText(settings, "{}");
            if (location == "repository") Directory.CreateDirectory(Path.Combine(source.Root, ".git"));
            else File.WriteAllText(Path.Combine(source.Root, ".git"), "gitdir: elsewhere");
        }
        var before = File.ReadAllBytes(registry.RegistryPath);
        using var output = new StringWriter();

        var exit = ProjectAnswerCommand.Execute(["project", "answer", "sample", "forge-identity", settings],
            registry, home.Root, null, output, () => Recorded);

        Assert.Equal(1, exit);
        Assert.StartsWith("Refused: forge-identity", output.ToString());
        Assert.Equal(before, File.ReadAllBytes(registry.RegistryPath));
        Assert.Null(registry.GetRequiredProject("sample").ResolveWorkspace().ForgeIdentity);
    }
}
