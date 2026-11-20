using Mcg.AgentOrchestrator.Infrastructure;

// Parallel safe: occupancy markers are scoped to a unique temporary root per test.
public sealed class RemoteLaneOfferPlannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "remote-offer-plan-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Plan_GateLaneShapes_OffersLargestLocalLanesFirstWithinSlotTotal()
    {
        RemoteLaneOfferInputs[] lanes =
        [
            Lane("Balanced 1", 584, 653, 807),
            Lane("Process spawning", 859, 836, 1015),
            Lane("Dotnet build slots", 782, 574, 700),
            .. new[] { 300d, 350, 400, 450, 500, 600 }.Select(seconds =>
                Lane($"Balanced {seconds}", seconds, seconds * 11 / 10, seconds * 13 / 10))
        ];

        var (decisions, projectedLocal, projectedMixed) = RemoteLaneOfferPlanner.Project(lanes, 5, [2, 2], _ => true, 0.10);
        var admitted = decisions.Where(decision => decision.Offer).ToArray();

        Assert.Equal(new[] { "Process spawning", "Dotnet build slots", "Balanced 600", "Balanced 1" },
            admitted.Select(decision => decision.Inputs.LaneName));
        Assert.Equal(new[] { "Process spawning", "Dotnet build slots", "Balanced 600", "Balanced 1",
            "Balanced 500", "Balanced 450", "Balanced 400", "Balanced 350", "Balanced 300" },
            decisions.Select(decision => decision.Inputs.LaneName));
        Assert.Equal(new[] { 836d, 700, 660, 807 }, admitted.Select(decision => decision.RemoteSeconds));
        Assert.Equal(new[] { 965d, 793.2, 636.8, 584 }, admitted.Select(decision => decision.Inputs.ExpectedLocalFinishSeconds));
        Assert.Equal(965, projectedLocal);
        Assert.Equal(836, projectedMixed);
    }

    [Theory]
    [InlineData(0, false, "remote-slower-after-offload", 600, 380)]
    [InlineData(1, true, "fits-slack", 390, 390)]
    public void Plan_UsesAvailableExecutorsToKeepOffersWithinGateGrowthLimit(int secondExecutorSlots,
        bool offer, string reason, double remoteSeconds, double expectedMixed)
    {
        var now = DateTimeOffset.Parse("2026-11-07T12:00:00Z");
        var (decisions, projectedLocal, projectedMixed) = RemoteLaneOfferPlanner.Project(
            [Lane("A", 400, 100), Lane("B", 380, 390, 600),
            Lane("C", 200, 100) with { ConsecutiveRemoteFailures = 3, NewestRemoteFailureAt = now, DecisionTime = now }],
            2, [2, secondExecutorSlots], _ => true, 0.10);

        Assert.Equal(offer ? new[] { "A", "B" } : new[] { "A" },
            decisions.Where(item => item.Offer).Select(item => item.Inputs.LaneName));
        var decision = decisions.Single(item => item.Inputs.LaneName == "B");
        Assert.Equal(reason, decision.Reason);
        Assert.Equal(380, decision.Inputs.ExpectedLocalFinishSeconds);
        Assert.Equal(remoteSeconds, decision.RemoteSeconds);
        Assert.Equal(490, projectedLocal);
        Assert.Equal(expectedMixed, projectedMixed);
    }

    [Theory]
    [InlineData(2, 2, 4)]
    [InlineData(3, 2, 5)]
    [InlineData(3, 3, 6)]
    public void Plan_RaisingIdleExecutorSlotsRaisesOffers(int firstSlots, int secondSlots, int expectedOffers)
    {
        var lanes = Enumerable.Range(1, 6).Select(index => Lane($"Lane {index}", 300, 250, 280)).ToArray();
        var freeSlots = RemoteExecutorOccupancyCensus.TakeGateFreeSlots(_root, [("one", firstSlots), ("two", secondSlots)]);

        var decisions = RemoteLaneOfferPlanner.Plan(lanes, 2, freeSlots, _ => true);

        Assert.Equal(new[] { firstSlots, secondSlots }, freeSlots);
        Assert.Equal(expectedOffers, decisions.Count(decision => decision.Offer));
        Assert.Equal(lanes.Length - expectedOffers, decisions.Count(decision => decision.Reason == "capacity"));
    }

    [Theory]
    [InlineData(0.10, 1, 320)]
    [InlineData(0.15, 2, 340)]
    [InlineData(0.50, 3, 400)]
    public void Plan_TrimmedOfferSetExpandsWithHigherGrowthThreshold(double threshold, int expectedOffers, double expectedMixed)
    {
        RemoteLaneOfferInputs[] lanes = [Lane("A", 300, 320), Lane("C", 300, 400), Lane("B", 300, 340)];

        var (decisions, projectedLocal, projectedMixed) = RemoteLaneOfferPlanner.Project(lanes, 3, [3], _ => true, threshold);

        Assert.Equal(300, projectedLocal);
        Assert.Equal(expectedMixed, projectedMixed);
        Assert.True(projectedMixed <= projectedLocal * (1 + threshold));
        Assert.Equal(expectedOffers, decisions.Count(decision => decision.Offer));
        Assert.Equal(lanes.Length - expectedOffers, decisions.Count(decision => decision.Reason == "remote-slower"));
    }

    [Theory]
    [InlineData("gate", (int)RemoteExecutorClaimClass.Gate, 0)]
    [InlineData("focused", (int)RemoteExecutorClaimClass.FocusedEvidence, 0)]
    [InlineData("screening", (int)RemoteExecutorClaimClass.Screening, 0)]
    [InlineData("screening-legacy", null, 0)]
    [InlineData("unclassified", null, 0)]
    [InlineData("shadow", (int)RemoteExecutorClaimClass.Shadow, 1)]
    public void Plan_LiveClaimClassDeterminesFreeGateSlotsAndOffers(string attempt, int? claimClass, int expectedOffers)
    {
        RemoteLaneOfferInputs[] lanes = [Lane("candidate", 300, 250)];
        var idleSlots = RemoteExecutorOccupancyCensus.TakeGateFreeSlots(_root, [("executor", 1)]);
        Assert.True(Assert.Single(RemoteLaneOfferPlanner.Plan(lanes, 1, idleSlots, _ => true)).Offer);
        RemoteExecutorOccupancy.Claim(_root, "executor", attempt, 0, (RemoteExecutorClaimClass?)claimClass);
        Assert.True(RemoteExecutorOccupancy.IsOccupied(_root, "executor"));

        var liveSlots = RemoteExecutorOccupancyCensus.TakeGateFreeSlots(_root, [("executor", 1)]);
        var decision = Assert.Single(RemoteLaneOfferPlanner.Plan(lanes, 1, liveSlots, _ => true));

        Assert.Equal(expectedOffers, Assert.Single(liveSlots));
        Assert.Equal(expectedOffers == 1, decision.Offer);
        Assert.Equal(expectedOffers == 1 ? "fits" : "capacity", decision.Reason);
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
        var second = plan.Decide("B", 2, timeout, reports.Add);
        var offered = plan.Decide("A", 1, timeout, reports.Add);
        Assert.Same(second, plan.Decide("B", 1, timeout, reports.Add));
        Assert.True(second.Offer);
        Assert.True(offered.Offer);
        Assert.Equal(2, reports.Count);
        Assert.Contains("reason=fits-slack", reports[0]);
        Assert.Contains(" F=380 ", reports[0]);
        Assert.Contains(" F=490 ", reports[1]);
        Assert.All(reports, line => Assert.EndsWith(" projectedLocalSeconds=490 projectedMixedSeconds=390", line));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static RemoteLaneOfferInputs Lane(string name, double local, double solo, double? concurrent = null) =>
        new(name, local, 3, local, solo, 3, 0, SoloMedianSeconds: solo, ConcurrentMedianSeconds: concurrent);
}
