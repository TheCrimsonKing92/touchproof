using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;
using Fixture = ExecutorScreeningRunnerTests.ExecutorScreeningFixture;

public sealed class CliExecutorScreenWaitTests
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task WholeGateWaitsTwoPollsWithoutEverTakingSlotZeroWhileSlotOneIsBusy()
    {
        using var fixture = new Fixture();
        using var output = new StringWriter();
        var occupancy = new SlotOccupancy { BusySlot = 1 };
        var clock = new MonotonicClock(fixture.Clock.GetUtcNow());
        var delays = new List<TimeSpan>();
        int[] heldOnSubmit = [];
        int[] markedOnSubmit = [];
        fixture.Executor.Submit = (request, _) =>
        {
            heldOnSubmit = occupancy.HeldSlots.Order().ToArray();
            markedOnSubmit = occupancy.Markers.Keys.Order().ToArray();
            return Task.FromResult(fixture.PassingSubmission(request));
        };

        var exit = await Run(fixture, occupancy, clock, output, ["--whole-gate", "--executor", "laptop", "--max-lanes", "1", "--wait", "0.1"],
            (interval, _) =>
            {
                delays.Add(interval);
                Assert.InRange(delays.Count, 1, 2);
                Assert.Empty(occupancy.HeldSlots);
                Assert.Empty(fixture.Executor.Requests);
                clock.Advance(interval);
                if (delays.Count == 2) occupancy.BusySlot = null;
                return Task.CompletedTask;
            });

        Assert.Equal(0, exit);
        Assert.Equal(new[] { PollInterval, PollInterval }, delays);
        Assert.Equal(new[] { (1, (int?)1, false), (1, (int?)1, false), (1, (int?)null, true), (0, (int?)null, true) },
            occupancy.ClaimAttempts);
        Assert.Equal(new[] { 0, 1 }, heldOnSubmit);
        Assert.Equal(new[] { 0, 1 }, markedOnSubmit);
        Assert.Single(fixture.Executor.Requests);
        Assert.Equal(ExecutorWholeGateTrialStatus.Complete,
            Assert.Single(ExecutorScreeningReceiptLedger.ReadTrials(fixture.ReceiptPath)).Status);
        Assert.StartsWith("TRIAL ", output.ToString());
        Assert.Empty(occupancy.HeldSlots);
        Assert.Empty(occupancy.Markers);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task ExpiryReportsLastBusySlotAndReleasesEveryClaim(int busySlot)
    {
        using var fixture = new Fixture();
        using var output = new StringWriter();
        var occupancy = new SlotOccupancy { BusySlot = busySlot, RevealBusyMarkerOnFailedClaim = true };
        var clock = new MonotonicClock(fixture.Clock.GetUtcNow());
        var delays = new List<TimeSpan>();

        var exit = await Run(fixture, occupancy, clock, output, ["--whole-gate", "--executor", "laptop", "--wait", "0.025"],
            (interval, _) =>
            {
                delays.Add(interval);
                Assert.InRange(delays.Count, 1, 2);
                Assert.Empty(occupancy.HeldSlots);
                Assert.Empty(occupancy.Markers);
                clock.Advance(interval);
                return Task.CompletedTask;
            });

        Assert.Equal(0, exit);
        Assert.Equal(new[] { PollInterval, TimeSpan.FromMilliseconds(500) }, delays);
        Assert.Equal(2, occupancy.ClaimAttempts.Count(attempt => attempt.Slot == busySlot && !attempt.Acquired));
        Assert.Equal(busySlot == 0 ? 2 : 0, occupancy.ClaimAttempts.Count(attempt => attempt.Acquired));
        Assert.Equal($"SCREEN idle reason=slot-busy slot={busySlot} attempt=external class=FocusedEvidence waited=0.025" +
            Environment.NewLine, output.ToString());
        Assert.Empty(occupancy.HeldSlots);
        Assert.Empty(occupancy.Markers);
        Assert.Empty(fixture.Executor.Requests);
        Assert.False(File.Exists(fixture.ReceiptPath));
    }

    [Fact]
    public async Task OrdinaryWaitCanStartWithAllExecutorsOccupiedAndStopOnCancellation()
    {
        using var fixture = new Fixture();
        using var output = new StringWriter();
        using var stop = new CancellationTokenSource();
        var occupancy = new SlotOccupancy { BusySlot = 1, BusyMarkerVisible = true };
        var clock = new MonotonicClock(fixture.Clock.GetUtcNow());
        var delays = 0;

        var exit = await Run(fixture, occupancy, clock, output, ["--wait", "1"], (_, token) =>
        {
            Assert.Equal(stop.Token, token);
            delays++;
            stop.Cancel();
            return Task.FromCanceled(token);
        }, stop.Token);

        Assert.Equal(0, exit);
        Assert.Equal(1, delays);
        Assert.Empty(occupancy.ClaimAttempts);
        Assert.Empty(occupancy.HeldSlots);
        Assert.Empty(fixture.Executor.Requests);
        Assert.Equal("", output.ToString());
        Assert.False(File.Exists(fixture.ReceiptPath));
    }

    private static Task<int> Run(Fixture fixture, SlotOccupancy occupancy, TimeProvider clock, TextWriter output,
        string[] args, Func<TimeSpan, CancellationToken, Task> delay, CancellationToken token = default) =>
        CliExecutorScreenCommand.RunAsync(["executor-screen", ..args],
            CliExecutorScreenCommandTests.WriteConfiguration(fixture, "shadow"), () => [fixture.Candidate],
            _ => (new[] { new AcceptanceTestLane("A", "A") }, TimeSpan.FromMinutes(1)),
            (_, _, _) => fixture.Executor, clock, PollInterval, fixture.Root, fixture.ReceiptPath, output, TextWriter.Null,
            trialPlanSource: _ => new([new("A", ExecutorScreeningPlanner.Project, "A", [], 1, TimeSpan.FromMinutes(1))],
                [], AcceptanceDeclaredTestSettings.Empty),
            token: token, passedGateSource: _ => [new(fixture.Candidate.GoalId, "passed", fixture.Candidate.Commit,
                fixture.Candidate.MainSha, fixture.Clock.GetUtcNow())],
            trialCheckout: _ => (fixture.Candidate, null), occupancy: occupancy, delay: delay);

    private sealed class MonotonicClock(DateTimeOffset fixedUtc) : TimeProvider
    {
        private long _ticks;
        public override DateTimeOffset GetUtcNow() => fixedUtc;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        internal void Advance(TimeSpan interval) => _ticks += interval.Ticks;
    }

    internal sealed class SlotOccupancy : IExecutorSlotOccupancy
    {
        private bool _includeMarkerOnNextRead;
        internal int? BusySlot { get; set; }
        internal bool BusyMarkerVisible { get; init; }
        internal bool RevealBusyMarkerOnFailedClaim { get; init; }
        internal List<(int Slot, int? BusySlot, bool Acquired)> ClaimAttempts { get; } = [];
        internal HashSet<int> HeldSlots { get; } = [];
        internal Dictionary<int, (string Attempt, string? Class)> Markers { get; } = [];

        public IDisposable? TryClaimExclusive(string root, string executor, int slot)
        {
            var acquired = slot != BusySlot && HeldSlots.Add(slot);
            ClaimAttempts.Add((slot, BusySlot, acquired));
            if (slot == BusySlot) _includeMarkerOnNextRead = RevealBusyMarkerOnFailedClaim;
            return acquired ? new ClaimHandle(HeldSlots, slot) : null;
        }

        public void Claim(string root, string executor, string attempt, int slot, RemoteExecutorClaimClass claimClass) =>
            Markers.Add(slot, (MarkerName(attempt, slot), claimClass.ToString()));

        public void Release(string root, string executor, string attempt, int slot) => Markers.Remove(slot);

        public IReadOnlyList<(string Attempt, string? Class)> LiveMarkers(string root, string executor)
        {
            var markers = Markers.Values.ToList();
            if (BusySlot is { } slot && (BusyMarkerVisible || _includeMarkerOnNextRead))
                markers.Add((MarkerName("external", slot), "FocusedEvidence"));
            _includeMarkerOnNextRead = false;
            return markers;
        }

        public bool IsOccupied(string root, string executor) => BusySlot is not null && BusyMarkerVisible;

        private static string MarkerName(string attempt, int slot) => slot == 0 ? attempt : $"{attempt}.slot-{slot}";

        private sealed class ClaimHandle(HashSet<int> slots, int slot) : IDisposable
        {
            public void Dispose() => Assert.True(slots.Remove(slot));
        }
    }
}
