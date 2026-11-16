using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: fixture-local stores and marker paths; no external gate or process.
public sealed class SelectedExecutionGateMissNegativeControlTests
{
    [Theory]
    [InlineData("selected-class")]
    [InlineData("candidate-own")]
    [InlineData("baseline-identities-unavailable")]
    public async Task NonMissGateFailureRaisesNoItemAndNoSuspension(string scenario)
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        await fixture.Land();
        string[] candidate = scenario switch
        {
            "selected-class" => ["Candidate.Floor.Test"],
            "candidate-own" => ["Candidate.X.First", "Other.Y.Z"],
            _ => ["Candidate.X.First"]
        };
        string[] baseline = scenario switch
        {
            "selected-class" => ["Candidate.Floor.Test"],
            "candidate-own" => ["Other.Y.Z"],
            _ => []
        };
        var verdict = scenario switch
        {
            "selected-class" => CandidateRedBaselineVerdict.Inherited,
            "candidate-own" => CandidateRedBaselineVerdict.CandidateOwn,
            _ => CandidateRedBaselineVerdict.BaselineIdentitiesUnavailable
        };
        var before = (await fixture.Events.ReadByTypeSinceAsync(SelectedExecutionBackstopStore.EventType)).Count;

        var result = await new SelectedExecutionGateMissDetector(fixture.Coordinator).ObserveAsync("gate",
            Arm(FindingEvidenceArm.Candidate, candidate), Arm(FindingEvidenceArm.Baseline, baseline));

        Assert.Equal(verdict, result.Verdict);
        Assert.Empty(result.Misses);
        Assert.Empty(await fixture.Items.ListAsync());
        Assert.Null((await fixture.Store.ReadAsync())!.OpenMiss);
        Assert.False(File.Exists(AcceptanceSelectedExecutionSessionSuspension.MarkerPath(fixture.Home)));
        Assert.Equal(before, (await fixture.Events.ReadByTypeSinceAsync(SelectedExecutionBackstopStore.EventType)).Count);
    }

    [Fact]
    public async Task UnreadableLandingRecordIsUnclassifiableAndNeverAMiss()
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        await fixture.Coordinator.NoteLandingAsync("goal-u", "c-u", null, "main-0");

        var result = await new SelectedExecutionGateMissDetector(fixture.Coordinator).ObserveAsync("gate",
            Arm(FindingEvidenceArm.Candidate, ["Candidate.X.First"]), Arm(FindingEvidenceArm.Baseline, ["Candidate.X.First"]));

        Assert.Equal(CandidateRedBaselineVerdict.Inherited, result.Verdict);
        var unreadable = Assert.Single(result.UnclassifiableLandings);
        Assert.Equal("goal-u", unreadable.GoalId);
        Assert.Equal("c-u", unreadable.Commit);
        Assert.Empty(result.Misses);
        Assert.Empty(await fixture.Items.ListAsync());
        Assert.Null((await fixture.Store.ReadAsync())!.OpenMiss);
        Assert.False(File.Exists(AcceptanceSelectedExecutionSessionSuspension.MarkerPath(fixture.Home)));
        var line = Assert.Single(fixture.Lines.Where(line => line.Contains("result=gate-no-miss ", StringComparison.Ordinal)));
        Assert.Contains("unclassifiable=goal-u@c-u", line);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NonRedArmCannotEstablishMiss(bool candidateIsGreen)
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        await fixture.Land();
        var candidate = Arm(FindingEvidenceArm.Candidate, ["Candidate.X.First"]);
        var baseline = Arm(FindingEvidenceArm.Baseline, ["Candidate.X.First"]);
        if (candidateIsGreen) candidate = candidate with { Disposition = FindingEvidenceArmDisposition.Green };
        else baseline = baseline with { Disposition = FindingEvidenceArmDisposition.Inconclusive };

        var result = await new SelectedExecutionGateMissDetector(fixture.Coordinator).ObserveAsync("gate", candidate, baseline);

        Assert.Empty(result.Misses);
        Assert.Null(result.Verdict);
        Assert.Empty(await fixture.Items.ListAsync());
        Assert.Null((await fixture.Store.ReadAsync())!.OpenMiss);
        Assert.False(File.Exists(AcceptanceSelectedExecutionSessionSuspension.MarkerPath(fixture.Home)));
    }

    [Fact]
    public async Task MissingRetainedRangeCannotEstablishMiss()
    {
        using var fixture = new SelectedExecutionBackstopFixture();

        var result = await new SelectedExecutionGateMissDetector(fixture.Coordinator).ObserveAsync("gate",
            Arm(FindingEvidenceArm.Candidate, ["Candidate.X.First"]), Arm(FindingEvidenceArm.Baseline, ["Candidate.X.First"]));

        Assert.Empty(result.Misses);
        Assert.Empty(await fixture.Items.ListAsync());
        Assert.Null(await fixture.Store.ReadAsync());
        Assert.False(File.Exists(AcceptanceSelectedExecutionSessionSuspension.MarkerPath(fixture.Home)));
    }

    private static FindingEvidenceArmReceipt Arm(FindingEvidenceArm arm, string[] failures) =>
        new(arm, arm == FindingEvidenceArm.Candidate ? "candidate-sha" : "baseline-sha", FindingEvidenceArmDisposition.Red,
            Accepted: true, Passed: false, Summary: "synthetic evidence", FailingTestIdentities: failures, TestResultPaths: []);
}
