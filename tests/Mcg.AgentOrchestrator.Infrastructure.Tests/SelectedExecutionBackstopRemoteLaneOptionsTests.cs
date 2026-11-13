using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its event log directory; no remote executor is started.
public sealed class SelectedExecutionBackstopRemoteLaneOptionsTests
{
    [Fact]
    public void BackstopOptionsEnableRemoteLanesAndWriteConductEvents()
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        var writer = new ConductEventLogWriter(Path.Combine(fixture.Home, ConductEventLogWriter.CurrentFileName));
        var request = new SelectedExecutionBackstopRequest("request", "from", "to", 1, ["goal"], fixture.Now);
        var options = SelectedExecutionBackstopGateRunner.CreateExecutionOptions(writer, request, fixture.Home, "integration");

        Assert.True(options.CohortRemoteLanes);
        Assert.Equal(request.Id, options.GateRunIdentity);
        Assert.Equal(fixture.Home, options.ProjectHomeDirectory);
        Assert.Equal("integration", options.IntegrationBranch);
        Assert.NotNull(options.RemoteLaneEventSink);
        const string detail = "REMOTE_LANE executor=one lane=process outcome=accepted";
        options.RemoteLaneEventSink(detail);
        using var document = JsonDocument.Parse(Assert.Single(File.ReadAllLines(writer.CurrentPath)));
        Assert.Equal("remote-lane", document.RootElement.GetProperty("eventKind").GetString());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("goalId").ValueKind);
        Assert.Equal(detail, document.RootElement.GetProperty("detail").GetString());
    }
}
