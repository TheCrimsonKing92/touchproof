using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: fixture-local stores and marker paths; no external gate or process.
public sealed class SelectedExecutionGateMissDetectorTests
{
    [Fact]
    public async Task InheritedFailureInSkippedClassRaisesOneMissAgainstLandingAndSuspends()
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        await fixture.Land();
        var before = (await fixture.Store.ReadAsync())!;
        var detector = new SelectedExecutionGateMissDetector(fixture.Coordinator);

        var result = await detector.ObserveAsync("gate-goal", Arm(FindingEvidenceArm.Candidate), Arm(FindingEvidenceArm.Baseline));

        Assert.Equal(CandidateRedBaselineVerdict.Inherited, result.Verdict);
        var detected = Assert.Single(result.Misses);
        var state = (await fixture.Store.ReadAsync())!;
        var miss = Assert.IsType<SelectedExecutionBackstopMissRecord>(state.OpenMiss);
        Assert.Equal(detected.Id, miss.Id);
        Assert.Equal(new[] { "goal-1" }, miss.GoalIds);
        Assert.Equal("main-1", miss.CoveredFromCommit);
        Assert.Equal("main-1", miss.CoveredToCommit);
        Assert.Equal(new[] { "Candidate.X.First" }, miss.FailingTests);
        Assert.Empty(result.UnclassifiableLandings);
        Assert.Empty(result.UnmatchedTests);
        Assert.Equal(before.CoveredCommit, state.CoveredCommit);
        Assert.Equal(before.RangeStartCommit, state.RangeStartCommit);
        Assert.Equal(JsonSerializer.Serialize(before.RangeLandings), JsonSerializer.Serialize(state.RangeLandings));
        Assert.Equal(before.RecordedLandingInstants, state.RecordedLandingInstants);
        var item = Assert.Single(await fixture.Items.ListAsync());
        Assert.Equal(miss.CorrelationKey, item.CorrelationKey);
        Assert.Equal(miss.Id, JsonSerializer.Deserialize<SelectedExecutionBackstopMissRecord>(item.Body)!.Id);
        Assert.Single(fixture.Lines.Where(line => line.Contains("result=miss-raised ", StringComparison.Ordinal)));
        Assert.Equal(miss.Id, (await AcceptanceSelectedExecutionSessionSuspension.ReadAsync("unused", fixture.Home))!.MissId);
        Assert.True(File.Exists(AcceptanceSelectedExecutionSessionSuspension.MarkerPath(fixture.Home)));
        Assert.Empty(fixture.Calls);
    }

    [Fact]
    public async Task RepeatedGateMissReusesCorrelationKeyAndResolutionClearsSuspension()
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        await fixture.Land();
        var detector = new SelectedExecutionGateMissDetector(fixture.Coordinator);
        var first = Assert.Single((await detector.ObserveAsync("gate-1",
            Arm(FindingEvidenceArm.Candidate), Arm(FindingEvidenceArm.Baseline))).Misses);
        var second = Assert.Single((await detector.ObserveAsync("gate-2",
            Arm(FindingEvidenceArm.Candidate), Arm(FindingEvidenceArm.Baseline))).Misses);

        Assert.Equal(first.CorrelationKey, second.CorrelationKey);
        Assert.Equal("selected-execution-backstop-miss:" + first.Id, second.CorrelationKey);
        var item = Assert.Single(await fixture.Items.ListAsync());
        Assert.Equal(first.CorrelationKey, item.CorrelationKey);
        Assert.Single(fixture.Lines.Where(line => line.Contains("result=miss-raised ", StringComparison.Ordinal)));
        Assert.Equal(first.Id, (await AcceptanceSelectedExecutionSessionSuspension.ReadAsync("unused", fixture.Home))!.MissId);

        Assert.True(await fixture.Items.TryResolveAsync(first.CorrelationKey, "operator verified fix"));
        await fixture.Service(capacity: false);

        Assert.Null((await fixture.Store.ReadAsync())!.OpenMiss);
        Assert.False(File.Exists(AcceptanceSelectedExecutionSessionSuspension.MarkerPath(fixture.Home)));
        Assert.Null(await AcceptanceSelectedExecutionSessionSuspension.ReadAsync("unused", fixture.Home));
        Assert.Single(fixture.Lines.Where(line => line.Contains("result=miss-resolved ", StringComparison.Ordinal)));

        await detector.ObserveAsync("gate-3", Arm(FindingEvidenceArm.Candidate), Arm(FindingEvidenceArm.Baseline));

        Assert.Equal(item.Id, Assert.Single(await fixture.Items.ListAsync()).Id);
        Assert.Null((await fixture.Store.ReadAsync())!.OpenMiss);
        Assert.False(File.Exists(AcceptanceSelectedExecutionSessionSuspension.MarkerPath(fixture.Home)));
        Assert.Single(fixture.Lines.Where(line => line.Contains("result=miss-raised ", StringComparison.Ordinal)));
        Assert.Single(fixture.Lines.Where(line => line.Contains("result=miss-resolved ", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task EveryMatchingLandingGetsAnItemAndUnreadableLandingsRemainReported()
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        await fixture.Land();
        await fixture.Land("goal-2", "main-2", SelectedExecutionBackstopFixture.Applied with
        {
            Lanes = [new("lane", "skipped", [], ["Other.Y"], null)]
        });
        await fixture.Coordinator.NoteLandingAsync("goal-u", "main-3", null, "main-2");
        var detector = new SelectedExecutionGateMissDetector(fixture.Coordinator);
        string[] failures = ["Candidate.X.First", "Other.Y.Second", "Selected.Z.Third"];

        var result = await detector.ObserveAsync("gate", Arm(FindingEvidenceArm.Candidate, failures),
            Arm(FindingEvidenceArm.Baseline, failures));

        Assert.Equal(new[] { "goal-1", "goal-2" }, result.Misses.Select(miss => Assert.Single(miss.GoalIds)));
        Assert.Equal(new[] { "main-1", "main-2" }, result.Misses.Select(miss => miss.CoveredToCommit));
        Assert.Equal(new[] { "Candidate.X.First" }, result.Misses[0].FailingTests);
        Assert.Equal(new[] { "Other.Y.Second" }, result.Misses[1].FailingTests);
        Assert.Equal("goal-u", Assert.Single(result.UnclassifiableLandings).GoalId);
        Assert.Equal(new[] { "Selected.Z.Third" }, result.UnmatchedTests);
        var items = await fixture.Items.ListAsync();
        Assert.Equal(2, items.Count);
        Assert.All(result.Misses, miss => Assert.Single(items.Where(item => item.CorrelationKey == miss.CorrelationKey)));
        Assert.Equal(result.Misses[0].Id, (await fixture.Store.ReadAsync())!.OpenMiss!.Id);
        Assert.Equal(result.Misses[0].Id,
            (await AcceptanceSelectedExecutionSessionSuspension.ReadAsync("unused", fixture.Home))!.MissId);

        await detector.ObserveAsync("repeat", Arm(FindingEvidenceArm.Candidate, failures), Arm(FindingEvidenceArm.Baseline, failures));
        Assert.Equal(2, (await fixture.Items.ListAsync()).Count);
        Assert.Equal(2, fixture.Lines.Count(line => line.Contains("result=miss-raised ", StringComparison.Ordinal)));
    }

    private static FindingEvidenceArmReceipt Arm(FindingEvidenceArm arm, string[]? failures = null) =>
        new(arm, arm == FindingEvidenceArm.Candidate ? "candidate-sha" : "baseline-sha", FindingEvidenceArmDisposition.Red,
            Accepted: true, Passed: false, Summary: "synthetic evidence",
            FailingTestIdentities: failures ?? ["Candidate.X.First"], TestResultPaths: []);
}
