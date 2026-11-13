using Mcg.AgentOrchestrator.Infrastructure;
using AcceptanceManifest = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifest;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Parallel-safe: each case owns its project home and scopes shard switches with AsyncLocal.
public sealed class NonHomeOmittedChecksManifestTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"version\":1}")]
    [InlineData("{\"engine\":{\"enforceStructuralCoverage\":false}}")]
    public void ForeignManifest_OmittedChecks_LoadsNoDefault(string json)
    {
        using var fixture = new NonHomeSelectionFixture();
        fixture.WriteManifest(json);
        var manifest = AcceptanceManifest.Load(fixture.Worktree, [], fixture.Home);
        Assert.Empty(manifest.Checks);
        Assert.DoesNotContain(AcceptanceManifestCheck.DefaultDotnetTest, manifest.Checks);
    }

    [Fact]
    public void ForeignManifest_OmittedChecks_PreservesOtherDeclarations()
    {
        using var fixture = new NonHomeSelectionFixture();
        fixture.WriteManifest(new
        {
            version = 7,
            forbiddenChangedPathGlobs = new[] { "private/**" },
            toolHomes = new[] { new { variable = "WIDGET_HOME", directory = fixture.Home } }
        });
        var manifest = AcceptanceManifest.Load(fixture.Worktree, [], fixture.Home);
        Assert.Empty(manifest.Checks);
        Assert.Equal(7, manifest.Version);
        Assert.Equal("private/**", Assert.Single(manifest.ForbiddenChangedPathGlobs));
        var home = Assert.Single(manifest.ToolHomes);
        Assert.Equal("WIDGET_HOME", home.Variable);
        Assert.Equal(fixture.Home, home.Directory);
    }

    [Fact]
    public void ForeignGate_OmittedChecks_SchedulesNoUndeclaredDotnetTest()
    {
        using var fixture = new NonHomeSelectionFixture();
        using var switches = AcceptanceShardPolicySwitches.Use(new(false, true));
        fixture.WriteManifest("{\"engine\":{\"enforceStructuralCoverage\":false}}");
        var checks = GoalAcceptanceVerifier.BuildEffectiveAcceptanceChecksForTests(
            fixture.Worktree, [NonHomeSelectionFixture.Source], fixture.Home);
        Assert.Empty(checks);
        Assert.DoesNotContain(checks, check => check.Type == "dotnet-test" ||
            check.Name == AcceptanceManifestCheck.DefaultDotnetTest.Name);
    }
}
