using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static OwnerConsoleGateExecutionReplayFixture;

// Parallel-safe: fixed timestamps and unique per-test plan artifacts.
public sealed class OwnerConsoleGateExecutionReplayTests
{
    [Fact]
    public void CohortExecutionIdentityReachesPlanAndIsolatesSameRosterReruns()
    {
        using var fixture = new OwnerConsoleGateExecutionReplayFixture();
        AcceptanceCohortMemberBinding Member(char digit) => new(new GoalId(new string(digit, 32)),
            new string('a', 40), new string('b', 40), ["src/A.cs"], ["resource:a"], ChangeRiskTier.Behavior,
            ConductorTransitionDecision.Auto, "Clean", "NoConflictsDetected");
        var members = new[] { Member('1'), Member('2') };
        var identity = AcceptanceCohortIdentity.Create(members, new string('c', 40), new string('d', 40), "manifest-v1");
        const string oldExecution = "operator-gate-20261103203920616-979ad9a676b14827bc504e99f624e75e";
        const string currentExecution = "operator-gate-20261103210123951-847dff45d61847a9a48953b999850b28";
        fixture.WritePlan(oldExecution, ["Console", "Obsolete"]);
        fixture.WritePlan(currentExecution, ["Console", "Console:expanded"]);
        var path = Path.Combine(fixture.Root, "conduct-events.log");
        var writer = new ConductEventLogWriter(path);
        foreach (var execution in new[] { oldExecution, currentExecution })
        {
            // A null goal produces operator-gate run IDs, outside the legacy
            // goal-prefixed heartbeat pattern. The explicit token must survive.
            var progress = new AcceptanceGateProgress(null, "lane-plan", "plan", null, null, null,
                OwnerConsoleGateExecutionReplayFixture.Now, OwnerConsoleGateExecutionReplayFixture.Now,
                OwnerConsoleGateExecutionReplayFixture.Now, TimeSpan.Zero, 0, fixture.PlanPath(execution), ExecutionId: execution);
            ConductorDriver.AppendCohortGateProgressEvents(writer, identity, members, progress);
            ConductorDriver.AppendCohortGateProgressEvents(writer, identity, members, progress with
            {
                Phase = execution == oldExecution ? "check-complete" : "verification-check",
                CurrentTarget = "Console", Outcome = execution == oldExecution ? "failed" : null
            });
        }
        var events = File.ReadAllLines(path).Select(line => OwnerConsoleGateProgress.ParseEvent(line)!).ToArray();
        Assert.Equal(8, events.Length);
        Assert.All(events, item =>
        {
            Assert.Contains(" execution=operator-gate-", item.Detail);
            Assert.Contains("cohort=" + identity.Value[..18], item.Detail);
            Assert.Contains("members=11111111,22222222", item.Detail);
        });
        var replay = OwnerConsoleGateProgress.Build(events.Max(item => item.Timestamp), events,
            fixture.Manifest, [], planReader: new OwnerConsoleGateLanePlanReader());
        Assert.Equal(2, replay.Executions!.Count);
        Assert.True(replay.ForExecution(oldExecution)!.Failing);
        var current = Assert.IsType<OwnerConsoleGateProgress>(replay.ForExecution(currentExecution));
        Assert.Equal(new[] { "11111111", "22222222" }, current.Members.Select(member => member.Id));
        Assert.True(current.LanePlanAvailable);
        Assert.Equal(2, current.TotalCount);
        Assert.Equal(0, current.DoneCount);
        Assert.False(current.Failing);
        Assert.Equal(OwnerConsoleGateLaneState.Running, Assert.Single(current.Lanes, row => row.Name == "Console").State);
        Assert.DoesNotContain(current.Lanes, row => row.Name == "Obsolete");
    }

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
    public void RemoteAcceptedReceiptRetainsPassedLaneWithoutInventingSchedule()
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
        Assert.Equal(OwnerConsoleGateLaneState.Done, row.State);
        Assert.Equal("passed", row.Outcome);
        Assert.Equal("executor-one", row.Location);
        Assert.Equal(0, gate.TotalCount);
    }

    [Theory]
    [InlineData(-10, false, true)]
    [InlineData(-20, false, true)] // Equal timestamps preserve input event order.
    [InlineData(-30, false, false)]
    [InlineData(-10, true, false)] // Another execution cannot complete this lane.
    public void RemoteReceiptAndLocalProgressUseExecutionLaneAndEventOrder(
        int remoteAge, bool otherExecution, bool completed)
    {
        using var fixture = new OwnerConsoleGateExecutionReplayFixture();
        var gate = fixture.Build([
            Progress(-1800, Current, "execution-admitted", ""),
            Progress(-20, Current, "remote-shard-started", "Console"),
            new(Now.AddSeconds(remoteAge), "remote-lane", null,
                $"REMOTE_LANE attempt={(otherExecution ? Original : Current)} executor=executor-one lane=Console outcome=accepted"),
            new(Now.AddSeconds(-5), "remote-lane", null,
                $"REMOTE_LANE attempt={Current} executor=executor-one lane=Other outcome=accepted")]);
        var row = Assert.Single(gate.Lanes, row => row.Name == "Console");
        Assert.Equal(completed ? OwnerConsoleGateLaneState.Done : OwnerConsoleGateLaneState.Running, row.State);
        Assert.Equal(completed ? "passed" : "unknown", row.Outcome);
        Assert.False(gate.ScheduleKnown);
        Assert.False(gate.HasEta);
        Assert.Equal(0, gate.TotalCount);
        Assert.Equal(OwnerConsoleGateLaneState.Done, Assert.Single(gate.Lanes, row => row.Name == "Other").State);
    }

    [Theory]
    [InlineData("matching", true)]
    [InlineData("missing", false)]
    [InlineData("different-parent", false)]
    [InlineData("different-owner", false)]
    public void AttributionRequiresExplicitParentAndOwnerWithoutCreatingParticipation(
        string link, bool visible)
    {
        using var fixture = new OwnerConsoleGateExecutionReplayFixture();
        var parent = link == "missing" ? "" : " parent_execution=" + (link == "different-parent" ? Original : Current);
        var owner = link == "different-owner" ? "independent" : Outer;
        var events = new[] {
            Progress(-1800, Current, "execution-admitted", ""),
            new OwnerConductEvent(Now.AddSeconds(-10), "gate-progress", Survivor,
                $"PHASE_PROGRESS goal={Survivor} execution=partition-run phase=verification-check target=Console " +
                $"members={Survivor} scope=attribution outer_attempt={owner}" + parent)
        };
        var projection = fixture.Build(events);
        var execution = Assert.Single(projection.Executions!);
        Assert.Equal(Current, execution.ExecutionId);
        Assert.Empty(projection.OtherLiveGates!);
        Assert.Equal(Current, projection.ForExecution(Current)!.ExecutionId);
        Assert.Equal(visible ? 1 : 0, execution.Lanes.Count(row => row.IsAttribution));
        if (visible)
        {
            Assert.True(execution.Live); // Recent auxiliary activity keeps its parent observable.
            Assert.Equal(Current, projection.ForMember(Survivor)!.ExecutionId);
            var row = Assert.Single(execution.Lanes);
            Assert.Equal(Survivor, row.ReproductionMemberId);
            Assert.False(row.IsScheduled);
            Assert.Equal(0, OwnerConsoleGateCount.Of(execution).Total);
        }
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

    [Theory]
    [InlineData("passed")]
    [InlineData("failed")]
    public void SoloProducerLifecycleAndProgressShareOneExecutionThroughCompletion(string outcome)
    {
        using var fixture = new OwnerConsoleGateExecutionReplayFixture();
        OwnerConductEvent Lifecycle(int age, string result) => new(Now.AddSeconds(age), "acceptance", Survivor,
            AcceptanceLifecycleEventFormatter.Format(Survivor, 0, result, Current, 1));
        OwnerConductEvent[] events = [Lifecycle(-30, "started"), Lifecycle(-20, "running")];
        var started = fixture.Build(events);
        Assert.Equal(Current, started.ForMember(Survivor)!.ExecutionId);
        Assert.Single(started.Executions!);

        var progress = new AcceptanceGateProgress(Survivor, "verification-check", "Console", 0, null, null,
            Now.AddSeconds(-30), Now.AddSeconds(-10), Now.AddSeconds(-10), TimeSpan.FromSeconds(20), 0, "",
            ExecutionId: Current);
        events = [.. events, new(Now.AddSeconds(-10), "gate-progress", Survivor, GateProgressConductEvents.Format(progress))];
        var running = fixture.Build(events);
        Assert.Equal(Current, running.ForMember(Survivor)!.ExecutionId);
        Assert.True(running.ForMember(Survivor)!.Live);
        Assert.Single(running.Executions!);

        var completed = fixture.Build(events.Append(Lifecycle(-5, outcome)));
        Assert.False(completed.Live);
        Assert.False(completed.ForExecution(Current)!.Live);
        Assert.False(completed.ForMember(Survivor)?.Live ?? false);
        Assert.Single(completed.Executions!);
        Assert.Empty(completed.OtherLiveGates!);
    }

    [Theory]
    [InlineData("verification-check")]
    [InlineData("execution-admitted")]
    public void CompletedOuterOwnerCannotBeReopenedByUnseenChildOrStaleRoster(string phase)
    {
        using var fixture = new OwnerConsoleGateExecutionReplayFixture();
        OwnerConductEvent[] events = [
            new(Now.AddSeconds(-40), "acceptance-cohort", Survivor,
                $"ACCEPTANCE_COHORT_CHILD_COMPLETED attempt={Outer} members={Survivor},{Companion} outcome=passed"),
            OuterEvent(-30),
            Progress(-20, Current, phase, "Console"),
            Progress(-10, Current, "execution-admitted", ""),
            Progress(-5, Current, "verification-check", "Console")];
        var completed = fixture.Build(events);
        Assert.False(completed.Live);
        var child = Assert.IsType<OwnerConsoleGateProgress>(completed.ForExecution(Current));
        Assert.False(child.Live);
        Assert.Equal("outer attempt completed", child.Participation);
        Assert.Equal(Outer, child.OuterAttemptId);
        Assert.Equal("Console", Assert.Single(child.Lanes).Name);
        Assert.False(completed.ForMember(Survivor)!.Live);

        // Terminal authority belongs to this owner, not every later execution.
        var independent = fixture.Build(events.Append(Progress(-1, Original, "execution-admitted", "",
            members: "aaaaaaaa", outer: "independent-owner")));
        Assert.True(independent.ForExecution(Original)!.Live);
        Assert.False(independent.ForExecution(Current)!.Live);
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
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void LegacyEjectionRequiresAdmissionEvenForADifferentTrain(bool admission, bool admissionAfterProgress)
    {
        using var fixture = new OwnerConsoleGateExecutionReplayFixture();
        var events = new List<OwnerConductEvent> {
            new(Now.AddSeconds(-40), "acceptance-cohort", Ejected,
                $"ACCEPTANCE_COHORT goal={Ejected} result=held reason=\"ejected from train {OriginalTrain} reason=RedNewestMember surviving-receipt=pending\"") };
        if (admission && !admissionAfterProgress)
            events.Add(Progress(-30, Current, "execution-admitted", "", members: Ejected, outer: "different-owner"));
        events.Add(Progress(-20, Current, "verification-check", "Console", members: Ejected, outer: "different-owner"));
        if (admission && admissionAfterProgress)
            events.Add(Progress(-10, Current, "execution-admitted", "", members: Ejected, outer: "different-owner"));
        events.Add(OuterEvent(-5));

        var replay = fixture.Build(events);
        var selected = Assert.IsType<OwnerConsoleGateProgress>(replay.ForMember(Ejected));
        Assert.Equal(admission, selected.Live);
        Assert.Equal(admission, replay.ForExecution(Current)!.Live);
        if (admission) Assert.Equal(Current, selected.ExecutionId);
        else
        {
            Assert.False(replay.Live);
            Assert.Null(selected.ExecutionId);
            Assert.Equal("ejected / awaiting survivor", selected.Participation);
            Assert.Equal(OriginalTrain, selected.TrainIdentity);
        }
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("independent-owner", "readmitted-owner", true)]
    [InlineData("shared-owner", "shared-owner", false)]
    public void ReadmissionClosesOnlyExecutionsWithAKnownSharedOwner(
        string? previousOwner, string? readmittedOwner, bool previousStaysLive)
    {
        using var fixture = new OwnerConsoleGateExecutionReplayFixture();
        OwnerConductEvent Event(int age, string execution, string phase, string member, string? owner) =>
            new(Now.AddSeconds(age), "gate-progress", member,
                $"PHASE_PROGRESS execution={execution} phase={phase} members={member}" +
                (owner is null ? "" : $" outer_attempt={owner}"));
        var events = new List<OwnerConductEvent>
        {
            Event(-50, Original, "execution-admitted", Survivor, previousOwner),
            new(Now.AddSeconds(-40), "acceptance-cohort", Ejected,
                $"ACCEPTANCE_COHORT goal={Ejected} result=held reason=\"ejected from train {OriginalTrain} reason=RedNewestMember surviving-receipt=pending\""),
            Event(-30, Current, "verification-check", Ejected, readmittedOwner)
        };
        var unresolved = fixture.Build(events);
        Assert.False(unresolved.ForExecution(Current)!.Live);
        Assert.Equal("participation unresolved after ejection", unresolved.ForExecution(Current)!.Participation);
        events.Add(Event(-20, Current, "execution-admitted", Ejected, readmittedOwner));

        var replay = fixture.Build(events);
        Assert.Equal(2, replay.Executions!.Count(gate => gate.ExecutionId is not null));
        var previous = replay.ForExecution(Original)!;
        Assert.Equal(previousStaysLive, previous.Live);
        Assert.Equal(previousOwner, previous.OuterAttemptId);
        if (previousStaysLive)
        {
            Assert.Null(previous.Participation);
            Assert.Equal(Original, replay.ForMember(Survivor)!.ExecutionId);
            Assert.True(replay.ForMember(Survivor)!.Live);
        }
        else Assert.Equal("historical execution", previous.Participation);
        var readmitted = replay.ForMember(Ejected)!;
        Assert.True(readmitted.Live);
        Assert.Equal(Current, readmitted.ExecutionId);
        Assert.Equal(readmittedOwner, readmitted.OuterAttemptId);
        Assert.NotNull(readmitted.AdmittedAt);
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
        Assert.Equal("concurrent", gate.ForMember(Survivor)!.Kind);
        Assert.Equal(2, gate.ForMember(Survivor)!.ConcurrentRuns!.Count);
    }
}
