using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: pure owner and gate observations; no processes or gate execution.
public sealed class ActivationAdmissionPreparingOwnerCensusTests
{
    [Theory]
    [InlineData("train", "SurvivorsGate")]
    [InlineData("train", "Attribution")]
    [InlineData("train", "FocusedPartition")]
    [InlineData("train", "PassedReceipt")]
    [InlineData("cohort", "Attribution")]
    [InlineData("cohort", "FocusedPartition")]
    [InlineData("cohort", "PassedReceipt")]
    public void Evaluate_PendingOwner_CountsOnceUntilOwnGateIsLive(
        string kind, string step)
    {
        var owner = new ActivationAdmissionSlotOwner(new(kind, "one"), ["survivor", "peer"],
            1234, ["joint-run"], Enum.Parse<ActivationAdmissionOwnerPendingStep>(step));
        var tally = Tally([owner, owner], []);
        Assert.Equal(0, tally.Live);
        Assert.Equal(1, tally.Preparing);
        Assert.Equal($"{kind}:one", tally.PreparingOwners);
        tally = Tally([owner], [Gate(1234, null, null)]);
        Assert.Equal(1, tally.Live);
        Assert.Equal(0, tally.Preparing);
        Assert.Empty(tally.PreparingOwners);
    }

    [Theory]
    [InlineData(1234, null, null)]
    [InlineData(5678, "survivor", null)]
    [InlineData(5678, null, "joint-run")]
    public void Evaluate_OwnGateByProcessMemberOrRun_EndsPreparing(
        int processId, string? goal, string? run)
    {
        var owner = new ActivationAdmissionSlotOwner(new("train", "one"), ["survivor", "peer"],
            1234, ["joint-run"], ActivationAdmissionOwnerPendingStep.SurvivorsGate);
        Assert.Equal(0, Tally([owner], [Gate(processId, goal, run)]).Preparing);
        var focused = Gate(processId, goal, run) with { RunClass = GateHeartbeatRunClass.FocusedEvidence };
        var tally = Tally([owner], [focused]);
        Assert.Equal(0, tally.Live);
        Assert.Equal(1, tally.Preparing);
    }

    [Fact]
    public void Evaluate_ReservationOrUnrelatedGate_UsesOwnerContinuation()
    {
        var owner = new ActivationAdmissionSlotOwner(new("cohort", "one"), ["member"],
            1234, [], ActivationAdmissionOwnerPendingStep.None);
        Assert.Equal(0, Tally([owner], []).Preparing);
        var pending = owner with { PendingStep = ActivationAdmissionOwnerPendingStep.Attribution };
        var tally = Tally([pending], [Gate(5678, "unrelated", "other-run")]);
        Assert.Equal(1, tally.Live);
        Assert.Equal(1, tally.Preparing);
    }

    [Fact]
    public void Evaluate_ChangedStepOrMembers_PreservesOwnerIdleInterval()
    {
        var elapsed = TimeSpan.Zero;
        var tracker = new ActivationAdmissionHealthTracker(() => elapsed);
        var owner = new ActivationAdmissionSlotOwner(new("train", "one"), ["red", "survivor", "peer"],
            1234, ["first-run"], ActivationAdmissionOwnerPendingStep.Attribution);
        Observe(Tally([owner], []));
        elapsed = TimeSpan.FromMinutes(9);
        owner = owner with { MemberGoalIds = ["survivor", "peer"], RunKeys = ["next-run"],
            PendingStep = ActivationAdmissionOwnerPendingStep.SurvivorsGate };
        Assert.Equal(ActivationAdmissionHealthState.Healthy, Observe(Tally([owner], [])).State);
        elapsed = TimeSpan.FromMinutes(11);
        owner = owner with { PendingStep = ActivationAdmissionOwnerPendingStep.PassedReceipt };
        Assert.Equal(ConductorActivationRevertReason.AdmissionOwnerIdle, Observe(Tally([owner], [])).Reason);

        ActivationAdmissionHealthVerdict Observe(ActivationAdmissionLiveGateTally tally) =>
            tracker.Observe(new(1, false, 5, 1, tally.Live, 0, tally.Preparing, tally.PreparingOwners),
                DateTimeOffset.UnixEpoch + elapsed);
    }

    private static ActivationAdmissionLiveGateTally Tally(IReadOnlyList<ActivationAdmissionSlotOwner> owners,
        IReadOnlyList<GateLoadContextProbe.LiveGateOccupant> gates) =>
        ActivationAdmissionPreparingAttemptCensus.Evaluate([], gates, DateTimeOffset.UnixEpoch,
            _ => SpawnTrackedProcessStatus.LiveMatch, owners);

    private static GateLoadContextProbe.LiveGateOccupant Gate(int pid, string? goal, string? run) =>
        new(pid, goal, 0, TimeSpan.Zero, SourcePath: "gate", RunClass: GateHeartbeatRunClass.Acceptance, RunId: run);
}
