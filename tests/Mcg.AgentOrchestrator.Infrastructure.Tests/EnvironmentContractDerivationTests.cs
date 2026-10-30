using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: pure manifest emission, with no tool execution or process-wide mutation.
public sealed class EnvironmentContractDerivationTests
{
    [Fact]
    public void DeclaredCargoChecks_EmitCargoAndNextestVersionProbes()
    {
        var emitted = LearnedAcceptanceManifest.Build(EnvironmentContractTestFixture.Model(), EnvironmentContractTestFixture.Answers());
        Assert.Empty(emitted.Blockers);
        using var json = JsonDocument.Parse(emitted.Json!);
        Assert.True(json.RootElement.TryGetProperty("environmentContract", out _));
        var contract = EnvironmentContract.FromManifest(emitted.Json!);
        var cargo = Assert.Single(contract.Tools, tool => tool.Name == "cargo");
        Assert.Equal("cargo", cargo.Command);
        Assert.Equal(["--version"], cargo.Arguments);
        var nextest = Assert.Single(contract.Tools, tool => tool.Name == "cargo nextest");
        Assert.Equal("cargo", nextest.Command);
        Assert.Equal(["nextest", "--version"], nextest.Arguments);
    }

    [Fact]
    public void CrosswalkProject_EmitsCrosswalkAndReportedStackTool()
    {
        using var fixture = new ProjectOnboardingFixture("declared-no-marker");
        using var crosswalk = new FakeCrosswalkProcess("jvm", "gradle");
        crosswalk.ListTests("suite.test");
        var adapter = ProjectDiscoveryAdapterSelector.Select(fixture.Root, crosswalk.Reader);
        Assert.IsType<CrosswalkProjectDiscoveryAdapter>(adapter);
        var model = adapter.Discover(fixture.Root);
        var emitted = LearnedAcceptanceManifest.Build(model, EnvironmentContractTestFixture.Answers());
        Assert.Empty(emitted.Blockers);
        var tools = EnvironmentContract.FromManifest(emitted.Json!).Tools;
        Assert.Equal(["--version"], Assert.Single(tools, tool => tool.Name == "crosswalk").Arguments);
        var gradle = Assert.Single(tools, tool => tool.Name == "gradle");
        Assert.Equal("gradle", gradle.Command);
        Assert.Equal(["--version"], gradle.Arguments);
        Assert.DoesNotContain(tools, tool => tool.Name.StartsWith("crosswalk-", StringComparison.Ordinal));
    }

    [Fact]
    public void LegacyHomeManifest_DerivesContractFromChecks()
    {
        var contract = EnvironmentContract.FromManifest("""{"checks":[{"type":"command","command":"git"},{"type":"dotnet-test"}]}""");
        Assert.Equal(["dotnet", "git"], contract.Tools.Select(tool => tool.Name));
        Assert.All(contract.Tools, tool => Assert.Equal(["--version"], tool.Arguments));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NodeProject_EmitsRuntimeAndManagerWithoutMetadataProbes(bool hasTsconfig)
    {
        using var fixture = new NodeProjectDiscoveryAdapterTests.Fixture(hasTsconfig: hasTsconfig);
        var emitted = LearnedAcceptanceManifest.Build(fixture.Discover(), NodeProjectDiscoveryAdapterTests.Fixture.Answers());

        Assert.Empty(emitted.Blockers);
        var tools = EnvironmentContract.FromManifest(emitted.Json!).Tools;
        Assert.Equal(hasTsconfig ? ["crosswalk", "node", "npm", "npx"] : ["crosswalk", "node", "npm"],
            tools.Select(tool => tool.Name));
        Assert.All(tools, tool =>
        {
            Assert.Equal(tool.Name, tool.Command);
            Assert.Equal(["--version"], tool.Arguments);
        });
    }

    [Theory]
    [InlineData("npm")]
    [InlineData("pnpm")]
    [InlineData("yarn")]
    public void PackageManagerNeed_UsesItsToolValue(string manager)
    {
        var model = EnvironmentContractTestFixture.Model(ProjectStack.Node,
            [new(NodeVerificationChecks.PackageManagerFact,
                new(manager, new FactSource("package.json", 1), FactConfidence.High))]);
        var tools = EnvironmentContractDeriver.Derive(JsonSerializer.SerializeToElement(Array.Empty<object>()), model).Tools;

        var tool = Assert.Single(tools);
        Assert.Equal(manager, tool.Name);
        Assert.Equal(manager, tool.Command);
        Assert.Equal(["--version"], tool.Arguments);
    }

    [Theory]
    [InlineData(".config/dotnet-tools.json")]
    [InlineData("dotnet-tools.json")]
    public void DotnetToolManifest_PackageIdsAreNotPathExecutables(string manifest)
    {
        using var fixture = new ProjectOnboardingFixture("solution");
        if (manifest == "dotnet-tools.json")
            File.Move(Path.Combine(fixture.Root, ".config", "dotnet-tools.json"), Path.Combine(fixture.Root, manifest));
        ProjectOnboardingFixture.SeedRemoteDefault(fixture.Root);
        var model = new DotnetProjectDiscoveryAdapter().Discover(fixture.Root);
        Assert.Contains(model.EnvironmentNeeds, need => need.Name == "dotnet-ef" && need.Value.Source.Path == manifest);
        Assert.Contains(model.EnvironmentNeeds, need => need.Name == "sample-tool");

        var emitted = LearnedAcceptanceManifest.Build(model, EnvironmentContractTestFixture.Answers());

        Assert.Empty(emitted.Blockers);
        var tool = Assert.Single(EnvironmentContract.FromManifest(emitted.Json!).Tools);
        Assert.Equal("dotnet", tool.Name);
        Assert.Equal("dotnet", tool.Command);
        Assert.Equal(["--version"], tool.Arguments);
    }

    [Fact]
    public void UnknownNeed_RemainsMetadataUntilOwnerDeclaresATool()
    {
        var model = EnvironmentContractTestFixture.Model(needs:
            [new("deployment-region", new("somewhere", new FactSource("project.json", 1), FactConfidence.High))]);

        var contract = EnvironmentContractDeriver.Derive(JsonSerializer.SerializeToElement(Array.Empty<object>()), model);

        Assert.Empty(contract.Tools);
    }
}
