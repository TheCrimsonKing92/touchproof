using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: all state and output belong to this test; the loop has no external work.
public sealed class SelectedExecutionBackstopRetirementTests(ITestOutputHelper output) : ConductorBatchLoopTests(output)
{
    [Fact]
    public async Task RepeatedServiceWithLandingsNeverQueuesOrStartsARun()
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        await fixture.Land();
        await fixture.Coordinator.NoteLandingAsync("unreadable", "main-2", null, "main-1");

        for (var index = 0; index < 3; index++) await fixture.Service();

        var state = (await fixture.Store.ReadAsync())!;
        Assert.Equal(2, state.RangeLandings.Count);
        Assert.Null(state.Request);
        Assert.Null(state.CoveredCommit);
        await AssertNoRunEvents(fixture);
        AssertNoRunLines(fixture.Lines);
        Assert.Contains(fixture.Lines, line => line.Contains("result=landing-record-unreadable "));
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task IdleLoopWithSpareAcceptanceWidthServicesMissesWithoutRunningGates()
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        await fixture.Land();
        await fixture.Coordinator.NoteLandingAsync("unreadable", "main-2", null, "main-1");
        var verifier = new BackgroundGateStartHarness.ScriptedAcceptanceVerifier(
            new AcceptanceVerificationResult(true, false, 0, null));
        var servicePasses = 0;
        var idlePasses = 0;
        var loop = new ConductorBatchLoop(writeJitter: () => 0, janitorialPhaseProbe: phase =>
        {
            if (phase == "selected-execution-backstop-idle") servicePasses++;
        }) { SelectedExecutionBackstop = fixture.CreateCoordinator(line => ConductorBatchLoop.EmitProgress(line)) };

        var captured = AsyncLocalConsoleRouter.Capture(() => loop.Run(
            new AgentOrchestratorKernel(), MakeDriver(cohortAcceptanceVerifier: verifier),
            ConductorAutonomyPolicy.Conservative with { AcceptanceWidth = 2 },
            NoStopPath(), maxIterations: 3, keepAliveWhenIdle: true,
            watchInterval: TimeSpan.FromMilliseconds(1), sleepFunc: _ => ++idlePasses == 3));

        Assert.Equal(3, servicePasses);
        Assert.Equal(0, verifier.RunCount);
        Assert.Null((await fixture.Store.ReadAsync())!.Request);
        await AssertNoRunEvents(fixture);
        AssertNoRunLines(fixture.Lines);
        AssertNoRunLines(captured.Split('\n'));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public async Task LegacyRequestIsClearedOnceWhileOpenMissAndMarkerSurvive(int attempts, bool retryPending)
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        var miss = new SelectedExecutionBackstopMissRecord("legacy-miss", "main-0", "main-1",
            ["landed"], ["Candidate.X.First"], ["lane"], null);
        var request = new SelectedExecutionBackstopRequest("legacy-request", "main-0", "main-1", 1,
            ["landed"], fixture.Now, Attempts: attempts, RetryPending: retryPending);
        await fixture.Store.WriteAsync("started", new("main-0", Request: request, OpenMiss: miss), fixture.Now);
        var item = await fixture.Items.RaiseAsync(CollaborationItemType.Verify, null, "open miss",
            JsonSerializer.Serialize(miss), miss.CorrelationKey);
        AcceptanceSelectedExecutionSessionSuspension.Write(fixture.Home, new(miss.Id));
        var restarted = fixture.CreateCoordinator();

        await restarted.ServiceAsync();

        var state = (await fixture.Store.ReadAsync())!;
        Assert.Null(state.Request);
        Assert.Equal(JsonSerializer.Serialize(miss), JsonSerializer.Serialize(state.OpenMiss));
        Assert.Equal(item.Id, Assert.Single(await fixture.Items.ListAsync()).Id);
        Assert.Equal(miss.Id, (await AcceptanceSelectedExecutionSessionSuspension.ReadAsync("unused", fixture.Home))!.MissId);
        var events = await fixture.Events.ReadByTypeSinceAsync(SelectedExecutionBackstopStore.EventType, afterSequence: 0, maxCount: 100);
        Assert.Equal("legacy-request-cleared", events.Last().Operation);
        await restarted.ServiceAsync();
        Assert.Single(fixture.Lines.Where(line => line.Contains("result=legacy-request-cleared ")));

        Assert.True(await fixture.Items.TryResolveAsync(miss.CorrelationKey, "operator verified fix"));
        await restarted.ServiceAsync();

        Assert.Null((await fixture.Store.ReadAsync())!.OpenMiss);
        Assert.False(File.Exists(AcceptanceSelectedExecutionSessionSuspension.MarkerPath(fixture.Home)));
        Assert.Single(fixture.Lines.Where(line => line.Contains("result=miss-resolved ")));
    }

    [Fact]
    public async Task RetainsNewest200LandingsAndInstantsAndStillDetectsAMiss()
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        var epoch = fixture.Now;
        for (var ordinal = 1; ordinal <= 205; ordinal++)
        {
            fixture.Now = epoch.AddMinutes(ordinal);
            await fixture.Land("goal-" + ordinal, "main-" + ordinal,
                ordinal == 205 ? SelectedExecutionBackstopFixture.Applied : AcceptanceSelectedExecutionRecord.Disabled);
        }

        var state = (await fixture.Store.ReadAsync())!;
        Assert.Equal(205, state.LastLandingOrdinal);
        Assert.Equal(200, state.RangeLandings.Count);
        Assert.Equal(Enumerable.Range(6, 200).Select(value => (long)value), state.RangeLandings.Select(landing => landing.Ordinal));
        Assert.Equal(state.RangeLandings.Select(landing => landing.Ordinal), state.RecordedLandingInstants.Select(instant => instant.Ordinal));
        Assert.All(state.RecordedLandingInstants, instant => Assert.Equal(epoch.AddMinutes(instant.Ordinal), instant.RecordedAt));

        var detection = await fixture.DetectMiss();
        var miss = Assert.Single(detection.Misses);
        Assert.Equal(new[] { "goal-205" }, miss.GoalIds);
        Assert.Equal("main-205", miss.CoveredToCommit);
        Assert.Equal(miss.CorrelationKey, Assert.Single(await fixture.Items.ListAsync()).CorrelationKey);
        Assert.Equal(miss.Id, (await fixture.Store.ReadAsync())!.OpenMiss!.Id);
        Assert.Equal(miss.Id, (await AcceptanceSelectedExecutionSessionSuspension.ReadAsync("unused", fixture.Home))!.MissId);
        Assert.Single(fixture.Lines.Where(line => line.Contains("result=miss-raised ")));
    }

    [Fact]
    public async Task GateMissReportsFullStoredItemIdOnceAcrossTicks()
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        await fixture.Land();
        var detection = await fixture.DetectMiss();
        Assert.Single(detection.Misses);

        var item = Assert.Single(await fixture.Items.ListAsync());
        Assert.Null(item.GoalId);
        var raised = Assert.Single(fixture.Lines.Where(line => line.Contains("result=miss-raised ", StringComparison.Ordinal)));
        Assert.EndsWith(" detail=item=" + item.Id, raised, StringComparison.Ordinal);

        await fixture.Service();
        await fixture.DetectMiss();
        await fixture.Service();

        Assert.Equal(item.Id, Assert.Single(await fixture.Items.ListAsync()).Id);
        Assert.Equal(raised, Assert.Single(fixture.Lines.Where(line => line.Contains("result=miss-raised ", StringComparison.Ordinal))));
    }

    private static async Task AssertNoRunEvents(SelectedExecutionBackstopFixture fixture)
    {
        var events = await fixture.Events.ReadByTypeSinceAsync(SelectedExecutionBackstopStore.EventType, afterSequence: 0, maxCount: 100);
        Assert.Equal(2, events.Count);
        Assert.All(events, entry =>
        {
            Assert.DoesNotContain(entry.Operation, new[] { "queued", "started", "coalesced", "resumed" });
            Assert.Null(JsonSerializer.Deserialize<SelectedExecutionBackstopState>(entry.PayloadJson!)!.Request);
        });
    }

    private static void AssertNoRunLines(IEnumerable<string> lines) =>
        Assert.DoesNotContain(lines, line => line.Contains("SELECTED_EXECUTION_BACKSTOP result=started ") ||
            line.Contains("SELECTED_EXECUTION_BACKSTOP result=queued ") ||
            line.Contains("SELECTED_EXECUTION_BACKSTOP result=resumed "));
}
