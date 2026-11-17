using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: console capture and the loop writer are AsyncLocal; stores and logs are fixture-local.
public sealed class SelectedExecutionBackstopEventLogTests
{
    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task GateMissTransitionsReachConductLog()
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        var path = Path.Combine(fixture.Home, ConductEventLogWriter.CurrentFileName);
        var previous = ConductorBatchLoop.CurrentConductEventLogWriter.Value;
        ConductorBatchLoop.CurrentConductEventLogWriter.Value = new ConductEventLogWriter(path);
        try
        {
            await AsyncLocalConsoleRouter.Out.CaptureLocalAsync(async () =>
            {
                var coordinator = fixture.CreateCoordinator(line => ConductorBatchLoop.EmitProgress(line));
                await coordinator.NoteLandingAsync("selected", "main-1", SelectedExecutionBackstopFixture.Applied, "main-0");
                var detector = new SelectedExecutionGateMissDetector(coordinator);
                await detector.ObserveAsync("gate", Arm(FindingEvidenceArm.Candidate), Arm(FindingEvidenceArm.Baseline));
                var miss = Assert.IsType<SelectedExecutionBackstopMissRecord>((await fixture.Store.ReadAsync())!.OpenMiss);
                await coordinator.ServiceAsync();
                Assert.DoesNotContain(fixture.Lines, line => line.Contains("result=miss-resolved "));
                Assert.True(await fixture.Items.TryResolveAsync(miss.CorrelationKey, "operator verified fix"));
                await coordinator.ServiceAsync();
                var resolved = Assert.Single(fixture.Lines.Where(line => line.Contains("result=miss-resolved ")));
                Assert.Null((await fixture.Store.ReadAsync())!.OpenMiss);
                Assert.False(File.Exists(AcceptanceSelectedExecutionSessionSuspension.MarkerPath(fixture.Home)));
                Assert.Contains("from=main-1 to=main-1", resolved);
                Assert.Contains("detail=miss=" + miss.Id, resolved);
                await coordinator.ServiceAsync();
                Assert.Single(fixture.Lines.Where(line => line.Contains("result=miss-resolved ")));
                ConductorBatchLoop.EmitProgress("SELECTED_EXECUTION_BACKSTOP_OTHER result=green");
            });
        }
        finally { ConductorBatchLoop.CurrentConductEventLogWriter.Value = previous; }

        var records = File.ReadAllLines(path).Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!).ToArray();
        Assert.Equal(fixture.Lines.ToArray(), records.Select(record => record.Detail).ToArray());
        Assert.All(records, record =>
        {
            Assert.Equal("selected-execution-backstop", record.EventKind);
            Assert.Null(record.GoalId);
        });
        foreach (var result in new[] { "gate-miss", "miss-raised", "miss-resolved" })
            Assert.Contains(records, record => record.Detail!.StartsWith(
                "SELECTED_EXECUTION_BACKSTOP result=" + result + " ", StringComparison.Ordinal));
    }

    private static FindingEvidenceArmReceipt Arm(FindingEvidenceArm arm) =>
        new(arm, "gate-sha", FindingEvidenceArmDisposition.Red,
            Accepted: true, Passed: false, Summary: "synthetic gate failure",
            FailingTestIdentities: ["Candidate.X.First"], TestResultPaths: []);
}
