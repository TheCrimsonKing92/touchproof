using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: the loop writer and console capture are AsyncLocal; stores and logs are fixture-local.
public sealed class SelectedExecutionBackstopEventLogTests
{
    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task CoordinatorTransitionsReachConductLogAndQueuedLineNamesAppliedLandings()
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        var path = Path.Combine(fixture.Home, ConductEventLogWriter.CurrentFileName);
        var previous = ConductorBatchLoop.CurrentConductEventLogWriter.Value;
        ConductorBatchLoop.CurrentConductEventLogWriter.Value = new ConductEventLogWriter(path);
        const string first = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string second = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        try
        {
            await AsyncLocalConsoleRouter.Out.CaptureLocalAsync(async () =>
            {
                var coordinator = fixture.CreateCoordinator(line => ConductorBatchLoop.EmitProgress(line));
                await coordinator.NoteLandingAsync("selected-first", first, SelectedExecutionBackstopFixture.Applied, "main-0");
                await coordinator.NoteLandingAsync("full-gate", "full-commit", AcceptanceSelectedExecutionRecord.Disabled, "main-0");
                await coordinator.NoteLandingAsync("selected-second", second, SelectedExecutionBackstopFixture.Applied, "main-0");
                fixture.Run = _ => Task.FromResult(SelectedExecutionBackstopOutcome.Infrastructure("gate unavailable"));
                await Service();
                await coordinator.DrainAsync();
                fixture.Run = _ => Task.FromResult(new SelectedExecutionBackstopOutcome("red", ["Candidate.X.Method"], ["lane"]));
                await Service();
                await coordinator.DrainAsync();
                var miss = (await fixture.Store.ReadAsync())!.OpenMiss!;
                await Service(capacity: false); // An open miss is not a resolution.
                Assert.DoesNotContain(fixture.Lines, line => line.Contains("result=miss-resolved "));
                Assert.True(await fixture.Items.TryResolveAsync(miss.CorrelationKey, "operator verified fix"));
                await Service(capacity: false);
                var resolved = Assert.Single(fixture.Lines.Where(line => line.Contains("result=miss-resolved ")));
                Assert.Null((await fixture.Store.ReadAsync())!.OpenMiss);
                Assert.False(File.Exists(AcceptanceSelectedExecutionSessionSuspension.MarkerPath(fixture.Home)));
                Assert.Contains("from=main-0 to=main-2", resolved);
                Assert.Contains("detail=miss=" + miss.Id, resolved);
                await Service(capacity: false);
                Assert.Single(fixture.Lines.Where(line => line.Contains("result=miss-resolved ")));
                fixture.Run = _ => Task.FromResult(SelectedExecutionBackstopOutcome.Green);
                await Service();
                await coordinator.DrainAsync();
                ConductorBatchLoop.EmitProgress("SELECTED_EXECUTION_BACKSTOP_OTHER result=green");

                Task Service(bool capacity = true) => coordinator.ServiceAsync(() => fixture.Head, [], capacity, false);
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
        foreach (var result in new[] { "queued", "started", "green", "infrastructure-failure", "red", "miss-resolved" })
        {
            var line = fixture.Lines.First(line => line.StartsWith("SELECTED_EXECUTION_BACKSTOP result=" + result + " ", StringComparison.Ordinal));
            Assert.Contains(records, record => record.Detail == line);
        }
        var queued = fixture.Lines.First(line => line.Contains("result=queued "));
        Assert.Contains("applied=selected-first@" + first + ",selected-second@" + second, queued);
        Assert.Contains("goals=selected-first,full-gate,selected-second", queued);
        Assert.DoesNotContain("full-gate@", queued);
    }
}
