using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using static OwnerConsoleGateExecutionReplayFixture;

// Parallel-safe: private replay artifacts and uninitialized per-test views.
public sealed class OwnerConsoleGateProjectionReplayTests
{
    [Theory]
    [InlineData(Ejected, Original, "ejected / awaiting survivor")]
    [InlineData(Survivor, Current, "1/2")]
    [InlineData(Companion, Current, "1/2")]
    public void BoardDetailAndDialogUseSameGoalExecutionMembershipAndCounts(string id, string execution, string state)
    {
        using var fixture = new OwnerConsoleGateExecutionReplayFixture();
        var gate = fixture.Build();
        var goal = Goal(id);
        var detail = new OwnerGoalDetailSnapshot(goal, null, []).WithGate(gate);
        Assert.Same(goal, detail.Goal);
        Assert.Equal(execution, detail.Gate!.ExecutionId);
        var board = OwnerConsoleStageDescriber.Describe(goal,
            new(OwnerConsoleAttention.GateRunning, null, id), null, Now, gate);
        var detailLine = OwnerGoalDetailGateLine.Text(detail.Gate);
        Assert.Contains(state, board);
        Assert.Contains(state, detailLine);
        Assert.Contains(execution, board);
        Assert.Contains(execution, detailLine);
        using var dialog = new OwnerConsoleGateDialog();
        dialog.Open(OwnerConsolePane.Board, gate, id);
        var dialogText = string.Join('\n', dialog.Lines);
        Assert.Equal(OwnerConsoleGateLayout.Lines(detail.Gate, OwnerConsoleEpicFormatter.Width(dialog.BodyPane.Viewport.Width), false), dialog.Lines);
        if (id == Ejected)
        {
            Assert.False(detail.Gate.Live);
            Assert.Contains(state, dialogText);
        }
        else
        {
            Assert.Equal(new[] { Survivor, Companion }, detail.Gate.Members.Select(member => member.Id));
            Assert.True(detail.Gate.LanePlanAvailable);
            Assert.Equal(OwnerConsoleGateCount.Of(gate.ForMember(id)!).Total, OwnerConsoleGateCount.Of(detail.Gate).Total);
            Assert.Equal(OwnerConsoleGateCount.Of(gate.ForMember(id)!).Done, OwnerConsoleGateCount.Of(detail.Gate).Done);
        }
    }

    [Fact]
    public void UnknownPlanHasConsistentUnknownCountAndEtaAcrossSurfaces()
    {
        using var fixture = new OwnerConsoleGateExecutionReplayFixture();
        File.Delete(fixture.PlanPath(Current));
        var gate = fixture.Build();
        var goal = Goal(Survivor);
        var selected = new OwnerGoalDetailSnapshot(goal, null, []).WithGate(gate).Gate!;
        var board = OwnerConsoleStageDescriber.Describe(goal,
            new(OwnerConsoleAttention.GateRunning, null, Survivor), null, Now, gate);
        Assert.Contains("schedule unknown", board);
        Assert.Contains("ETA unknown", board);
        Assert.Contains("schedule unknown, ETA unknown", OwnerGoalDetailGateLine.Text(selected));
        Assert.Contains("scheduled total unknown; ETA unknown", OwnerConsoleGateFormatter.ProgressLine(selected));
        Assert.False(selected.ScheduleKnown);
        Assert.Equal(3, selected.Lanes.Count);
    }

    [Fact]
    public void DialogKeepsHistoricalExecutionAndNeverFallsBackToUnrelatedGoal()
    {
        using var fixture = new OwnerConsoleGateExecutionReplayFixture();
        var gate = fixture.Build();
        using var historical = new OwnerConsoleGateDialog();
        historical.Open(OwnerConsolePane.Board, gate.ForExecution(Original));
        historical.Render(gate);
        Assert.Equal(OwnerConsoleGateLayout.Lines(gate.ForExecution(Original),
            OwnerConsoleEpicFormatter.Width(historical.BodyPane.Viewport.Width), false), historical.Lines);

        using var selectedGoal = new OwnerConsoleGateDialog();
        selectedGoal.Open(OwnerConsolePane.Board, gate, Ejected);
        selectedGoal.Render(fixture.Build([Progress(-10, Current, "verification-check", "Console")]));
        Assert.Contains(selectedGoal.Lines, line => line.Contains("selected goal execution unresolved", StringComparison.Ordinal));
        Assert.DoesNotContain(selectedGoal.Lines, line => line.Contains("gate running", StringComparison.Ordinal));
        selectedGoal.Render(gate);
        Assert.Equal(OwnerConsoleGateLayout.Lines(gate.ForMember(Ejected),
            OwnerConsoleEpicFormatter.Width(selectedGoal.BodyPane.Viewport.Width), false), selectedGoal.Lines);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void TerminalUnknownAuxiliary_RemainsVisibleWithoutChangingSchedule(
        bool expanded, bool attribution)
    {
        using var fixture = new OwnerConsoleGateExecutionReplayFixture();
        var before = fixture.Build().ForMember(Survivor)!;
        fixture.Events.Add(attribution
            ? new(Now.AddSeconds(-10), "gate-progress", Survivor,
                $"PHASE_PROGRESS goal={Survivor} execution=partition-run phase=attribution-complete " +
                $"target=Console members={Survivor} scope=attribution outer_attempt={Outer} " +
                $"parent_execution={Current} outcome=unknown elapsed_ms=1000")
            : Progress(-10, Current, "check-complete", "auxiliary check"));
        var gate = new OwnerGoalDetailSnapshot(Goal(Survivor), null, [])
            .WithGate(fixture.Build()).Gate!;
        var auxiliary = Assert.Single(gate.Lanes, row => attribution
            ? row.IsAttribution : row.Name == "auxiliary check");
        Assert.Equal(OwnerConsoleGateLaneState.Done, auxiliary.State);
        Assert.Equal("unknown", auxiliary.Outcome);
        Assert.False(auxiliary.IsScheduled);

        var lines = OwnerConsoleGateLayout.Lines(gate, 180, expanded);
        Assert.Contains("AUXILIARY (outcome unknown)", lines);
        var name = attribution ? "reproduction " + Survivor + ": unknown" : "auxiliary check";
        Assert.Single(lines, line => line.Contains(name, StringComparison.Ordinal));
        var count = OwnerConsoleGateCount.Of(gate);
        Assert.Equal(2, count.Total);
        Assert.Equal(1, count.Done);
        Assert.Equal(1, count.Passed);
        Assert.Equal(1, count.Running);
        Assert.Equal(0, count.Unknown);
        Assert.Equal(before.Failing, gate.Failing);
        Assert.Equal(OwnerConsoleGateFormatter.CountText(before), OwnerConsoleGateFormatter.CountText(gate));
        Assert.Equal(OwnerConsoleGateFormatter.ProgressLine(before), OwnerConsoleGateFormatter.ProgressLine(gate));
    }

    private static Goal Goal(string id) => new(new(id), "Replay " + id, [new(new("task-" + id), "Work", AgentRole.Developer)]);
}
