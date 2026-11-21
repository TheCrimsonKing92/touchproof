using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: comparison inputs are written under each test's own directory.
public sealed class DecisionComparisonTests
{
    private static readonly DecisionSnapshotContext Context =
        new(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero), "Auto", 0, "build-a", new string('a', 64));

    [Fact]
    public void IdenticalDecisionsPass_AndOnlyExactGateExemptionApplies()
    {
        using var directory = new DecisionSnapshotTestDirectory();
        var before = Evaluated("goal-a", eligible: true, "task-1");
        var after = Evaluated("goal-a", eligible: false, "task-1");
        var adopted = Write(directory, "adopted", Context, before);
        var identical = Write(directory, "identical", Context with { BuildIdentity = "build-b" }, before);
        var candidate = Write(directory, "candidate", Context, after);
        var same = DecisionSnapshotComparer.Compare(adopted, identical, []);
        Assert.True(same.Passed);
        Assert.Empty(same.Differences);

        var regression = DecisionSnapshotComparer.Compare(adopted, candidate,
            [new("goal-b", DecisionSnapshotComparer.GateEligibility), new("goal-a", DecisionSnapshotComparer.DispatchReadiness)]);
        Assert.False(regression.Passed);
        var difference = Assert.Single(regression.Differences);
        Assert.Equal("goal-a", difference.GoalId);
        Assert.Equal(DecisionSnapshotComparer.GateEligibility, difference.DecisionKind);
        Assert.False(difference.Exempted);
        Assert.Equal(DecisionSnapshotDecision.CandidateBuilt, difference.Adopted!.Decision!.GateOutcome);
        Assert.Equal(ConductorDriver.CandidateDeclineLifecycleNotVerified, difference.Candidate!.Decision!.DeclineReason);

        var accepted = DecisionSnapshotComparer.Compare(adopted, candidate, [new("goal-a", DecisionSnapshotComparer.GateEligibility)]);
        Assert.True(accepted.Passed);
        Assert.True(Assert.Single(accepted.Differences).Exempted);
    }

    [Fact]
    public void ReadinessFailsOnlyWhenTheWholeGoalBecomesBlocked()
    {
        using var directory = new DecisionSnapshotTestDirectory();
        var adopted = Write(directory, "before", Context, Evaluated("goal-a", false, "task-1", "task-2"));
        var partial = Write(directory, "partial", Context, Evaluated("goal-a", false, "task-2"));
        var blocked = Write(directory, "blocked", Context, Evaluated("goal-a", false));

        var diagnostic = DecisionSnapshotComparer.Compare(adopted, partial, []);
        Assert.True(diagnostic.Passed);
        var loss = Assert.Single(diagnostic.Differences);
        Assert.False(loss.IsRegression);
        Assert.Equal(new[] { "task-1", "task-2" }, loss.Adopted!.Decision!.DispatchReadyTaskIds);
        Assert.Equal(new[] { "task-2" }, loss.Candidate!.Decision!.DispatchReadyTaskIds);

        var regression = DecisionSnapshotComparer.Compare(adopted, blocked, []);
        Assert.False(regression.Passed);
        var difference = Assert.Single(regression.Differences);
        Assert.Equal(DecisionSnapshotComparer.DispatchReadiness, difference.DecisionKind);
        Assert.True(difference.IsRegression);
        Assert.Empty(difference.Candidate!.Decision!.DispatchReadyTaskIds);
    }

    [Fact]
    public void MissingOpportunityAndCandidateOnlyExceptionAreReportedSeparately()
    {
        using var directory = new DecisionSnapshotTestDirectory();
        var adopted = Write(directory, "before", Context,
            Evaluated("goal-active", true, "task-1"), Evaluated("goal-inactive", false));
        var candidate = Write(directory, "after", Context,
            new("goal-exception", Exception: new("FixtureException", "stored record rejected")),
            Evaluated("goal-new", false));

        var result = DecisionSnapshotComparer.Compare(adopted, candidate,
            [new("goal-exception", DecisionSnapshotComparer.GateEligibility)]);
        Assert.False(result.Passed);
        Assert.Null(result.InputError);
        Assert.Equal(3, result.Differences.Count);
        Assert.Contains(result.Differences, d => d.GoalId == "goal-active" &&
            d.DecisionKind == DecisionSnapshotComparer.GateEligibility && d.Candidate is null);
        Assert.Contains(result.Differences, d => d.GoalId == "goal-active" &&
            d.DecisionKind == DecisionSnapshotComparer.DispatchReadiness && d.Candidate is null);
        Assert.Contains(result.Differences, d => d.GoalId == "goal-exception" &&
            d.DecisionKind == DecisionSnapshotComparer.Exception && !d.Exempted && d.Adopted is null);

        var accepted = DecisionSnapshotComparer.Compare(adopted, candidate,
            [new("goal-active", DecisionSnapshotComparer.GateEligibility),
             new("goal-active", DecisionSnapshotComparer.DispatchReadiness),
             new("goal-exception", DecisionSnapshotComparer.Exception)]);
        Assert.True(accepted.Passed);
        Assert.All(accepted.Differences, d => Assert.True(d.Exempted));
    }

    [Fact]
    public void InvalidInputsCannotBeExempted()
    {
        using var directory = new DecisionSnapshotTestDirectory();
        var goal = Evaluated("goal-a", false);
        var before = Write(directory, "before", Context, goal);
        var invalidAdopted = Write(directory, "invalid-adopted", Context,
            new DecisionSnapshotGoal("goal-a", Exception: new("Bad", "parse failed")));
        var duplicate = Write(directory, "duplicate", Context, goal, goal);
        var otherTime = Write(directory, "other-time", Context with { AsOf = Context.AsOf.AddMinutes(1) }, goal);
        var hashless = Write(directory, "hashless", Context with { StateCopyHash = null }, goal);
        var hashlessMissing = Write(directory, "missing", Context with { StateCopyHash = null });
        var unsupported = Path.Combine(directory.Path, "unsupported.json");
        File.WriteAllText(unsupported, JsonSerializer.Serialize(new DecisionSnapshot(2, Context, [goal]), DecisionSnapshot.JsonOptions));

        AssertInputError(invalidAdopted, before, [new("goal-a", DecisionSnapshotComparer.Exception)], "Adopted goal");
        AssertInputError(before, duplicate, [], "duplicate goal ID");
        AssertInputError(before, otherTime, [], "incompatible evaluation");
        AssertInputError(hashless, hashlessMissing, [], "state-copy identities");
        AssertInputError(before, unsupported, [], "unsupported schema");
        AssertInputError(before, before, [new("*", DecisionSnapshotComparer.GateEligibility)], "exact goalId");
        AssertInputError(before, before, [new("goal-a", "unknown")], "supported decisionKind");
        Assert.True(DecisionSnapshotComparer.Compare(hashless, hashless, []).Passed);
    }

    private static void AssertInputError(string adopted, string candidate, DecisionDeclaredChange[] exemptions, string message)
    {
        var result = DecisionSnapshotComparer.Compare(adopted, candidate, exemptions);
        Assert.False(result.Passed);
        Assert.Contains(message, result.InputError!, StringComparison.Ordinal);
        Assert.Empty(result.Differences);
    }

    private static DecisionSnapshotGoal Evaluated(string id, bool eligible, params string[] ready) =>
        new(id, Decision: new(ready,
            eligible ? DecisionSnapshotDecision.CandidateBuilt : DecisionSnapshotDecision.Declined,
            eligible ? null : ConductorDriver.CandidateDeclineLifecycleNotVerified,
            GoalStatus.Active, null, new Dictionary<string, bool> { ["task-1"] = false, ["task-2"] = false }));

    private static string Write(DecisionSnapshotTestDirectory directory, string name, DecisionSnapshotContext context,
        params DecisionSnapshotGoal[] goals)
    {
        var path = Path.Combine(directory.Path, name + ".json");
        var snapshot = new DecisionSnapshot(DecisionSnapshot.CurrentSchemaVersion, context, goals);
        File.WriteAllText(path, JsonSerializer.Serialize(snapshot, DecisionSnapshot.JsonOptions));
        return path;
    }
}
