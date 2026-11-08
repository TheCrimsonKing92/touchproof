using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: immutable snapshots and fixed elapsed values.
public sealed class OwnerConsoleGateStatusSegmentTests
{
    private const string Execution = "41a09434-0-20261105131307015-97f069e446d6477bb6d563c41853a816";

    [Theory]
    [InlineData(false, false, "gate: solo 41a0 running 18m")]
    [InlineData(true, false, "gate: solo 41a0 3/5 running 18m")]
    [InlineData(true, true, "gate: solo 41a0 3/5 ~6m")]
    public void RunningGate_ShowsOnlyKnownFacts(bool plan, bool eta, string expected)
    {
        var gate = Model() with { LanePlanAvailable = plan, EtaAvailable = eta };

        var segment = OwnerConsoleGateFormatter.StatusSegment(gate);

        Assert.Equal(expected, segment);
        Assert.DoesNotContain("schedule unknown", segment);
        Assert.DoesNotContain("ETA unknown", segment);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExecutionIdentity_RemainsInGateAndFieldsOnly(bool live)
    {
        var gate = Model() with { Live = live, Participation = live ? null : "historical execution" };

        var segment = OwnerConsoleGateFormatter.StatusSegment(gate);

        // Every possible nine-character window would expose too much of the ID.
        for (var start = 0; start <= Execution.Length - 9; start++)
            Assert.DoesNotContain(Execution.Substring(start, 9), segment);
        Assert.Contains(OwnerConsoleGateLayout.Lines(gate, 180, false),
            line => line.Contains(Execution, StringComparison.Ordinal));
        Assert.Equal(Execution, Assert.Single(new OwnerConsoleGateFieldProjection(gate).FieldSnapshot().Fields, field => field.Key == "execution").Value);
    }

    [Fact]
    public void ConcurrentRuns_KeepKnownFactsOnOneLineWithoutExecutionIds()
    {
        var first = Model();
        var second = first with { ExecutionId = Execution.Replace("-0-", "-1-", StringComparison.Ordinal) };
        var gate = OwnerConsoleGateProgress.Idle() with { ConcurrentRuns = [first, second] };

        var segment = OwnerConsoleGateFormatter.StatusSegment(gate);

        Assert.Equal("gate: 2 live: solo 41a0 running 18m; solo 41a0 running 18m", segment);
        Assert.DoesNotContain('\n', segment);
        Assert.DoesNotContain("unknown", segment);
        Assert.DoesNotContain(first.ExecutionId!, segment);
        Assert.DoesNotContain(second.ExecutionId!, segment);
        var lines = OwnerConsoleGateLayout.Lines(gate, 200, false);
        Assert.Contains(lines, line => line.Contains(first.ExecutionId!, StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains(second.ExecutionId!, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failure_RemainsVisibleWithoutAPlan(bool compact)
    {
        var gate = Model() with { Lanes = [new("Console", OwnerConsoleGateLaneState.Done, "failed", "local",
            TimeSpan.FromMinutes(1), null, false)] };

        var segment = OwnerConsoleGateFormatter.StatusSegment(gate, compact);

        Assert.Contains("FAILING", segment);
        Assert.DoesNotContain("unknown", segment);
        Assert.DoesNotContain(Execution, segment);
    }

    private static OwnerConsoleGateProgress Model() => new(true, "solo", [new("41a09434-full-member-id", "Goal")],
        DateTimeOffset.UnixEpoch, TimeSpan.FromMinutes(18), [], 0, 360, 3, 5,
        LanePlanAvailable: false, ExecutionId: Execution, EtaAvailable: false);
}
