using Mcg.AgentOrchestrator.App.Orchestration;
using static CostGoalOutcomeBaselineTests;

// Parallel-safe: fixed instants, immutable gate observations and fixture commit ancestry.
public sealed class CostGoalOutcomeVerdictTests
{
    private static readonly DateTimeOffset AdoptedAt = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("decrease", "30%", 100d, 70d, true)]
    [InlineData("decrease", "30%", 100d, 71d, false)]
    [InlineData("decrease", "90%", 100d, 10d, true)]
    [InlineData("increase", "25%", 80d, 100d, true)]
    [InlineData("increase", "25%", 80d, 99d, false)]
    [InlineData("increase", "70", null, 70d, true)]
    [InlineData("decrease", "69", 0d, 70d, false)]
    public void Evaluate_CompleteWindow_ComparesUnroundedMedianToTarget(
        string direction, string target, double? before, double middle, bool met)
    {
        var line = new OutcomeMetricLine("selection-milliseconds", direction, target, 3);
        var gates = new[]
        {
            Gate("later", AdoptedAt.AddHours(4), 900),
            Gate("descendant", AdoptedAt.AddHours(2), middle, commit: "descendant"),
            Gate("landed", AdoptedAt.AddHours(1), middle - 10),
            Gate("third", AdoptedAt.AddHours(3), middle + 5),
            Gate("unrelated", AdoptedAt, 5, commit: "unrelated"),
            Gate("unknown", AdoptedAt, 5, commit: null),
            Gate("before-adoption", AdoptedAt.AddTicks(-1), 5)
        };

        var verdict = Assert.Single(CostGoalOutcomeVerdict.Evaluate(Record(line, before), gates, "landed",
            (ancestor, descendant) => ancestor == "landed" && descendant == "descendant", AdoptedAt, AdoptedAt.AddDays(1)));

        var state = met ? CostGoalOutcomeState.Met : CostGoalOutcomeState.Missed;
        Assert.Equal(new CostGoalOutcomeResult(0, line, state, before, middle, before is null ? 0 : 3, 3, null), verdict);
    }

    [Fact]
    public void Evaluate_IncompleteWindow_PendsUntilExactlySevenDays()
    {
        var line = new OutcomeMetricLine("selection-milliseconds", "decrease", "20%", 3);
        var record = Record(line, 100);
        var deadline = AdoptedAt.AddDays(7);
        var gates = new[]
        {
            Gate("one", AdoptedAt, 50),
            Gate("two", deadline.AddTicks(-1), 60),
            Gate("deadline", deadline, 70),
            Gate("after-deadline", deadline.AddTicks(1), 80)
        };

        var pending = Assert.Single(CostGoalOutcomeVerdict.Evaluate(record, gates, "landed",
            (_, _) => false, AdoptedAt, deadline.AddTicks(-1)));
        var expired = Assert.Single(CostGoalOutcomeVerdict.Evaluate(record, gates, "landed",
            (_, _) => false, AdoptedAt, deadline));
        var later = Assert.Single(CostGoalOutcomeVerdict.Evaluate(record, gates, "landed",
            (_, _) => false, AdoptedAt, deadline.AddDays(1)));

        Assert.Equal(new CostGoalOutcomeResult(0, line, CostGoalOutcomeState.Pending, 100, null, 3, 2, null), pending);
        Assert.Equal(new CostGoalOutcomeResult(0, line, CostGoalOutcomeState.InsufficientData, 100, null, 3, 2, "window-unfilled"), expired);
        Assert.Equal(expired, later);
    }

    [Theory]
    [InlineData(null, "baseline-unavailable")]
    [InlineData(0d, "zero-baseline")]
    public void Evaluate_PercentWithUnusableBaseline_ReturnsInsufficientData(double? before, string reason)
    {
        var line = new OutcomeMetricLine("full-gate-rate", "increase", "25%", 2);
        var gates = new[]
        {
            Gate("full", AdoptedAt, 50, reason: "Full"),
            Gate("selected", AdoptedAt.AddTicks(1), 60)
        };

        var verdict = Assert.Single(CostGoalOutcomeVerdict.Evaluate(Record(line, before), gates, "landed",
            (_, _) => false, AdoptedAt, AdoptedAt.AddHours(1)));

        Assert.Equal(new CostGoalOutcomeResult(0, line, CostGoalOutcomeState.InsufficientData,
            before, .5, before is null ? 0 : 3, 2, reason), verdict);
    }

    private static CostGoalOutcomeRecord Record(OutcomeMetricLine line, double? baseline) =>
        new(new(AdoptedAt.AddDays(-1), null, [new(line, baseline, baseline is null ? 0 : 3, null)]), [], [], null, false);
}
