using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: all evidence and clocks are local values.
public sealed class RecurringUnrelatedTestRuleTests
{
    internal static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-11T18:00:00Z");
    internal const string Identity = "Example.UnreliableTests.DrainCompletes";
    internal const string Key = "UnreliableTests.DrainCompletes";
    internal static ApparatusRedFailingTest Test => new("verifier lane", Identity, null,
        ["tests/UnreliableTests.cs"], false, false, CandidateRerunFailed: true,
        FailureMessage: "drain was incomplete", BaselineAttribution:
            new(Identity, AcceptanceTestFailureOrigin.Introduced, "passed at merge base"));
    internal static ApparatusRedGateReading Reading(params ApparatusRedFailingTest[] tests) =>
        new(["src/CandidateFeature.cs"], tests.Length == 0 ? [Test] : tests, true, ["verifier lane"]);
    internal static RecurringTestFailureOccurrence Occurrence(string goal, DateTimeOffset? at = null) =>
        new(Key, goal, at ?? Now.AddHours(-1), "census", "attempt-" + goal, "verifier lane", "sha-" + goal, "fingerprint");
    internal static IReadOnlyList<RecurringTestFailureOccurrence> History => [Occurrence("other-a"), Occurrence("other-b")];
    internal static RecurringTestPassedGate Pass(int ordinal) => new("pass-" + ordinal, "candidate-" + ordinal,
        "main", Now.AddMinutes(-30 + ordinal));

    [Fact(DisplayName = "Two other recent goals corroborate an unrelated reproduced failure")]
    public void TwoOtherGoalsQualify()
    {
        var verdict = RecurringUnrelatedTestRule.Evaluate("current", Reading(), History, [], _ => null, Now);
        var match = Assert.Single(Assert.IsType<RecurringUnrelatedTestVerdict>(verdict).RecurringTests);
        Assert.Equal(Identity, match.Test.TestIdentity);
        Assert.Equal(["other-a", "other-b"], match.Occurrences.Select(row => row.GoalId));
    }

    [Theory(DisplayName = "Candidate guards and insufficient gate history keep normal attribution")]
    [InlineData("changed")]
    [InlineData("message")]
    [InlineData("one-goal")]
    [InlineData("same-goal")]
    [InlineData("old")]
    [InlineData("future")]
    [InlineData("mixed")]
    [InlineData("source-guard")]
    [InlineData("unresolved")]
    [InlineData("identityless")]
    [InlineData("unknown-paths")]
    public void NegativeControls(string scenario)
    {
        var reading = Reading();
        var history = History;
        switch (scenario)
        {
            case "changed": reading = Reading(Test with { InsideChangedPaths = true }); break;
            case "message": reading = Reading(Test with { FailureMessage = "src/CandidateFeature.cs is wrong" }); break;
            case "one-goal": history = [Occurrence("other-a"), Occurrence("other-a")]; break;
            case "same-goal": history = [Occurrence("current"), Occurrence("other-a")]; break;
            case "old": history = [Occurrence("other-a", Now.AddDays(-7).AddMinutes(-1)), Occurrence("other-b")]; break;
            case "future": history = [Occurrence("other-a", Now.AddMinutes(1)), Occurrence("other-b")]; break;
            case "mixed": reading = Reading(Test, Test with { TestIdentity = "Example.GenuineTests.Broken" }); break;
            case "source-guard": reading = Reading(Test with { ReadsRepositorySources = true }); break;
            case "unresolved": reading = Reading(Test with { ResolvedSourcePaths = [] }); break;
            case "identityless": reading = reading with { EveryFailedCheckHasTestIdentities = false }; break;
            case "unknown-paths": reading = reading with { ChangedPaths = [] }; break;
        }
        Assert.Null(RecurringUnrelatedTestRule.Evaluate("current", reading, history, [], _ => null, Now));
    }

    [Fact(DisplayName = "A corroborated inherited identity may accompany a recurring failure")]
    public void MixedInheritedIsEligible()
    {
        var inherited = Test with { TestIdentity = "Example.InheritedTests.Broken",
            BaselineAttribution = new("Example.InheritedTests.Broken", AcceptanceTestFailureOrigin.Inherited, "failed at merge base") };
        Assert.NotNull(RecurringUnrelatedTestRule.Evaluate("current", Reading(Test, inherited), History, [], _ => null, Now));
        Assert.Null(RecurringUnrelatedTestRule.Evaluate("current", Reading(inherited), History, [], _ => null, Now));
    }

    [Fact(DisplayName = "Closing the fix item clears old recurrence but allows new evidence")]
    public void ClosureWatermarkClearsHistory()
    {
        Assert.Null(RecurringUnrelatedTestRule.Evaluate("current", Reading(), History, [], _ => Now.AddMinutes(-40), Now));
        Assert.NotNull(RecurringUnrelatedTestRule.Evaluate("current", Reading(),
            [.. History, Occurrence("new-a", Now.AddMinutes(-10)), Occurrence("new-b", Now.AddMinutes(-5))],
            [], _ => Now.AddMinutes(-40), Now));
    }

    [Fact(DisplayName = "Only three distinct consecutive later passed gates clear recurrence")]
    public void ThreeConsecutivePassesClear()
    {
        var passes = new[] { Pass(1), Pass(2), Pass(3) };
        Assert.NotNull(RecurringUnrelatedTestRule.Evaluate("current", Reading(), History, passes[..2], _ => null, Now));
        Assert.Null(RecurringUnrelatedTestRule.Evaluate("current", Reading(), History, passes, _ => null, Now));
        Assert.NotNull(RecurringUnrelatedTestRule.Evaluate("current", Reading(), History,
            [Pass(1), Pass(1), Pass(2)], _ => null, Now));
        Assert.NotNull(RecurringUnrelatedTestRule.Evaluate("current", Reading(),
            [.. History, Occurrence("other-a", Now.AddMinutes(-28.5))], passes, _ => null, Now));
        Assert.NotNull(RecurringUnrelatedTestRule.Evaluate("current", Reading(),
            [.. History, Occurrence("other-a", Pass(3).At)], passes, _ => null, Now));
        // A new failure after the reset cannot resurrect pre-reset occurrences.
        Assert.Null(RecurringUnrelatedTestRule.Evaluate("current", Reading(),
            [.. History, Occurrence("current", Now)], passes, _ => null, Now));
    }
}
