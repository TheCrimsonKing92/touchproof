using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: fixed in-memory snapshots; no files, processes or ambient clock.
public sealed class OwnerConsoleGateFailingBoardSuffixTests
{
    [Theory]
    [InlineData("Queued", "")]
    [InlineData("Running", ", confirming")]
    [InlineData("Done", "")]
    public void ConfirmingSuffixUsesExistingActiveConfirmationState(string stateName, string suffix)
    {
        var state = Enum.Parse<OwnerConsoleGateLaneState>(stateName);
        var gate = Gate([
            Lane("Failed lane", OwnerConsoleGateLaneState.Done, "failed") with { FailingTestNames = ["Example.Fails"] },
            Lane("Confirmation", state) with
            {
                IsAttribution = true,
                Target = "reviewer focused evidence: project FullyQualifiedName~Example.Fails"
            }
        ]);

        var observed = OwnerConsoleGateConfirmation.Attach(gate.Lanes);
        Assert.Equal(state == OwnerConsoleGateLaneState.Running, observed[0].Confirmation?.Running == true);
        Assert.Equal("in gate: FAILING, 1 failed: Failed lane" + suffix, Board(gate));
        Assert.Contains(state == OwnerConsoleGateLaneState.Running
            ? "GATE solo | elapsed 31m | FAILING, confirming 1 failure(s)"
            : "GATE solo | elapsed 31m | FAILING", OwnerConsoleGateLayout.Lines(gate, 240, false));
    }

    [Fact]
    public void MultipleFailuresNameFirstManifestLaneInPanelOrderAndAdditionalCount()
    {
        var gate = Gate([
            Lane("Check first in snapshot", OwnerConsoleGateLaneState.Done, "failed") with { IsManifestLane = false },
            Lane("First manifest lane", OwnerConsoleGateLaneState.Done, "failed"),
            Lane("Second manifest lane", OwnerConsoleGateLaneState.Done, "failed")
        ]);

        Assert.Equal("in gate: FAILING, 3 failed: First manifest lane +2", Board(gate));
        Assert.Equal(new[] { "First manifest lane", "Second manifest lane", "Check first in snapshot" },
            OwnerConsoleGateCount.Of(gate).FailedItems.Select(row => row.Name));
    }

    [Fact]
    public void NonFailingBoardKeepsWordingAndEstimateWithSharedCounts()
    {
        var gate = Gate([
            Lane("Passed", OwnerConsoleGateLaneState.Done, "passed"),
            Lane("Running check", OwnerConsoleGateLaneState.Running) with { IsManifestLane = false },
            Lane("Queued", OwnerConsoleGateLaneState.Queued)
        ]);

        Assert.False(gate.Failing);
        Assert.Equal("in gate: 1/3, ~6m left", Board(gate));
    }

    [Fact]
    public void EmptyLaneSnapshotPreservesStoredProgress()
    {
        Assert.Equal("in gate: 24/33, ~6m left", Board(Gate([]) with { DoneCount = 24, TotalCount = 33 }));
    }

    [Fact]
    public void FailingFlagWithoutLogicalFailedItemRemainsVisible()
    {
        var gate = Gate([Lane("Comparison [trusted main baseline]", OwnerConsoleGateLaneState.Done, "failed")]);

        Assert.True(gate.Failing);
        Assert.Equal("in gate: FAILING, 0 failed", Board(gate));
    }

    private static OwnerConsoleGateLane Lane(string name, OwnerConsoleGateLaneState state, string outcome = "unknown") =>
        new(name, state, outcome, "local", TimeSpan.Zero, 60, false);

    private static OwnerConsoleGateProgress Gate(IReadOnlyList<OwnerConsoleGateLane> lanes) =>
        new(true, "solo", [new("79056401", "Fixture")], DateTimeOffset.UnixEpoch, TimeSpan.FromMinutes(31),
            lanes, .9, 360, 0, 0);

    private static string Board(OwnerConsoleGateProgress gate)
    {
        var goal = new Goal(new("79056401"), "Fixture", [new(new("fixture-task"), "Work", AgentRole.Developer)]);
        return OwnerConsoleStageDescriber.Describe(goal,
            new(OwnerConsoleAttention.GateRunning, null, goal.Id.Value), null, DateTimeOffset.UnixEpoch, gate);
    }
}
