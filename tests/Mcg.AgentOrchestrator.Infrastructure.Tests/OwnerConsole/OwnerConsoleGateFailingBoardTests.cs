using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: fixed in-memory snapshots; no files, processes or ambient clock.
public sealed class OwnerConsoleGateFailingBoardTests
{
    [Fact]
    public void FailedMemberShowsFailureAndSharedLogicalCounts()
    {
        var gate = Gate([
            Lane("Dotnet build slots", OwnerConsoleGateLaneState.Done, "failed"),
            Lane("Passed lane", OwnerConsoleGateLaneState.Done, "passed"),
            Lane("Passed check", OwnerConsoleGateLaneState.Done, "passed") with { IsManifestLane = false },
            Lane("Confirmation", OwnerConsoleGateLaneState.Queued) with { IsAttribution = true },
            Lane("Comparison [trusted main baseline]", OwnerConsoleGateLaneState.Done, "passed")
        ]);

        Assert.True(gate.Failing);
        Assert.Equal("in gate: FAILING, 1 failed: Dotnet build slots", Board(gate));
        var count = OwnerConsoleGateCount.Of(gate);
        Assert.Equal(3, count.Done);
        Assert.Equal(3, count.Total);
        Assert.Equal("gate: solo 7905 3/3 FAILING", OwnerConsoleGateFormatter.StatusSegment(gate));
        Assert.Contains(" x 1 failed   > 0 running   + 2 passed   = 0 cached   . 0 queued",
            OwnerConsoleGateLayout.Lines(gate, 240, false));

        // The failing format omits a fraction. Exercise the board's count path with the same population.
        var passed = gate with { Lanes = gate.Lanes.Select((row, index) =>
            index == 0 ? row with { Outcome = "passed" } : row).ToArray() };
        Assert.False(passed.Failing);
        Assert.Equal("in gate: 3/3, ~6m left", Board(passed));
        Assert.Equal("gate: solo 7905 3/3 ~6m", OwnerConsoleGateFormatter.StatusSegment(passed));
        Assert.Contains(" x 0 failed   > 0 running   + 3 passed   = 0 cached   . 0 queued",
            OwnerConsoleGateLayout.Lines(passed, 240, false));
    }

    [Theory]
    [InlineData("Queued")]
    [InlineData("Running")]
    [InlineData("Done")]
    public void LogicalPopulationIsIndependentOfConfirmationAttemptState(string attemptStateName)
    {
        var attemptState = Enum.Parse<OwnerConsoleGateLaneState>(attemptStateName);
        var gate = Gate([
            Lane("Failed lane", OwnerConsoleGateLaneState.Done, "failed"),
            Lane("Passed check", OwnerConsoleGateLaneState.Done, "passed") with { IsManifestLane = false },
            Lane("Cached lane", OwnerConsoleGateLaneState.Cached, "passed"),
            Lane("Running lane", OwnerConsoleGateLaneState.Running),
            Lane("Queued check", OwnerConsoleGateLaneState.Queued) with { IsManifestLane = false },
            Lane("Skipped lane", OwnerConsoleGateLaneState.Skipped),
            Lane("Comparison [trusted main baseline]", OwnerConsoleGateLaneState.Done, "failed"),
            Lane("Confirmation", attemptState, "failed") with { IsAttribution = true }
        ]);

        var count = OwnerConsoleGateCount.Of(gate);
        Assert.Equal(3, count.Done);
        Assert.Equal(5, count.Total);
        Assert.Equal(1, count.Failed);
        Assert.Equal(1, count.Running);
        Assert.Equal(1, count.Passed);
        Assert.Equal(1, count.Cached);
        Assert.Equal(1, count.Queued);
        Assert.Equal("Failed lane", Assert.Single(count.FailedItems).Name);
        Assert.Equal("gate: solo 7905 3/5 FAILING", OwnerConsoleGateFormatter.StatusSegment(gate));
        Assert.Contains(" x 1 failed   > 1 running   + 1 passed   = 1 cached   . 1 queued   - 1 skipped",
            OwnerConsoleGateLayout.Lines(gate, 240, false));
    }

    private static OwnerConsoleGateLane Lane(string name, OwnerConsoleGateLaneState state, string outcome = "unknown") =>
        new(name, state, outcome, "local", TimeSpan.Zero, 60, false);

    private static OwnerConsoleGateProgress Gate(IReadOnlyList<OwnerConsoleGateLane> lanes) =>
        new(true, "solo", [new("79056401", "Fixture")], DateTimeOffset.UnixEpoch, TimeSpan.FromMinutes(31),
            lanes, .9, 360, 1, 2);

    private static string Board(OwnerConsoleGateProgress gate)
    {
        var goal = new Goal(new("79056401"), "Fixture", [new(new("fixture-task"), "Work", AgentRole.Developer)]);
        return OwnerConsoleStageDescriber.Describe(goal,
            new(OwnerConsoleAttention.GateRunning, null, goal.Id.Value), null, DateTimeOffset.UnixEpoch, gate);
    }
}
