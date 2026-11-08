using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: immutable in-memory snapshots and a fixed clock; no shared resources.
public sealed class OwnerConsoleGateShortIdentityTests
{
    private const string Execution = "2ecc4d01-0-20261105164526769-5900ed863c3b4a5294001abb352bb20c";
    private const string OtherExecution = "operator-gate-20261104232553958-4c4f3804";

    [Fact]
    public void SoloBoardStatus_ShowsCountAndRemainingWithoutExecutionId()
    {
        var board = Board(Model());

        Assert.Equal("in gate: 24/27, ~<1m left", board);
        AssertNoLongIdentity(Execution, board);
    }

    [Fact]
    public void WaitingBoardStatus_ShowsParticipationWithoutExecutionId()
    {
        var gate = Model() with { Live = false, Participation = "ejected / awaiting survivor" };

        var board = Board(gate, OwnerConsoleAttention.WaitingForGate);

        Assert.Equal("gate: ejected / awaiting survivor", board);
        AssertNoLongIdentity(Execution, board);
    }

    [Fact]
    public void ConcurrentBoardLines_ShowDistinctEightCharacterPrefixes()
    {
        var gate = Model() with { ConcurrentRuns = [Model(), Model() with { ExecutionId = OtherExecution }] };

        var lines = Board(gate).Split('\n');

        Assert.Equal(2, lines.Length);
        Assert.Equal("in gate: solo 79056401 running 24/27 ~<1m left [2ecc4d01]", lines[0]);
        Assert.Equal("in gate: solo 79056401 running 24/27 ~<1m left [operator]", lines[1]);
        foreach (var line in lines)
        {
            AssertNoLongIdentity(Execution, line);
            AssertNoLongIdentity(OtherExecution, line);
        }
    }

    [Fact]
    public void GoalDetailGateLine_ShowsEightCharacterPrefixBeforeGateViewHint()
    {
        var line = OwnerGoalDetailGateLine.Text(Model());

        Assert.Equal("solo, 1 member, 24/27 lanes, 0 failed, ~<1m left [2ecc4d01] (g opens the gate view)", line);
        AssertNoLongIdentity(Execution, line);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("tiny", " [tiny]")]
    [InlineData("12345678", " [12345678]")]
    public void ShortDisplays_PreserveNullAndShortIdentities(string? execution, string suffix)
    {
        var run = Model() with { ExecutionId = execution };
        var gate = run with { ConcurrentRuns = [run, Model() with { ExecutionId = OtherExecution }] };

        Assert.Equal("in gate: solo 79056401 running 24/27 ~<1m left" + suffix, Board(gate).Split('\n')[0]);
        Assert.Equal("solo, 1 member, 24/27 lanes, 0 failed, ~<1m left" + suffix + " (g opens the gate view)",
            OwnerGoalDetailGateLine.Text(run));
    }

    [Fact]
    public void GateViewAndFields_KeepFullExecutionId()
    {
        var solo = Model();

        Assert.Contains("Execution: " + Execution, OwnerConsoleGateLayout.Lines(solo, 240, false));
        Assert.Equal(Execution, Assert.Single(new OwnerConsoleGateFieldProjection(solo).FieldSnapshot().Fields, field => field.Key == "execution").Value);

        var gate = solo with { ConcurrentRuns = [solo, solo with { ExecutionId = OtherExecution }] };
        var fields = new Dictionary<int, OwnerConsoleFieldSnapshot>();
        var lines = OwnerConsoleGateLayout.Lines(gate, 240, false, fields);

        Assert.Equal(2, fields.Count);
        foreach (var execution in new[] { Execution, OtherExecution })
        {
            var entry = Assert.Single(fields, entry => entry.Value.Fields.Any(field =>
                field.Key == "execution" && field.Value == execution));
            Assert.Equal("solo 79056401 running 24/27 ~<1m left [" + execution + "]", lines[entry.Key]);
            Assert.Equal(execution, Assert.Single(entry.Value.Fields, field => field.Key == "execution").Value);
        }
    }

    private static OwnerConsoleGateProgress Model() => new(true, "solo", [new("79056401", "Fixture")],
        DateTimeOffset.UnixEpoch, TimeSpan.FromMinutes(18), [], 24d / 27, 30, 24, 27,
        LanePlanAvailable: true, ExecutionId: Execution, EtaAvailable: true);

    private static string Board(OwnerConsoleGateProgress gate, OwnerConsoleAttention rank = OwnerConsoleAttention.GateRunning)
    {
        var goal = new Goal(new("79056401"), "Fixture", [new(new("fixture-task"), "Work", AgentRole.Developer)]);
        return OwnerConsoleStageDescriber.Describe(goal, new(rank, null, goal.Id.Value), null,
            DateTimeOffset.UnixEpoch, gate);
    }

    private static void AssertNoLongIdentity(string execution, string text)
    {
        for (var start = 0; start <= execution.Length - 9; start++)
            Assert.DoesNotContain(execution.Substring(start, 9), text);
    }
}
