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

    private static BuildPermitClaimant Claim(string key, BuildPermitClaimantKind kind) =>
        new(key, kind, OwnerPid: 123, OwnerBound: false);

    public void Dispose() => SharedTestSupport.RemoveTempDirectory(_storage.RootPath);
}
