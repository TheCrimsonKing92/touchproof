using Mcg.AgentOrchestrator.App.OwnerConsole;
using static OwnerConsoleGateExecutionReplayFixture;

// Parallel-safe: fixed timestamps and unique per-test plan artifacts.
public sealed class OwnerConsoleGateExecutionReplayTests
{
    [Fact]
    public void EjectionAndSurvivorOverrideStaleOuterRosterWithoutImportingOldFailures()
    {
        using var fixture = new OwnerConsoleGateExecutionReplayFixture();
        var gate = fixture.Build();
        var ejected = Assert.IsType<OwnerConsoleGateProgress>(gate.ForMember(Ejected));
        Assert.False(ejected.Live);
        Assert.Equal("ejected / awaiting survivor", ejected.Participation);
        Assert.Equal(Original, ejected.ExecutionId);
        foreach (var id in new[] { Survivor, Companion })
        {
            var survivor = Assert.IsType<OwnerConsoleGateProgress>(gate.ForMember(id));
            Assert.True(survivor.Live);
            Assert.Equal(Current, survivor.ExecutionId);
            Assert.Equal(Outer, survivor.OuterAttemptId);
            Assert.Equal(new[] { Survivor, Companion }, survivor.Members.Select(member => member.Id));
            Assert.False(survivor.Failing);
            var count = OwnerConsoleGateCount.Of(survivor);
            Assert.Equal(2, count.Total);
            Assert.Equal(1, count.Done);
            Assert.Equal(1, count.Running);
            Assert.DoesNotContain(survivor.Lanes, row => row.Name == "Obsolete");
            Assert.False(Assert.Single(survivor.Lanes, row => row.Name == "build check").IsScheduled);
        }
        Assert.Empty(gate.OtherLiveGates!);
    }

    [Fact]
    public void DynamicScheduleReconcilesExactNamesAndNeverCopiesOtherAttemptObservations()
    {
        using var fixture = new OwnerConsoleGateExecutionReplayFixture();
        fixture.Events.Add(Progress(-50, Current, "check-complete", "Console:new", "passed"));
        var before = fixture.Build().ForMember(Survivor)!;
        Assert.False(Assert.Single(before.Lanes, row => row.Name == "Console:new").IsScheduled);
        fixture.WritePlan(Current, ["Console", "Console:expanded", "Console:new", "Unobserved"]);
        var after = fixture.Build().ForMember(Survivor)!;
        Assert.Equal(2, before.TotalCount);
        Assert.Equal(4, after.TotalCount);
        Assert.Equal(2, after.DoneCount);
        Assert.Equal(4, after.Lanes.Count(row => row.IsScheduled == true));
        Assert.Single(after.Lanes, row => row.Name == "Console:new");
        var planned = Assert.Single(after.Lanes, row => row.Name == "Unobserved");
        Assert.Equal(OwnerConsoleGateLaneState.Planned, planned.State);
        Assert.Empty(planned.Location);
        Assert.False(after.HasEta); // Dynamic name has no supported duration estimate.
        Assert.DoesNotContain(after.Lanes, row => row.Outcome == "failed");
    }

    [Fact]
    public void AuxiliaryFailuresRemainVisibleWithoutChangingScheduledArithmetic()
    {
        using var fixture = new OwnerConsoleGateExecutionReplayFixture();
        fixture.Events.Add(Progress(-20, Current, "check-complete", "build check", "failed"));
        var gate = fixture.Build().ForMember(Survivor)!;
        Assert.True(gate.Failing);
        var count = OwnerConsoleGateCount.Of(gate);
        Assert.Equal(2, count.Total);
        Assert.Equal(1, count.Done);
        Assert.Equal(0, count.Failed);
        Assert.Equal("build check", Assert.Single(count.FailedItems).Name);
        Assert.Contains("1 failed: build check", OwnerConsoleGateBoardStatus.Failing(count));
        Assert.Contains(OwnerConsoleGateLayout.Lines(gate, 200, true), line => line.Contains("build check", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("mismatched")]
    [InlineData("duplicate")]
    public void UnavailablePlanKeepsObservedActivityAndUnknownSchedule(string fault)
    {
        using var fixture = new OwnerConsoleGateExecutionReplayFixture();
        var path = fixture.PlanPath(Current);
        if (fault == "missing") File.Delete(path);
        if (fault == "corrupt") File.WriteAllText(path, "{");
        if (fault == "mismatched") File.Copy(fixture.PlanPath(Original), path, true);
        if (fault == "duplicate") fixture.WritePlan(Current, ["Console", "Console"]);
        var gate = fixture.Build().ForMember(Survivor)!;
        Assert.False(gate.LanePlanAvailable);
        Assert.False(gate.ScheduleKnown);
        Assert.False(gate.HasEta);
        Assert.Equal(0, OwnerConsoleGateCount.Of(gate).Total);
        Assert.Equal(3, gate.Lanes.Count);
        Assert.Contains(gate.Lanes, row => row.Name == "Console" && row.State == OwnerConsoleGateLaneState.Done);
        Assert.Contains(gate.Lanes, row => row.Name == "Console:expanded" && row.State == OwnerConsoleGateLaneState.Running);
        Assert.DoesNotContain(gate.Lanes, row => row.State is OwnerConsoleGateLaneState.Planned or OwnerConsoleGateLaneState.Queued);
        Assert.Contains("ETA unknown", OwnerConsoleGateFormatter.ProgressLine(gate));
    }

    [Fact]
    public void AttemptBoundRemoteActivityRetainsObservedRunningLaneWithoutInventingSchedule()
    {
        using var fixture = new OwnerConsoleGateExecutionReplayFixture();
        var gate = fixture.Build([
            Progress(-1800, Current, "execution-admitted", ""),
            new(Now.AddSeconds(-10), "remote-lane", null,
                $"REMOTE_LANE attempt={Current} executor=executor-one lane=Console outcome=accepted")]);
        Assert.True(gate.Live);
        Assert.False(gate.ScheduleKnown);
        var row = Assert.Single(gate.Lanes);
        Assert.Equal("Console", row.Name);
        Assert.Equal(OwnerConsoleGateLaneState.Running, row.State);
        Assert.Equal("executor-one", row.Location);
        Assert.Equal(0, gate.TotalCount);
    }

    [Fact]
    public void TailWithoutLineageDoesNotInventSurvivorRelationship()
    {
        using var fixture = new OwnerConsoleGateExecutionReplayFixture();
        var tail = fixture.Events.Where(item => item.Timestamp >= Now.AddSeconds(-540))
            .Select(item => item with { Detail = item.Detail.Replace("outer_attempt=" + Outer, "", StringComparison.Ordinal) });
        var gate = fixture.Build(tail);
        Assert.Equal("awaiting execution evidence", gate.ForMember(Ejected)!.Participation);
        Assert.Null(gate.ForMember(Ejected)!.ExecutionId);
        var current = gate.ForMember(Survivor)!;
        Assert.Null(current.OuterAttemptId);
        Assert.Equal(Current, current.ExecutionId);
        Assert.Equal("passed", Assert.Single(current.Lanes, row => row.Name == "Console").Outcome);
        var ownershipOnly = fixture.Build([OuterEvent(-10)]).ForMember(Ejected)!;
        Assert.False(ownershipOnly.Live);
        Assert.Null(ownershipOnly.ExecutionId);
        Assert.Equal("awaiting execution evidence", ownershipOnly.Participation);
    }

    [Fact]
    public void SameRosterRerunDoesNotInheritTerminalLanesFromEarlierExecution()
    {
        using var fixture = new OwnerConsoleGateExecutionReplayFixture();
        const string next = "4eb773ae-gate-20261103211300000-cccccccccccccccccccccccccccccccc";
        fixture.WritePlan(next, ["Console", "Console:expanded"]);
        fixture.Events.Add(Progress(-30, next, "execution-admitted", ""));
        fixture.Events.Add(fixture.PlanEvent(-20, next));
        var current = fixture.Build().ForMember(Survivor)!;
        Assert.Equal(next, current.ExecutionId);
        Assert.Equal(0, current.DoneCount);
        Assert.All(current.Lanes, row => Assert.Equal(OwnerConsoleGateLaneState.Planned, row.State));
        Assert.False(current.Failing);
    }

    [Fact]
    public void TruncatedTailDoesNotPromoteUnseenOldProgressWithoutExplicitCurrentAdmission()
    {
        using var fixture = new OwnerConsoleGateExecutionReplayFixture();
        OwnerConductEvent[] tail = [
            Progress(-30, Current, "execution-admitted", ""), fixture.PlanEvent(-20, Current),
            Progress(-10, Original, "shard-complete", "Console", "failed", Ejected + "," + Survivor + "," + Companion)];
        var gate = fixture.Build(tail);
        var selected = gate.ForMember(Survivor)!;
        Assert.False(selected.Live);
        Assert.Null(selected.ExecutionId);
        Assert.Equal("current execution unresolved", selected.Participation);
        Assert.DoesNotContain(gate.ForExecution(Current)!.Lanes, row => row.Outcome == "failed");
        Assert.Equal("failed", Assert.Single(gate.ForExecution(Original)!.Lanes, row => row.Name == "Console").Outcome);
        var admitted = fixture.Build(tail.Append(Progress(-5, Current, "execution-admitted", ""))).ForMember(Survivor)!;
        Assert.True(admitted.Live);
        Assert.Equal(Current, admitted.ExecutionId);
        Assert.False(admitted.Failing);
    }

    [Fact]
    public void LegacyEjectionReceiptRetainsDispositionWhenParentEvidenceWasNeverRecorded()
    {
        using var fixture = new OwnerConsoleGateExecutionReplayFixture();
        var gate = fixture.Build([
            OuterEvent(-20),
            new(Now.AddSeconds(-10), "acceptance-cohort", Ejected,
                $"ACCEPTANCE_COHORT goal={Ejected} result=held reason=\"ejected from train {OriginalTrain} reason=RedNewestMember surviving-receipt=pending\""),
            OuterEvent(-5)]);
        var selected = gate.ForMember(Ejected)!;
        Assert.False(selected.Live);
        Assert.Equal("ejected / awaiting survivor", selected.Participation);
        Assert.Null(selected.ExecutionId);
        Assert.Null(selected.OuterAttemptId);
        Assert.Equal(OriginalTrain, selected.TrainIdentity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnlyExplicitAdmissionRestoresAnEjectedParticipation(bool admission)
    {
        using var fixture = new OwnerConsoleGateExecutionReplayFixture();
        const string next = "4eb773ae-gate-20261103211300000-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        fixture.Events.Add(Progress(-30, next, admission ? "execution-admitted" : "verification-check", "Console",
            members: Ejected + "," + Survivor + "," + Companion));
        fixture.Events.Add(OuterEvent(-20));
        var selected = fixture.Build().ForMember(Ejected)!;
        Assert.Equal(admission, selected.Live);
        Assert.Equal(admission ? next : Original, selected.ExecutionId);
    }

    [Fact]
    public void AdmissionWithoutExecutionIdentityCannotRestoreEjectedMember()
    {
        using var fixture = new OwnerConsoleGateExecutionReplayFixture();
        fixture.Events.Add(new(Now.AddSeconds(-20), "gate-progress", Ejected,
            $"phase=execution-admitted outer_attempt={Outer} members={Ejected},{Survivor},{Companion}"));
        var selected = fixture.Build().ForMember(Ejected)!;
        Assert.False(selected.Live);
        Assert.Equal(Original, selected.ExecutionId);
        Assert.Equal("ejected / awaiting survivor", selected.Participation);
    }

    [Theory]
    [InlineData("4eb773ae,c9a50f57")]
    [InlineData("4eb773ae,aaaaaaaa")]
    [InlineData("4eb773ae")]
    public void EqualOverlappingAndSubsetMembershipCannotMergeIndependentExecutions(string members)
    {
        using var fixture = new OwnerConsoleGateExecutionReplayFixture();
        const string independent = "4eb773ae-gate-20261103211300000-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        fixture.Events.Add(Progress(-30, independent, "execution-admitted", "", members: members, outer: "other-owner"));
        fixture.Events.Add(Progress(-20, independent, "verification-check", "Console", members: members, outer: "other-owner"));
        var gate = fixture.Build();
        Assert.Equal(Current, gate.ExecutionId);
        Assert.Equal(independent, Assert.Single(gate.OtherLiveGates!).ExecutionId);
        Assert.Equal(Current, gate.ForExecution(Current)!.ExecutionId);
        Assert.Equal(independent, gate.ForExecution(independent)!.ExecutionId);
        Assert.Equal("unresolved", gate.ForMember(Survivor)!.Kind);
    }
}
