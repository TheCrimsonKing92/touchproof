using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel safe: private temporary storage, injected clock and permit probes.
public sealed class BuildPermitGatePrecedenceTests : IDisposable
{
    private readonly DotnetBuildStorageRoot _storage = new(SharedTestSupport.CreateTempDirectory());
    private readonly RecordingTimeProvider _clock = new();

    [Theory]
    [InlineData(1L)]
    [InlineData(5 * TimeSpan.TicksPerMinute - TimeSpan.TicksPerSecond)]
    [InlineData(5 * TimeSpan.TicksPerMinute)]
    [InlineData(30 * TimeSpan.TicksPerMinute)]
    public void WaitingGateIsAdmittedAheadOfEarlierEvidence(long evidenceWaitTicks)
    {
        var queue = new BuildPermitWaitQueue(_storage, _clock, isProcessAlive: _ => true);
        var evidence = Claim("evidence", BuildPermitClaimantKind.FocusedEvidence);
        var gate = Claim("gate", BuildPermitClaimantKind.Gate);
        var original = queue.Register(evidence);
        var gateArrival = original.FirstWaitedAt.AddTicks(evidenceWaitTicks);
        var refreshInterval = TimeSpan.FromMinutes(3);
        while (_clock.GetUtcNow() + refreshInterval < gateArrival)
        {
            _clock.Advance(refreshInterval);
            Assert.Equal(original.FirstWaitedAt, queue.Register(evidence).FirstWaitedAt);
        }
        _clock.Advance(gateArrival - _clock.GetUtcNow());
        queue.Register(gate);

        var ordered = queue.Snapshot();
        Assert.Equal(new[] { gate.Key, evidence.Key }, ordered.Select(ticket => ticket.Key));
        Assert.Equal(gateArrival, ordered[0].FirstWaitedAt);
        Assert.Equal(original.FirstWaitedAt, ordered[1].FirstWaitedAt);
        Assert.True(queue.Admit(gate, unheldPermitCount: 1));
        Assert.False(queue.Admit(evidence, unheldPermitCount: 1));
    }

    [Theory]
    [InlineData(BuildPermitClaimantKind.Gate, false, 1)]
    [InlineData(BuildPermitClaimantKind.FocusedEvidence, true, 0)]
    [InlineData(BuildPermitClaimantKind.WorkerBuild, true, 0)]
    public void FocusedEvidenceLaunchWithSpareCapacity_RefusesOnlyWaitingGate(
        BuildPermitClaimantKind waitingKind, bool mayStart, int waitingGates)
    {
        var queue = new BuildPermitWaitQueue(_storage, _clock, isProcessAlive: _ => true);
        queue.Register(Claim("waiting", waitingKind));

        var snapshot = WorkerBuildCapacityGate.ReadHostSnapshot(_storage, _clock, _ => false);

        Assert.Equal(new WorkerBuildCapacitySnapshot(2, 2, 1, true, waitingGates), snapshot);
        Assert.Equal(mayStart, PreReviewEvidenceLaunchCapacity.MayStart(snapshot));
    }

    [Fact]
    public void MixedEvidenceAndWorkerWaiters_AllowLaunchWithSpareCapacity()
    {
        var queue = new BuildPermitWaitQueue(_storage, _clock, isProcessAlive: _ => true);
        queue.Register(Claim("evidence", BuildPermitClaimantKind.FocusedEvidence));
        queue.Register(Claim("worker", BuildPermitClaimantKind.WorkerBuild));
        var snapshot = WorkerBuildCapacityGate.ReadHostSnapshot(_storage, _clock, _ => false);
        Assert.Equal(new WorkerBuildCapacitySnapshot(2, 2, 2, true), snapshot);

        Assert.True(PreReviewEvidenceLaunchCapacity.MayStart(snapshot with { UnheldPermits = 3, TotalPermits = 3 }));
    }

    [Fact]
    public void GateTicketsKeepFirstWaitOrderAcrossPromotion()
    {
        var queue = new BuildPermitWaitQueue(_storage, _clock, isProcessAlive: _ => true);
        var earlierEvidence = Claim("evidence", BuildPermitClaimantKind.FocusedEvidence);
        var firstGate = Claim("gate-z", BuildPermitClaimantKind.Gate);
        var secondGate = Claim("gate-a", BuildPermitClaimantKind.Gate);
        queue.Register(earlierEvidence);
        _clock.Advance(TimeSpan.FromSeconds(1));
        queue.Register(firstGate);
        _clock.Advance(TimeSpan.FromSeconds(1));
        queue.Register(secondGate);

        var expected = new[] { firstGate.Key, secondGate.Key };
        Assert.Equal(expected, queue.Snapshot().Where(t => t.Kind == BuildPermitClaimantKind.Gate).Select(t => t.Key));

        _clock.Advance(TimeSpan.FromMinutes(3));
        queue.Register(earlierEvidence);
        queue.Register(firstGate);
        queue.Register(secondGate);
        _clock.Advance(TimeSpan.FromMinutes(2));
        var afterPromotion = queue.Snapshot();
        Assert.Contains(afterPromotion, t => t.Key == earlierEvidence.Key);
        Assert.Equal(expected, afterPromotion.Where(t => t.Kind == BuildPermitClaimantKind.Gate).Select(t => t.Key));
    }

    [Fact]
    public void EvidenceTicketsKeepFirstWaitOrderWithALaterGate()
    {
        var queue = new BuildPermitWaitQueue(_storage, _clock, isProcessAlive: _ => true);
        var firstEvidence = Claim("evidence-z", BuildPermitClaimantKind.FocusedEvidence);
        var secondEvidence = Claim("evidence-a", BuildPermitClaimantKind.FocusedEvidence);
        queue.Register(firstEvidence);
        _clock.Advance(TimeSpan.FromTicks(1));
        queue.Register(secondEvidence);
        _clock.Advance(TimeSpan.FromTicks(1));
        queue.Register(Claim("gate", BuildPermitClaimantKind.Gate));

        Assert.Equal(new[] { firstEvidence.Key, secondEvidence.Key },
            queue.Snapshot().Where(t => t.Kind == BuildPermitClaimantKind.FocusedEvidence).Select(t => t.Key));
    }

    [Fact]
    public void WorkersKeepFirstWaitOrderWhenBothOtherKindsReachFiveMinutes()
    {
        var queue = new BuildPermitWaitQueue(_storage, _clock, isProcessAlive: _ => true);
        var firstWorker = Claim("worker-z", BuildPermitClaimantKind.WorkerBuild);
        var secondWorker = Claim("worker-a", BuildPermitClaimantKind.WorkerBuild);
        var gate = Claim("gate", BuildPermitClaimantKind.Gate);
        var evidence = Claim("waiting-evidence", BuildPermitClaimantKind.FocusedEvidence);
        queue.Register(firstWorker);
        _clock.Advance(TimeSpan.FromSeconds(1));
        queue.Register(secondWorker);
        _clock.Advance(TimeSpan.FromSeconds(59));
        queue.Register(gate);
        queue.Register(evidence);

        _clock.Advance(TimeSpan.FromMinutes(2));
        queue.Register(firstWorker);
        queue.Register(secondWorker);
        queue.Register(gate);
        queue.Register(evidence);
        _clock.Advance(TimeSpan.FromMinutes(3) - TimeSpan.FromTicks(1));
        Assert.Equal(new[] { firstWorker.Key, secondWorker.Key, gate.Key, evidence.Key },
            queue.Snapshot().Select(t => t.Key));

        _clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(new[] { gate.Key, evidence.Key, firstWorker.Key, secondWorker.Key },
            queue.Snapshot().Select(t => t.Key));
    }

    [Fact]
    public void InterleavedWorkersKeepTheirPositionsWhenOnlyEvidenceReachesFiveMinutes()
    {
        var queue = new BuildPermitWaitQueue(_storage, _clock, isProcessAlive: _ => true);
        var firstWorker = Claim("worker-z", BuildPermitClaimantKind.WorkerBuild);
        var evidence = Claim("evidence", BuildPermitClaimantKind.FocusedEvidence);
        var secondWorker = Claim("worker-a", BuildPermitClaimantKind.WorkerBuild);
        queue.Register(firstWorker);
        _clock.Advance(TimeSpan.FromMinutes(1));
        queue.Register(evidence);
        _clock.Advance(TimeSpan.FromMinutes(1));
        queue.Register(secondWorker);
        _clock.Advance(TimeSpan.FromMinutes(1));
        var gate = Claim("gate", BuildPermitClaimantKind.Gate);
        queue.Register(gate);

        var beforePromotion = queue.Snapshot();
        Assert.Equal(4, beforePromotion.Count);
        Assert.Equal(firstWorker.Key, beforePromotion[0].Key);
        Assert.Equal(secondWorker.Key, beforePromotion[2].Key);

        queue.Register(firstWorker);
        queue.Register(evidence);
        queue.Register(secondWorker);
        _clock.Advance(TimeSpan.FromMinutes(3));
        var afterPromotion = queue.Snapshot();
        Assert.Equal(4, afterPromotion.Count);
        Assert.Equal(firstWorker.Key, afterPromotion[1].Key);
        Assert.Equal(secondWorker.Key, afterPromotion[2].Key);
    }

    private static BuildPermitClaimant Claim(string key, BuildPermitClaimantKind kind) =>
        new(key, kind, OwnerPid: 123, OwnerBound: false);

    public void Dispose() => SharedTestSupport.RemoveTempDirectory(_storage.RootPath);
}
