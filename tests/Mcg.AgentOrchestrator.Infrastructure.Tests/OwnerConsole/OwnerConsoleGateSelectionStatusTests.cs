using Mcg.AgentOrchestrator.App.OwnerConsole;
using static OwnerConsoleGateStatusTestFixture;

// Parallel-safe: unique temporary files, fixed clock, no shared state or processes.
public sealed class OwnerConsoleGateSelectionStatusTests
{
    [Fact]
    public void FullGateShowsAbbreviatedBoardReasonAndWholeDetailReason()
    {
        using var fixture = new OwnerConsoleGateStatusTestFixture();
        const string reason = "UnresolvedObligationSelector: Sample.Missing.SelectorTests";
        fixture.WritePlan(Decision("full-gate", reason));
        var gate = fixture.Load();

        Assert.Equal("in gate: full gate 0/1, ~2m left (UnresolvedObligationSelector)", Board(gate));
        Assert.DoesNotContain("Sample.Missing.SelectorTests", Board(gate));
        Assert.Contains("Full-gate reason: " + reason, Detail(gate).Split(Environment.NewLine));
        Assert.Contains("solo, 1 member, full gate", OwnerGoalDetailGateLine.Text(gate));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RecordedPlanDecisionShowsSelectedRegardlessOfModeOutcomeOrApplied(bool applied)
    {
        using var fixture = new OwnerConsoleGateStatusTestFixture();
        fixture.WritePlan(Decision("plan", "Unused reason", applied));
        var gate = fixture.Load();

        Assert.Equal("in gate: 0/1 selected, ~2m left", Board(gate));
        Assert.DoesNotContain("full gate", Board(gate));
        Assert.DoesNotContain("Full-gate reason:", Detail(gate));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void FullGateWithoutReasonDoesNotInventOne(string? reason)
    {
        using var fixture = new OwnerConsoleGateStatusTestFixture();
        fixture.WritePlan(Decision("full-gate", reason));
        var gate = fixture.Load();

        Assert.Equal("in gate: full gate 0/1, ~2m left", Board(gate));
        Assert.DoesNotContain("Full-gate reason:", Detail(gate));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("wrong-attempt")]
    [InlineData("locked")]
    public void UnavailablePlanPreservesExistingText(string fault)
    {
        using var fixture = new OwnerConsoleGateStatusTestFixture();
        fixture.WritePlan(Decision("full-gate", "Hidden reason"));
        Assert.Equal("in gate: full gate 0/1, ~2m left (Hidden reason)", Board(fixture.Load()));
        File.Delete(fixture.PlanPath);
        if (fault != "missing") fixture.WritePlan(Decision("full-gate", "Hidden reason"),
            recordAttempt: fault == "wrong-attempt" ? NextAttempt : Attempt);
        if (fault == "malformed") File.WriteAllText(fixture.PlanPath, "{");
        using var locked = fault == "locked" ? File.Open(fixture.PlanPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;

        var gate = fixture.Load();

        Assert.False(gate.LanePlanAvailable);
        Assert.Null(gate.Selection);
        Assert.Equal("in gate: schedule unknown, ETA unknown", Board(gate));
        Assert.DoesNotContain("Full-gate reason:", Detail(gate));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("disabled")]
    [InlineData("future-decision")]
    public void UnrecognisedDecisionPreservesExistingCountAndEta(string? decision)
    {
        using var fixture = new OwnerConsoleGateStatusTestFixture();
        fixture.WritePlan(decision is null ? null : Decision(decision, "Hidden reason"));
        var gate = fixture.Load();

        Assert.True(gate.LanePlanAvailable);
        Assert.Null(gate.Selection);
        Assert.Equal("in gate: 0/1, ~2m left", Board(gate));
        Assert.DoesNotContain("Full-gate reason:", Detail(gate));
    }

    [Fact]
    public void NewAttemptDoesNotBorrowOldDecisionOrTiming()
    {
        using var fixture = new OwnerConsoleGateStatusTestFixture();
        fixture.WritePlan(Decision("full-gate", "Old reason"));
        Assert.NotNull(fixture.Load([Progress(0, "verification-check", "A", 220000)]).OverrunSeconds);
        var gate = fixture.Load([
            fixture.PlanEvent(-900),
            new(Now.AddSeconds(-800), "gate-progress", GoalId, $"ACCEPTANCE goal={GoalId} execution={Attempt} result=passed")
        ], execution: NextAttempt);

        Assert.Null(gate.Selection);
        Assert.Null(gate.OverrunSeconds);
        Assert.Equal("in gate: schedule unknown, ETA unknown", Board(gate));
        Assert.DoesNotContain("Old reason", Detail(gate));
    }

    [Fact]
    public void LongMultilineReasonUsesExistingWidthFitterAndStaysWholeInDetails()
    {
        using var fixture = new OwnerConsoleGateStatusTestFixture();
        var category = new string('R', 90);
        fixture.WritePlan(Decision("full-gate", category + ":\r\nSample.SelectorTests"));
        var gate = fixture.Load();
        var board = Board(gate);

        Assert.DoesNotContain('\n', board);
        Assert.Contains(new string('R', 31) + "…", board);
        var cell = OwnerConsoleLineFitter.Cut(board, 42);
        Assert.True(cell.Length <= 42);
        Assert.StartsWith("in gate: full gate 0/1, ~2m left", cell);
        Assert.EndsWith("…", cell);
        Assert.Contains("Full-gate reason: " + category + ": Sample.SelectorTests", Detail(gate).Split(Environment.NewLine));
    }
}
