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

    [Theory]
    [InlineData(null)]
    [InlineData(0d)]
    public void Evaluate_PercentWithUnusableBaselineAndNoGates_PendsUntilDeadline(double? before)
    {
        var line = new OutcomeMetricLine("full-gate-rate", "increase", "25%", 3);
        var record = Record(line, before);
        var pending = Assert.Single(CostGoalOutcomeVerdict.Evaluate(record, [], "landed",
            (_, _) => false, AdoptedAt, AdoptedAt.AddDays(1)));
        var expired = Assert.Single(CostGoalOutcomeVerdict.Evaluate(record, [], "landed",
            (_, _) => false, AdoptedAt, AdoptedAt.AddDays(7)));

        Assert.Equal(new CostGoalOutcomeResult(0, line, CostGoalOutcomeState.Pending,
            before, null, before is null ? 0 : 3, 0, null), pending);
        Assert.Equal(pending with { State = CostGoalOutcomeState.InsufficientData, Reason = "window-unfilled" }, expired);
    }

    [Theory]
    [InlineData("must-not-increase", 2, true)]
    [InlineData("must-not-increase", 1, false)]
    [InlineData("must-not-decrease", 0, true)]
    [InlineData("must-not-decrease", 2, false)]
    public void Evaluate_GuardUsesPrimaryGateWindow_OverridesMetTarget(
        string rule, int afterFullGates, bool regressed)
    {
        var line = new OutcomeMetricLine("selection-milliseconds", "decrease", "20%", 3, new("full-gate-rate", rule));
        var filedAt = AdoptedAt.AddDays(-1);
        var before = CostGoalOutcomeMetric.BaselinesAt([line],
        [
            Gate("before-one", filedAt.AddHours(-4), 100, reason: "Full"),
            Gate("before-two", filedAt.AddHours(-3), 100),
            Gate("before-three", filedAt.AddHours(-2), 100),
            Gate("before-no-selection", filedAt.AddHours(-1), 0, 0, "No changed source")
        ], filedAt);
        var record = new CostGoalOutcomeRecord(new(filedAt, null, before), [], [], null, false);
        var after = new[]
        {
            Gate("after-no-selection", AdoptedAt, 0, 0, "No changed source"),
            Gate("after-one", AdoptedAt.AddHours(1), 50, reason: afterFullGates >= 1 ? "Full" : null),
            Gate("after-two", AdoptedAt.AddHours(2), 50, reason: afterFullGates >= 2 ? "Full" : null),
            Gate("after-three", AdoptedAt.AddHours(3), 50),
            Gate("after-outside-window", AdoptedAt.AddHours(4), 500, reason: "Full")
        };

        var result = Assert.Single(CostGoalOutcomeVerdict.Evaluate(record, after, "landed",
            (_, _) => false, AdoptedAt, AdoptedAt.AddDays(1)));

        Assert.Equal(regressed ? CostGoalOutcomeState.Missed : CostGoalOutcomeState.Met, result.State);
        Assert.Equal(100, result.Before);
        Assert.Equal(50, result.After);
        Assert.Equal(new OutcomeGuardResult(new(1d / 3, 3), new(afterFullGates / 3d, 3)), result.Guard);
        Assert.Equal(regressed ? "guard-regressed" : null, result.Reason);
    }

    [Fact]
    public void Evaluate_MatchingReplayReports_AggregatesOnlyResolvedSelectionsAndCheckedFailures()
    {
        var results = EvaluateReplay(ReplayAfter);

        Assert.Collection(results,
            selected =>
            {
                Assert.Equal(CostGoalOutcomeState.Met, selected.State);
                Assert.Equal(6, selected.Before);
                Assert.Equal(2, selected.After);
                Assert.Equal(4, selected.BeforeSampleSize);
                Assert.Equal(4, selected.AfterSampleSize);
                Assert.Equal(new OutcomeGuardResult(new(2, 4), new(1, 4)), selected.Guard);
                Assert.Equal(5, selected.ReplayAttemptSetSize);
            },
            missed =>
            {
                Assert.Equal(CostGoalOutcomeState.Met, missed.State);
                Assert.Equal(2, missed.Before);
                Assert.Equal(1, missed.After);
                Assert.Equal(4, missed.BeforeSampleSize);
                Assert.Equal(4, missed.AfterSampleSize);
                Assert.Equal(5, missed.ReplayAttemptSetSize);
            });
    }

    [Fact]
    public void Evaluate_ReplayReportsWithSameCountButDifferentIds_ReturnsInsufficientData()
    {
        var after = ReplayAfter.Replace("\"attemptId\":\"a\"", "\"attemptId\":\"another\"", StringComparison.Ordinal);

        Assert.All(EvaluateReplay(after), result =>
        {
            Assert.Equal(CostGoalOutcomeState.InsufficientData, result.State);
            Assert.Equal("replay-set-mismatch", result.Reason);
            Assert.Null(result.Before);
            Assert.Null(result.After);
        });
    }

    [Fact]
    public void Evaluate_ReplayGuardWithMoreMisses_OverridesMetSelectionTarget()
    {
        var after = ReplayAfter.Replace("{\"isMiss\":false}", "{\"isMiss\":true},{\"isMiss\":true}", StringComparison.Ordinal);
        var selected = EvaluateReplay(after)[0];

        Assert.Equal(2, selected.After);
        Assert.Equal(CostGoalOutcomeState.Missed, selected.State);
        Assert.Equal("guard-regressed", selected.Reason);
        Assert.Equal(new OutcomeGuardResult(new(2, 4), new(3, 4)), selected.Guard);
    }

    [Fact]
    public void Evaluate_ReplayWithLostMissCheckCoverage_CannotClaimAnImprovement()
    {
        var after = ReplayAfter.Replace(
            "\"failingClasses\":[{\"isMiss\":true}],\"missCheckComplete\":true",
            "\"failingClasses\":[{\"isMiss\":true}],\"missCheckComplete\":false", StringComparison.Ordinal);

        Assert.All(EvaluateReplay(after), result =>
        {
            Assert.Equal(CostGoalOutcomeState.InsufficientData, result.State);
            Assert.Equal("replay-miss-check-incomplete", result.Reason);
        });
    }

    private static IReadOnlyList<CostGoalOutcomeResult> EvaluateReplay(string after)
    {
        var selected = new OutcomeMetricLine("replay-selected-classes", "decrease", "50%", 3,
            new("replay-missed-failures", "must-not-increase"));
        var missed = new OutcomeMetricLine("replay-missed-failures", "decrease", "1", 3);
        var filedAt = AdoptedAt.AddDays(-1);
        var record = new CostGoalOutcomeRecord(new(filedAt, null, CostGoalOutcomeMetric.BaselinesAt([selected, missed], [], filedAt)),
            [], [], null, false);
        var replay = CostGoalOutcomeReplay.Parse(ReplayBefore, after);
        return CostGoalOutcomeVerdict.Evaluate(record, [], "landed", (_, _) => false, AdoptedAt, AdoptedAt.AddDays(1), replay);
    }

    private const string ReplayBefore = """
        {"attempts":[
          {"attemptId":"a","selectionOutcome":"Resolved","selectedTestClasses":["A","B","C","D","E","F","G","H","I"],"failingClasses":[{"isMiss":true},{"isMiss":false}],"missCheckComplete":true},
          {"attemptId":"b","selectionOutcome":"Resolved","selectedTestClasses":["A","B","C","D","E"],"failingClasses":[{"isMiss":false}],"missCheckComplete":true},
          {"attemptId":"c","selectionOutcome":"Abandoned","selectedTestClasses":[],"failingClasses":[{"isMiss":true},{"isMiss":true}],"missCheckComplete":false},
          {"attemptId":"d","selectionOutcome":"Resolved","selectedTestClasses":["A","B","C","D","E","F","G"],"failingClasses":[{"isMiss":true}],"missCheckComplete":true},
          {"attemptId":"e","selectionOutcome":"Resolved","selectedTestClasses":["A"],"failingClasses":[],"missCheckComplete":true}
        ]}
        """;

    private const string ReplayAfter = """
        {"attempts":[
          {"attemptId":"e","selectionOutcome":"Resolved","selectedTestClasses":["A"],"failingClasses":[],"missCheckComplete":true},
          {"attemptId":"d","selectionOutcome":"Resolved","selectedTestClasses":["A","B","C"],"failingClasses":[{"isMiss":false}],"missCheckComplete":true},
          {"attemptId":"c","selectionOutcome":"Abandoned","selectedTestClasses":[],"failingClasses":[{"isMiss":true}],"missCheckComplete":false},
          {"attemptId":"b","selectionOutcome":"Resolved","selectedTestClasses":["A","B","C","D","E"],"failingClasses":[{"isMiss":true}],"missCheckComplete":true},
          {"attemptId":"a","selectionOutcome":"Resolved","selectedTestClasses":["A"],"failingClasses":[],"missCheckComplete":true}
        ]}
        """;

    private static CostGoalOutcomeRecord Record(OutcomeMetricLine line, double? baseline) =>
        new(new(AdoptedAt.AddDays(-1), null, [new(line, baseline, baseline is null ? 0 : 3, null)]), [], [], null, false);
}
