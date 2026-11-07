using Mcg.AgentOrchestrator.Infrastructure;

public sealed class RemoteLaneOfferPlannerTests
{
    [Fact]
    public void Plan_GateLaneShapes_AdmitsBestRatioFirstWithinRecomputedFinishAndSlotTotal()
    {
        RemoteLaneOfferInputs[] lanes =
        [
            Lane("Balanced 1", 584, 653, 807),
            Lane("Process spawning", 859, 836, 1015),
            Lane("Dotnet build slots", 782, 574, 700),
            .. new[] { 300d, 350, 400, 450, 500, 600 }.Select(seconds =>
                Lane($"Balanced {seconds}", seconds, seconds * 11 / 10, seconds * 13 / 10))
        ];

        var decisions = RemoteLaneOfferPlanner.Plan(lanes, 5, [2, 2], _ => true);
        var admitted = decisions.Where(decision => decision.Offer).ToArray();

        Assert.Equal(new[] { "Dotnet build slots", "Process spawning", "Balanced 450", "Balanced 400" },
            admitted.Select(decision => decision.Inputs.LaneName));
        Assert.Equal(new[] { "Dotnet build slots", "Process spawning", "Balanced 600", "Balanced 500",
            "Balanced 450", "Balanced 400", "Balanced 350", "Balanced 300", "Balanced 1" },
            decisions.Select(decision => decision.Inputs.LaneName));
        Assert.Equal(new[] { 574d, 836, 585, 520 }, admitted.Select(decision => decision.RemoteSeconds));
        Assert.Equal(new[] { 965d, 859, 636.8, 600 }, admitted.Select(decision => decision.Inputs.ExpectedLocalFinishSeconds));
        Assert.True(admitted.Length <= 4);
        var remaining = lanes.ToList();
        foreach (var decision in decisions)
        {
            var expectedFinish = Math.Max(remaining.Max(lane => lane.ManifestEstimateSeconds),
                remaining.Sum(lane => lane.ManifestEstimateSeconds) / 5);
            Assert.Equal(expectedFinish, decision.Inputs.ExpectedLocalFinishSeconds);
            if (!decision.Offer) continue;
            Assert.True(decision.RemoteSeconds <= expectedFinish, decision.Inputs.LaneName);
            Assert.Equal("fits", decision.Reason);
            remaining.RemoveAll(lane => lane.LaneName == decision.Inputs.LaneName);
        }
        Assert.All(decisions.Where(decision => decision.Inputs.LaneName is "Balanced 300" or "Balanced 350"),
            decision => { Assert.False(decision.Offer); Assert.Equal("capacity", decision.Reason); });
        Assert.Equal(807, decisions.Single(decision => decision.Inputs.LaneName == "Balanced 1").RemoteSeconds);
    }

    [Fact]
    public void Plan_LaneFitsAllLocalDeadlineButNotRecomputedFinish_KeepsLocalRemoteSlowerAfterOffload()
    {
        var now = DateTimeOffset.Parse("2026-11-07T12:00:00Z");
        var slower = Lane("B", 380, 390);
        var decisions = RemoteLaneOfferPlanner.Plan([Lane("A", 400, 100), slower,
            Lane("C", 200, 100) with { ConsecutiveRemoteFailures = 3, NewestRemoteFailureAt = now, DecisionTime = now }],
            2, [1, 1], _ => true);

        var baseline = RemoteLaneOfferPolicy.Decide(slower with { ExpectedLocalFinishSeconds = 490 });
        Assert.True(baseline.Offer);
        Assert.Equal("fits", baseline.Reason);
        Assert.True(decisions[0].Offer);
        Assert.Equal("A", decisions[0].Inputs.LaneName);
        var decision = decisions.Single(item => item.Inputs.LaneName == "B");
        Assert.False(decision.Offer);
        Assert.Equal("remote-slower-after-offload", decision.Reason);
        Assert.Equal(380, decision.Inputs.ExpectedLocalFinishSeconds);
        Assert.Equal(390, decision.RemoteSeconds);
    }

    [Theory]
    [InlineData("short", 45, 30d, 3, 0, 0, false)]
    [InlineData("remote-failing", 120, 100d, 3, 3, 1, false)]
    [InlineData("fits-probe", 150, null, 0, 0, 0, true)]
    [InlineData("fits-reprobe", 150, null, 0, 3, 7, true)]
    public void Plan_SingleCandidateLane_KeepsPerLanePolicyOutcome(string reason, double local,
        double? remote, int samples, int failures, int ageHours, bool offer)
    {
        var now = DateTimeOffset.Parse("2026-11-07T12:00:00Z");
        var lane = new RemoteLaneOfferInputs("candidate", local, 3, local, remote, samples, 0,
            failures, failures > 0 ? now.AddHours(-ageHours) : null, now);
        // Exploration needs a local peer to provide the existing probe/reprobe band.
        RemoteLaneOfferInputs[] lanes = offer ? [lane, Lane("local peer", 300, 100)] : [lane];
        var finish = offer ? 300 : local;
        var baseline = RemoteLaneOfferPolicy.Decide(lane with { ExpectedLocalFinishSeconds = finish });
        var decision = RemoteLaneOfferPlanner.Plan(lanes, 5, [2], name => name == "candidate")
            .Single(item => item.Inputs.LaneName == "candidate");

        Assert.Equal(offer, decision.Offer);
        Assert.Equal(reason, decision.Reason);
        Assert.Equal(baseline, decision);
    }

    [Fact]
    public void Plan_IneligibleLaneStaysLocalWithoutConsumingCapacity()
    {
        var decisions = RemoteLaneOfferPlanner.Plan([Lane("excluded", 600, 50), Lane("candidate", 400, 300)],
            5, [1], name => name == "candidate");
        var admitted = Assert.Single(decisions, decision => decision.Offer);
        Assert.Equal("candidate", admitted.Inputs.LaneName);
        Assert.Equal(600, admitted.Inputs.ExpectedLocalFinishSeconds);
        Assert.False(decisions.Single(decision => decision.Inputs.LaneName == "excluded").Offer);
    }

    [Fact]
    public void Plan_NoSlotsKeepsFittingLaneLocal()
    {
        var decision = Assert.Single(RemoteLaneOfferPlanner.Plan([Lane("lane", 400, 100)], 5, [], _ => true));
        Assert.False(decision.Offer);
        Assert.Equal("capacity", decision.Reason);
    }

    [Fact]
    public void Decide_SelectsSlotMedianAndFallsBackToExistingEstimate()
    {
        var lane = Lane("lane", 400, 100, 200) with { RemoteMedianSeconds = 150 };
        Assert.Equal(100, RemoteLaneOfferPolicy.Decide(lane).RemoteSeconds);
        Assert.Equal(200, RemoteLaneOfferPolicy.Decide(lane, sharedExecutor: true).RemoteSeconds);
        Assert.Equal(150, RemoteLaneOfferPolicy.Decide(lane with { SoloMedianSeconds = null }).RemoteSeconds);
        Assert.Equal(150, RemoteLaneOfferPolicy.Decide(lane with { ConcurrentMedianSeconds = null }, true).RemoteSeconds);
        Assert.Equal(815, RemoteLaneOfferPolicy.Decide(lane with { RemoteSamples = 2 }, true).RemoteSeconds);
    }

    [Fact]
    public void Plan_CachesWholeSetButReportsEachAskedLaneOnlyOnce()
    {
        var lanes = new[] { Lane("A", 400, 100), Lane("B", 380, 390), Lane("C", 200, 1000) };
        var plan = new RemoteLaneOfferPlan(lanes.ToDictionary(lane => lane.LaneName), [1, 1], _ => true);
        var reports = new List<string>();
        var timeout = TimeSpan.FromMinutes(5);
        var rejected = plan.Decide("B", 2, timeout, reports.Add);
        var offered = plan.Decide("A", 2, timeout, reports.Add);
        Assert.Same(rejected, plan.Decide("B", 1, timeout, reports.Add));
        Assert.False(rejected.Offer);
        Assert.True(offered.Offer);
        Assert.Equal(2, reports.Count);
        Assert.Contains("reason=remote-slower-after-offload", reports[0]);
        Assert.Contains(" F=380 ", reports[0]);
        Assert.Contains(" F=490 ", reports[1]);
    }

    private static RemoteLaneOfferInputs Lane(string name, double local, double solo, double? concurrent = null) =>
        new(name, local, 3, local, solo, 3, 0, SoloMedianSeconds: solo, ConcurrentMedianSeconds: concurrent);
}
