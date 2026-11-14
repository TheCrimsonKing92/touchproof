using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: adapters read unique fixture directories and do not execute learned commands.
public sealed class NonDotnetAdapterLearnedFactListsTests
{
    [Theory]
    [InlineData(ProjectStack.Go)]
    [InlineData(ProjectStack.Node)]
    [InlineData(ProjectStack.Lua)]
    [InlineData(ProjectStack.Declared)]
    public void Discover_NonDotnetAdapters_LeaveAllNewListsEmpty(ProjectStack stack)
    {
        using var fixture = new ProjectOnboardingFixture(stack == ProjectStack.Go ? "go-module" : "declared-no-marker");
        File.WriteAllText(Path.Combine(fixture.Root, "AGENTS.md"), "# Build conventions\n- Keep the source.\n");
        File.WriteAllText(Path.Combine(fixture.Root, "appsettings.json"), "{}");
        File.WriteAllText(Path.Combine(fixture.Root, "PublicAPI.Shipped.txt"), "Api");
        if (stack == ProjectStack.Node)
            File.WriteAllText(Path.Combine(fixture.Root, "package.json"), "{\"name\":\"sample\",\"scripts\":{\"test\":\"vitest\"}}");
        if (stack == ProjectStack.Lua)
            File.WriteAllText(Path.Combine(fixture.Root, "sample.rockspec"), "package = 'sample'\nversion = '1.0-1'\n");
        IProjectDiscoveryAdapter adapter = stack switch
        {
            ProjectStack.Go => new GoProjectDiscoveryAdapter(),
            ProjectStack.Node => new NodeProjectDiscoveryAdapter(),
            ProjectStack.Lua => new LuaProjectDiscoveryAdapter(),
            ProjectStack.Declared => new DeclaredProjectDiscoveryAdapter(),
            _ => throw new ArgumentOutOfRangeException(nameof(stack))
        };

        var model = adapter.Discover(fixture.Root);
        Assert.Equal(stack, model.Stack);
        Assert.Equal(5, model.SchemaVersion);
        Assert.Empty(model.RiskAreas);
        Assert.Empty(model.Conventions);
        Assert.Empty(model.Verifications);
    }
}
