using Mcg.AgentOrchestrator.Infrastructure;
using static AcceptanceSelectedExecutionFixture;

// Parallel-safe: synthetic lane records with injected source reads, no external boundaries.
public sealed class AcceptanceSelectedExecutionShadowTests
{
    [Fact]
    public void Shadow_PreservesOffDispatchAndRecordsProposedExecution()
    {
        var off = Session(AcceptanceSelectedExecutionMode.Off);
        Assert.Null(off.LaneTransform);
        var baseline = Observe(off);
        var shadow = Session(AcceptanceSelectedExecutionMode.Shadow);
        Assert.Same(Lanes, shadow.LaneTransform!(Lanes));
        var observation = Observe(shadow);
        Assert.Equal(baseline.Scheduled, observation.Scheduled);
        Assert.Equal(baseline.Skipped, observation.Skipped);
        Assert.False(shadow.Applied);
        var record = observation.SelectedExecution!;
        Assert.Equal("Shadow", record.Mode);
        Assert.Equal("plan", record.Decision);
        Assert.Equal(5, record.ExecutedClassCount);
        Assert.Equal(0, record.SkippedClassCount);
        Assert.Equal(3, record.ProposedExecutedClassCount);
        Assert.Equal(2, record.ProposedSkippedClassCount);
        Assert.Equal(AcceptanceGateLaneSkip.ShadowLaneNotSelected,
            record.Lanes.Single(lane => lane.Name == "Balanced 2").LaneSkipReason);
        Assert.Equal(AcceptanceGateLaneSkip.ShadowClassNotSelected,
            record.Lanes.Single(lane => lane.Name == "Balanced 1").ClassSkipReason);
    }

    [Fact]
    public void Off_DoesNotReadSelectionAndRecordsDisabled()
    {
        var session = new AcceptanceSelectedExecutionSession(AcceptanceSelectedExecutionMode.Off,
            "candidate", Changes, read: (_, _) => throw new Xunit.Sdk.XunitException("Reader must not run."));
        Assert.Null(session.LaneTransform);
        Assert.Same(Lanes, session.Transform(Lanes));
        Assert.Same(AcceptanceSelectedExecutionRecord.Disabled, Observe(session).SelectedExecution);
    }

    [Fact]
    public void ShadowReadFault_RetainsFullGateAndRecordsUnreadable()
    {
        var session = new AcceptanceSelectedExecutionSession(AcceptanceSelectedExecutionMode.Shadow,
            "candidate", Changes, read: (_, _) => throw new IOException("candidate-read-failed"));
        var observation = Observe(session);
        Assert.Equal(Lanes.Select(lane => lane.Name), observation.Scheduled);
        Assert.Empty(observation.Skipped);
        Assert.Equal("full-gate", observation.SelectedExecution!.Decision);
        Assert.Equal("Unreadable", observation.SelectedExecution.Outcome);
        Assert.Equal("IOException: candidate-read-failed", observation.SelectedExecution.FullGateReason);
    }

    [Fact]
    public void Cancellation_Propagates()
    {
        var session = new AcceptanceSelectedExecutionSession(AcceptanceSelectedExecutionMode.Shadow,
            "candidate", Changes, read: (_, _) => throw new OperationCanceledException());
        Assert.Throws<OperationCanceledException>(() => session.LaneTransform!(Lanes));
    }
}
