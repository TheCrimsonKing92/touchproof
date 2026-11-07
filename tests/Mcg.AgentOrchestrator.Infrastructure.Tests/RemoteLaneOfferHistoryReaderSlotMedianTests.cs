using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel safe: each instance owns a private journal and uses fixed intervals.
public sealed class RemoteLaneOfferHistoryReaderSlotMedianTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "offer-slot-medians-" + Guid.NewGuid().ToString("N"));
    private string Journal => Path.Combine(_root, "history.jsonl");

    [Fact]
    public void SoloAndConcurrentMediansRequireMinimumSamples()
    {
        Append(0, 100);
        Append(1200, 120);
        // Neither another executor nor a non-attempt outcome makes a solo sample concurrent.
        Append(0, 200, executor: "other", lane: "peer");
        Append(0, 200, outcome: "executor-busy");
        foreach (var (start, seconds) in new[] { (2400, 300), (3600, 320), (4800, 340) })
        {
            Append(start, seconds);
            Append(start + 1, 900, outcome: "remote-red", lane: "peer");
        }

        var measured = RemoteLaneOfferHistoryReader.Read(Journal)[("lane", "filter")];
        Assert.Equal(5, measured.Samples);
        Assert.Equal(300, measured.MedianSeconds);
        Assert.Equal(100, measured.LastSeconds);
        Assert.Null(measured.SoloMedianSeconds);
        Assert.Equal(320, measured.ConcurrentMedianSeconds);

        Append(6000, 140);
        measured = RemoteLaneOfferHistoryReader.Read(Journal)[("lane", "filter")];
        Assert.Equal(6, measured.Samples);
        Assert.Equal(220, measured.MedianSeconds);
        Assert.Equal(120, measured.SoloMedianSeconds);
        Assert.Equal(320, measured.ConcurrentMedianSeconds);
    }

    [Fact]
    public void ConcurrentSubsetBelowMinimumFallsBackToOverallMedian()
    {
        Append(0, 100);
        foreach (var start in new[] { 1200, 2400 })
        {
            Append(start, 300);
            Append(start + 1, 500, outcome: "remote-red");
        }
        var measured = RemoteLaneOfferHistoryReader.Read(Journal)[("lane", "filter")];
        Assert.Equal(3, measured.Samples);
        Assert.Null(measured.SoloMedianSeconds);
        Assert.Null(measured.ConcurrentMedianSeconds);
        var lane = new RemoteLaneOfferInputs("lane", 400, 3, 400, measured.MedianSeconds, measured.Samples, 400,
            SoloMedianSeconds: measured.SoloMedianSeconds, ConcurrentMedianSeconds: measured.ConcurrentMedianSeconds);
        Assert.Equal(300, RemoteLaneOfferPolicy.Decide(lane, sharedExecutor: true).RemoteSeconds);
    }

    [Fact]
    public void MissingIntervalsKeepOverallSamplesWithoutInventingSoloMeasurements()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllLines(Journal, new[] { 100, 200, 300 }.Select(seconds => JsonSerializer.Serialize(new
        {
            outcome = "accepted", lane = "lane", expected = new { filter = "filter" },
            attempt = new { last_status = new { seconds } }
        })));
        var measured = RemoteLaneOfferHistoryReader.Read(Journal)[("lane", "filter")];
        Assert.Equal(3, measured.Samples);
        Assert.Equal(200, measured.MedianSeconds);
        Assert.Null(measured.SoloMedianSeconds);
        Assert.Null(measured.ConcurrentMedianSeconds);
    }

    [Fact]
    public void TouchingIntervalsDoNotCountAsConcurrent()
    {
        Append(0, 100);
        Append(100, 100);
        Append(200, 100);
        var measured = RemoteLaneOfferHistoryReader.Read(Journal)[("lane", "filter")];
        Assert.Equal(100, measured.SoloMedianSeconds);
        Assert.Null(measured.ConcurrentMedianSeconds);
    }

    [Theory]
    [InlineData("accepted")]
    [InlineData("remote-red")]
    public void PeerWithoutDurationStillMakesSamplesConcurrent(string outcome)
    {
        foreach (var (offset, seconds) in new[] { (0, 100), (1200, 120), (2400, 140) })
        {
            Append(offset, seconds);
            var start = DateTimeOffset.Parse("2026-11-07T00:00:00Z").AddSeconds(offset + 1);
            File.AppendAllLines(Journal, [JsonSerializer.Serialize(new
            {
                executor_id = "one", observed_at = start.AddSeconds(30), outcome,
                attempt = new { steps = new[] { new { started_at = start } } }
            })]);
        }
        var measured = RemoteLaneOfferHistoryReader.Read(Journal)[("lane", "filter")];
        Assert.Equal(3, measured.Samples);
        Assert.Equal(120, measured.ConcurrentMedianSeconds);
        Assert.Null(measured.SoloMedianSeconds);
    }

    private void Append(int offset, int seconds, string executor = "one", string outcome = "accepted", string lane = "lane")
    {
        Directory.CreateDirectory(_root);
        var start = DateTimeOffset.Parse("2026-11-07T00:00:00Z").AddSeconds(offset);
        File.AppendAllLines(Journal, [JsonSerializer.Serialize(new
        {
            executor_id = executor, observed_at = start.AddSeconds(seconds), outcome, lane,
            expected = new { filter = "filter" },
            attempt = new { steps = new[] { new { started_at = start } }, last_status = new { seconds } }
        })]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
