using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using static InfrastructureTestSupport;

// Parallel-safe: every registry and project path belongs to this fixture.
public sealed class ProjectRegistryFleetFieldsTests : IDisposable
{
    private readonly string directory = CreateTempDirectory();
    private OrchestratorProjectRegistry Registry => new(Path.Combine(directory, "registry"), Path.Combine(directory, "data"));

    [Fact]
    public void RoundTrip_EnabledAndRequirements_PreservesBothProjectReaders()
    {
        var registry = Registry;
        registry.CreateProject("sample", directory);
        ProjectToolRequirement[] requirements = [new("JDK", "JAVA_HOME"), new("lua")];
        registry.SetFleetEnabled("sample", true);
        registry.SetToolRequirements("sample", requirements);

        var loaded = Registry.GetRequiredProject("sample");
        Assert.True(loaded.Enabled);
        Assert.Equal(requirements, loaded.ToolRequirements);
        var existing = Registry.ResolveProjectForCreation("sample", directory, out var registered);
        Assert.True(registered);
        Assert.True(existing.Enabled);
        Assert.Equal(requirements, existing.ToolRequirements);
        using var json = JsonDocument.Parse(File.ReadAllText(registry.RegistryPath));
        var entries = json.RootElement.GetProperty("Projects")[0].GetProperty("ToolRequirements");
        Assert.False(entries[1].TryGetProperty("EnvironmentVariable", out _));
    }

    [Fact]
    public void LegacyFile_UnrelatedRewrite_PreservesDefaultsAndOmitsUnsetFields()
    {
        var registry = Registry;
        Directory.CreateDirectory(registry.RegistryDirectory);
        File.WriteAllText(registry.RegistryPath, JsonSerializer.Serialize(new
        {
            Projects = new[] { new { Name = "legacy", RootDirectory = directory } }
        }));
        var project = registry.GetRequiredProject("legacy");
        Assert.False(project.Enabled);
        Assert.Empty(project.ToolRequirements);
        registry.SetUseLearnedIntegrationBranch("legacy", false);
        AssertUnset(registry);
        registry.SetFleetEnabled("legacy", true);
        registry.SetToolRequirements("legacy", [new("JDK", "JAVA_HOME")]);
        registry.SetFleetEnabled("legacy", false);
        registry.SetToolRequirements("legacy", []);
        AssertUnset(registry);
    }

    [Fact]
    public void FleetCommands_EnableRequireAndClear_UpdateRegistryThroughProjectSurface()
    {
        var registry = Registry;
        registry.CreateProject("sample", directory);
        using var output = new StringWriter();
        int Execute(params string[] arguments) => ProjectCliCommand.Execute(arguments, registry,
            directory, null, discoveryOutput: output);
        Assert.Equal(0, Execute("project", "fleet", "enable", "sample"));
        Assert.Equal(0, Execute("project", "fleet", "require", "sample", "JDK", "--env", "JAVA_HOME"));
        Assert.True(registry.GetRequiredProject("sample").Enabled);
        Assert.Equal(new ProjectToolRequirement("JDK", "JAVA_HOME"),
            Assert.Single(registry.GetRequiredProject("sample").ToolRequirements));
        Assert.Equal(0, Execute("project", "fleet", "enable", "sample", "--off"));
        Assert.Equal(0, Execute("project", "fleet", "require", "sample", "--clear"));
        AssertUnset(registry);
    }

    [Theory]
    [InlineData("default")]
    [InlineData("unknown")]
    public void FleetSetters_UnregisteredProject_RejectWithoutCreatingRegistry(string name)
    {
        var registry = Registry;
        Assert.Throws<ArgumentException>(() => registry.SetFleetEnabled(name, true));
        Assert.Throws<ArgumentException>(() => registry.SetToolRequirements(name, []));
        Assert.False(File.Exists(registry.RegistryPath));
    }

    [Fact]
    public void Requirements_DuplicateVariables_RejectWithoutChangingStoredRequirements()
    {
        var registry = Registry;
        registry.CreateProject("sample", directory);
        var before = File.ReadAllText(registry.RegistryPath);
        Assert.Throws<ArgumentException>(() => registry.SetToolRequirements("sample",
            [new("JDK", "JAVA_HOME"), new("other", "java_home")]));
        Assert.Equal(before, File.ReadAllText(registry.RegistryPath));
    }

    private static void AssertUnset(OrchestratorProjectRegistry registry)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(registry.RegistryPath));
        var entry = json.RootElement.GetProperty("Projects")[0];
        Assert.False(entry.TryGetProperty("Enabled", out _));
        Assert.False(entry.TryGetProperty("ToolRequirements", out _));
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
