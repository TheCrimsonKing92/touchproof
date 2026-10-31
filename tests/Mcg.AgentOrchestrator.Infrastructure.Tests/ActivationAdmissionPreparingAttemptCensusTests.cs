using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private temporary heartbeat files, controlled time, and injected owner identity.
public sealed class ActivationAdmissionPreparingAttemptCensusTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 31, 19, 7, 0, TimeSpan.Zero);

    [Xunit.Fact]
    public void Evaluate_FreshPreparingOwner_RemainsHealthyPastStallLimit()
    {
        using var fixture = new HeartbeatFixture();
        var tracker = new ActivationAdmissionHealthTracker();
        var ownerChecks = 0;
        var ticks = ActivationAdmissionHealthPolicy.OccupiedWithoutLiveGateTicks + 3;

        for (var tick = 1; tick <= ticks; tick++)
        {
            var now = Start.AddSeconds(tick * 20);
            fixture.WriteHeartbeat(now);
            var tally = ActivationAdmissionPreparingAttemptCensus.Evaluate(
                [fixture.Attempt], [], now, attempt =>
                {
                    Assert.Same(fixture.Attempt, attempt);
                    Assert.Equal(1234, attempt.OwnerProcessId);
                    Assert.Equal(Start.AddMinutes(-1), attempt.OwnerProcessStartedAt);
                    Assert.Equal("gate-owner.exe", attempt.OwnerExecutablePath);
                    ownerChecks++;
                    return SpawnTrackedProcessStatus.LiveMatch;
                });

            Assert.Equal(1, tally.Live);
            Assert.Equal(1, tally.Preparing);
            var verdict = Observe(tracker, tally, tick, now);
            Assert.Equal(ActivationAdmissionHealthState.Healthy, verdict.State);
            Assert.Null(verdict.Reason);
        }

        Assert.Equal(ticks, ownerChecks);
    }

    [Xunit.Theory]
    [Xunit.InlineData("dead")]
    [Xunit.InlineData("stale")]
    public void Evaluate_DeadOwnerOrStaleHeartbeat_TripsAdmissionStalled(string scenario)
    {
        using var fixture = new HeartbeatFixture();
        var tracker = new ActivationAdmissionHealthTracker();
        var ownerChecks = 0;
        var limit = ActivationAdmissionHealthPolicy.OccupiedWithoutLiveGateTicks;

        for (var tick = 1; tick <= limit; tick++)
        {
            var now = Start.AddSeconds(tick);
            fixture.WriteHeartbeat(scenario == "stale"
                ? now - GateLoadContextProbe.LiveGateHeartbeatFreshness - TimeSpan.FromTicks(1)
                : now);
            var tally = ActivationAdmissionPreparingAttemptCensus.Evaluate(
                [fixture.Attempt], [], now, _ =>
                {
                    ownerChecks++;
                    return scenario == "dead"
                        ? SpawnTrackedProcessStatus.DeadOrRecycled
                        : SpawnTrackedProcessStatus.LiveMatch;
                });

            Assert.Equal(0, tally.Live);
            Assert.Equal(0, tally.Preparing);
            var verdict = Observe(tracker, tally, tick, now);
            Assert.Equal(tick < limit ? ActivationAdmissionHealthState.StreakOpen :
                ActivationAdmissionHealthState.Tripped, verdict.State);
        }

        Assert.Equal(scenario == "dead" ? limit : 0, ownerChecks);
        Assert.Equal(ConductorActivationRevertReason.AdmissionStalled, tracker.Verdict.Reason);
        Assert.Equal($"occupiedNoLiveGateTicks={limit} waiting=1 occupied=1 live=0",
            tracker.Verdict.Detail);
    }

    [Xunit.Fact]
    public void Evaluate_AttemptAndMultipleCheckHeartbeats_CountsAttemptOnce()
    {
        using var fixture = new HeartbeatFixture();
        fixture.WriteHeartbeat(Start);
        var firstCheck = new GateLoadContextProbe.LiveGateOccupant(
            1234, fixture.Attempt.GoalId, 0, TimeSpan.Zero,
            SourcePath: "check-one.heartbeat.json", RunClass: GateHeartbeatRunClass.Acceptance,
            RunId: fixture.Attempt.AttemptId);
        var secondCheck = firstCheck with { SourcePath = "check-two.heartbeat.json" };

        var preparingOnly = ActivationAdmissionPreparingAttemptCensus.Evaluate(
            [fixture.Attempt], [], Start, _ => SpawnTrackedProcessStatus.LiveMatch);
        Assert.Equal(1, preparingOnly.Live);
        Assert.Equal(1, preparingOnly.Preparing);

        var overlapping = ActivationAdmissionPreparingAttemptCensus.Evaluate(
            [fixture.Attempt, fixture.Attempt], [firstCheck, secondCheck], Start,
            _ => SpawnTrackedProcessStatus.LiveMatch);

        Assert.Equal(1, overlapping.Live);
        Assert.Equal(0, overlapping.Preparing);
    }

    [Xunit.Fact]
    public void Evaluate_HeartbeatAtFreshnessBoundary_CountsPreparingAttempt()
    {
        using var fixture = new HeartbeatFixture();
        fixture.WriteHeartbeat(Start - GateLoadContextProbe.LiveGateHeartbeatFreshness);

        var tally = ActivationAdmissionPreparingAttemptCensus.Evaluate(
            [fixture.Attempt], [], Start, _ => SpawnTrackedProcessStatus.LiveMatch);

        Assert.Equal(1, tally.Live);
        Assert.Equal(1, tally.Preparing);
    }

    private static ActivationAdmissionHealthVerdict Observe(ActivationAdmissionHealthTracker tracker,
        ActivationAdmissionLiveGateTally tally, int tick, DateTimeOffset now) =>
        tracker.Observe(new ActivationAdmissionSample(tick, CensusFailed: false,
            WaitingVerified: 1, Occupied: 1, LiveGates: tally.Live,
            EvidenceHeldPermits: 0, PreparingAttempts: tally.Preparing), now);

    private sealed class HeartbeatFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(),
            "preparing-census-" + Guid.NewGuid().ToString("N"));

        internal ConductorParallelAcceptanceAttempt Attempt { get; }

        internal HeartbeatFixture()
        {
            Directory.CreateDirectory(_directory);
            Attempt = new ConductorParallelAcceptanceAttempt(
                AttemptId: "attempt-one", GoalId: "goal-one", GoalPrefix: "goal-one",
                SlotIndex: 0, BranchHeadSha: null, MainHeadSha: null,
                StartedAt: Start, LastHeartbeatAt: Start, OwnerProcessId: 1234,
                Outcome: ConductorParallelAcceptanceAttemptOutcome.Running,
                StdoutPath: "", StderrPath: "", ExitCodePath: "",
                HeartbeatPath: Path.Combine(_directory, "attempt-one.heartbeat.json"),
                ResultPath: "", MetadataPath: "")
            {
                OwnerProcessStartedAt = Start.AddMinutes(-1),
                OwnerExecutablePath = "gate-owner.exe"
            };
        }

        internal void WriteHeartbeat(DateTimeOffset lastProgressAt) =>
            File.WriteAllText(Attempt.HeartbeatPath, JsonSerializer.Serialize(new
            {
                kind = "gate", state = "running", lastProgressAt
            }));

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
