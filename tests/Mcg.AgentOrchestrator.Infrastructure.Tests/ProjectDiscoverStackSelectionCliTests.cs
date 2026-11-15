using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: each test owns its sources, workspace, registry, data and output writer.
public sealed class ProjectDiscoverStackSelectionCliTests
{
    [Fact]
    public void GoModRootWritesAGoModel()
    {
        using var workspace = new ProjectOnboardingFixture("solution");
        using var source = new ProjectOnboardingFixture("go-module");
        var registry = new OrchestratorProjectRegistry(Path.Combine(workspace.Root, "registry"), Path.Combine(workspace.Root, "data"));
        using var output = new StringWriter();

        using var crosswalk = new FakeCrosswalkProcess("go");
        var exit = ProjectCliCommand.Execute(["project", "discover", "--root", source.Root], registry, workspace.Root, null, output, crosswalkReaderFactory: () => crosswalk.Reader);

        Assert.Equal(0, exit);
        var modelPath = Path.Combine(OrchestratorWorkspace.ForProject(Path.GetFileName(source.Root), source.Root,
            dataRootDirectory: registry.DataRootDirectory).OrchestratorDirectory, "project-model.json");
        Assert.True(File.Exists(modelPath));
        var json = File.ReadAllText(modelPath);
        Assert.Equal("go", JsonNode.Parse(json)!["stack"]!.GetValue<string>());
        var model = ProjectModelJson.Deserialize(json);
        Assert.Equal(ProjectStack.Go, model.Stack);
        Assert.Equal(5, model.Units.Count);
        Assert.Contains($"Project model: {modelPath}", output.ToString());
    }

    [Fact]
    public void CrosswalkUnreviewedFramework_PrintsAndStoresNoProbeQuestion()
    {
        using var workspace = new ProjectOnboardingFixture("solution");
        using var source = new ProjectOnboardingFixture("declared-no-marker");
        var registry = new OrchestratorProjectRegistry(Path.Combine(workspace.Root, "registry"), Path.Combine(workspace.Root, "data"));
        using var output = new StringWriter();
        using var crosswalk = new FakeCrosswalkProcess("future-stack", "future-runner");
        crosswalk.ListTests("suite.test");

        var exit = ProjectCliCommand.Execute(["project", "discover", "--root", source.Root], registry, workspace.Root, null, output,
            crosswalkReaderFactory: () => crosswalk.Reader);

        Assert.Equal(0, exit);
        var modelPath = Path.Combine(OrchestratorWorkspace.ForProject(Path.GetFileName(source.Root), source.Root,
            dataRootDirectory: registry.DataRootDirectory).OrchestratorDirectory, "project-model.json");
        Assert.True(File.Exists(modelPath));
        var model = ProjectModelJson.Deserialize(File.ReadAllText(modelPath));
        Assert.Equal(ProjectStack.Crosswalk, model.Stack);
        Assert.Contains(model.EnvironmentNeeds, need => need.Name == "crosswalk-test-framework" && need.Value.Value == "future-runner");
        var question = Assert.Single(model.OwnerQuestions, question => question.FactKey == "environment/tools/future-runner");
        Assert.Contains("No environment probe was derived for test framework 'future-runner'.", question.Question);
        Assert.Contains($"Owner question: {question.Question}", output.ToString());
    }

    [Fact]
    public void SolutionRootStillWritesADotnetModel()
    {
        using var workspace = new ProjectOnboardingFixture("solution");
        using var source = new ProjectOnboardingFixture("solution");
        var registry = new OrchestratorProjectRegistry(Path.Combine(workspace.Root, "registry"), Path.Combine(workspace.Root, "data"));
        using var output = new StringWriter();

        using var crosswalk = new FakeCrosswalkProcess("dotnet");
        var exit = ProjectCliCommand.Execute(["project", "discover", "--root", source.Root], registry, workspace.Root, null, output, crosswalkReaderFactory: () => crosswalk.Reader);

        Assert.Equal(0, exit);
        var modelPath = Path.Combine(OrchestratorWorkspace.ForProject(Path.GetFileName(source.Root), source.Root,
            dataRootDirectory: registry.DataRootDirectory).OrchestratorDirectory, "project-model.json");
        Assert.True(File.Exists(modelPath));
        var json = File.ReadAllText(modelPath);
        Assert.Equal("dotnet", JsonNode.Parse(json)!["stack"]!.GetValue<string>());
        var model = ProjectModelJson.Deserialize(json);
        Assert.Equal(ProjectStack.Dotnet, model.Stack);
        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal($"Project model: {modelPath}", lines[0]);
        Assert.Equal("Integration branch: not learned", lines[1]);
        Assert.Equal($"Kept 0 unit measurements; {model.Commands.Count(command => command.TestCommand is not null)} test units have no measured test duration", lines[2]);
        Assert.Equal(model.OwnerQuestions.Select(question => $"Owner question: {question.Question}"), lines.Skip(3));
    }
}
