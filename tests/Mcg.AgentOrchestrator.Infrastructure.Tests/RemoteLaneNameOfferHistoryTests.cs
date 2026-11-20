using Mcg.AgentOrchestrator.Infrastructure;

// Each test owns its ledger and uses the fixed intervals from RemoteLaneOfferSeeding.
public sealed class RemoteLaneNameOfferHistoryTests : IDisposable
{
    private const string Lane = "infrastructure tests: Balanced 5";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "offer-lane-name-" + Guid.NewGuid().ToString("N"));
    private string Journal => Path.Combine(_root, "history.jsonl");

    [Fact]
    public void OfferHistoryPoolsRunsOutsideTheAdmissionTail()
    {
        Assert.True(RemoteLaneOfferHistoryReader.LineLimit >= 5_000);
        Assert.Equal(RemoteLaneOfferHistoryReader.LineLimit, GoalAcceptanceVerifier.RemoteLaneOfferHistoryLineLimit);
        Assert.Equal(500, RemoteLaneAdmissionHistory.LineLimit);

        SeedThreePriorFilters();
        File.AppendAllLines(Journal, Enumerable.Repeat("malformed", 499));

        var offer = DecideWithCurrentFilter();
        Assert.Equal(3, offer.Inputs.RemoteSamples);
        Assert.Equal(100, offer.RemoteSeconds);
        var admission = Assert.Single(RemoteLaneAdmissionHistory.Read(Journal).Records);
        Assert.Equal(GoalAcceptanceVerifier.ShortHash("previous-c"), admission.Expected.Filter);
    }

    [Fact]
    public void OfferHistoryDropsPooledSamplesPastItsTail()
    {
        RemoteLaneOfferSeeding.SeedRemote(Journal, Lane, "expired-filter", 999, samples: 1);
        SeedThreePriorFilters();
        File.AppendAllLines(Journal, Enumerable.Repeat("malformed", RemoteLaneOfferHistoryReader.LineLimit - 3));

        var offer = DecideWithCurrentFilter();
        Assert.Equal(3, offer.Inputs.RemoteSamples);
        Assert.Equal(100, offer.RemoteSeconds);

        File.AppendAllLines(Journal, ["malformed"]);
        offer = DecideWithCurrentFilter();
        Assert.Equal(0, offer.Inputs.RemoteSamples);
        Assert.Equal(255, offer.RemoteSeconds);
    }

    private void SeedThreePriorFilters()
    {
        RemoteLaneOfferSeeding.SeedRemote(Journal, Lane, "previous-a", 80, samples: 1);
        RemoteLaneOfferSeeding.SeedRemote(Journal, Lane, "previous-b", 100, samples: 1);
        RemoteLaneOfferSeeding.SeedRemote(Journal, Lane, "previous-c", 300, samples: 1);
    }

    private RemoteLaneOfferDecision DecideWithCurrentFilter()
    {
        var (exact, laneNames) = RemoteLaneOfferHistoryReader.ReadWithLaneNames(Journal);
        var selected = laneNames.Select(exact, (Lane, GoalAcceptanceVerifier.ShortHash("current-filter")));
        return RemoteLaneOfferPolicy.Decide(new(Lane, 120, 3, 120, selected?.MedianSeconds, selected?.Samples ?? 0, 300,
            SoloMedianSeconds: selected?.SoloMedianSeconds, ConcurrentMedianSeconds: selected?.ConcurrentMedianSeconds));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
