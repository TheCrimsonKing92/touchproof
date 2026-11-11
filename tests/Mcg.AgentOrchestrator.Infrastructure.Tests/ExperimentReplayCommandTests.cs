using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;
using static ExperimentReplayTestData;

// Parallel-safe: no provider or dispatch dependency; each invocation has private paths.
public sealed class ExperimentReplayCommandTests
{
    [Fact]
    public void Registration_MutationOrReducedSample_IsRejected()
    {
        using var fixture = new ExperimentReplayTestData();
        var store = fixture.Store();
        store.Register(Registration());
        store.Register(Registration());
        Assert.Throws<InvalidDataException>(() => store.Register(Registration() with { Seed = 99 }));
        Assert.Throws<InvalidDataException>(() => store.Register(Registration() with { Cases = Registration().Cases.Take(19).ToArray() }));
        Assert.Equal(42, store.Registration("registration").Seed);
    }

    [Fact]
    public void Capabilities_ReportIsQueryAndRunIsExecution()
    {
        Assert.Equal(CliCommandCapability.QueryOnly, CliCommandCapabilities.Classify(["experiment-replay", "report", "--run", "r"]));
        Assert.Equal(CliCommandCapability.Execution, CliCommandCapabilities.Classify(["experiment-replay", "run", "--registration", "r"]));
    }

    [Fact]
    public async Task Report_RecordedRun_ReturnsEvidenceWithoutProviderStartup()
    {
        using var fixture = new ExperimentReplayTestData();
        Directory.CreateDirectory(Path.Combine(fixture.Root, "src", "Mcg.AgentOrchestrator.App"));
        File.WriteAllText(Path.Combine(fixture.Root, "src", "Mcg.AgentOrchestrator.App", "Mcg.AgentOrchestrator.App.csproj"), "<Project />");
        var store = fixture.Store();
        store.Register(Registration());
        var run = new ExperimentReplayRun("run", "registration", "running", null, Now, null);
        store.Start(run);
        store.Finish(run with { State = "incomplete", CompletedAt = Now });
        var workspace = new OrchestratorWorkspace(fixture.Root, fixture.Root, fixture.Root, "home", true, "default", false,
            "unused-state.db", "unused-agents", "unused-workers", "unused-prompts", "unused-logs", "unused-continuation", "unused-transcript");
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(0, await CliExperimentReplayCommand.RunAsync(["experiment-replay", "report", "--run", "run", "--json"], workspace, output, error));
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal("incomplete", document.RootElement.GetProperty("run").GetProperty("state").GetString());
        Assert.Equal(20, document.RootElement.GetProperty("registeredCases").GetInt32());
        Assert.Equal("", error.ToString());
        Assert.False(File.Exists(Path.Combine(fixture.Root, "unused-state.db")));
    }

    [Fact]
    public void ValueBound_ZeroOrWrongCorpus_BlocksExpensiveExecution()
    {
        var bound = new CliExperimentReplayCommand.ValueBound("home", "wrong", 0, [], "analysis");
        Assert.Throws<InvalidDataException>(() => CliExperimentReplayCommand.ValidateBound(bound, "home", Registration().Cases));
    }
}
