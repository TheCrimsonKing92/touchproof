using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: each test owns its planner and uses only in-memory inputs.
public sealed class LandingInteractionProbePlannerTests
{
    [Fact]
    public void OverlapPlansExactOrdinalClassUnionInCandidateOrder()
    {
        var planner = new LandingInteractionProbePlanner();
        var landing = new LandingInteractionProbeLanding("L1", ["src/A.cs"], ["ZTests", "ATests", "aTests", "ZTests"]);

        var outcomes = planner.Plan(landing,
        [
            Candidate("C2", GoalStatus.Verifying, ["src/A.cs", "src/B.cs"], ["MTests", "ATests"]),
            Candidate("disjoint", GoalStatus.Verified, ["src/C.cs"], ["OtherTests"]),
            Candidate("C1", GoalStatus.Verified, ["src/A.cs"], ["Project::BTests"])
        ], false);

        Assert.Collection(outcomes,
            outcome => AssertPlanned(outcome, "C2", ["ATests", "MTests", "ZTests", "aTests"]),
            outcome => AssertPlanned(outcome, "C1", ["ATests", "Project::BTests", "ZTests", "aTests"]));
    }

    [Theory]
    [InlineData("src/a.cs")]
    [InlineData("src\\A.cs")]
    [InlineData(" src/A.cs")]
    public void PathsAreComparedWithoutNormalization(string candidatePath)
    {
        var planner = new LandingInteractionProbePlanner();
        Assert.Empty(planner.Plan(Landing(), [Candidate("C1", GoalStatus.Verified, [candidatePath])], false));
    }

    [Fact]
    public void PairIsPlannedOnceButDifferentLandingAndPlannerCanPlanAgain()
    {
        var planner = new LandingInteractionProbePlanner();
        var landing = Landing();
        LandingInteractionProbeCandidate[] candidates = [Candidate("C1")];

        AssertPlanned(Assert.Single(planner.Plan(landing, candidates, false)), "C1", ["LandedTests"]);
        Assert.Empty(planner.Plan(landing, candidates, false));
        AssertPlanned(Assert.Single(planner.Plan(landing with { LandingId = "L2" }, candidates, false)), "C1", ["LandedTests"]);
        AssertPlanned(Assert.Single(new LandingInteractionProbePlanner().Plan(landing, candidates, false)), "C1", ["LandedTests"]);
    }

    [Fact]
    public void PairIdentityIsOrdinalForBothLandingAndCandidate()
    {
        var planner = new LandingInteractionProbePlanner();
        var landing = Landing();
        Assert.Collection(planner.Plan(landing, [Candidate("C1"), Candidate("c1")], false),
            outcome => AssertPlanned(outcome, "C1", ["LandedTests"]),
            outcome => AssertPlanned(outcome, "c1", ["LandedTests"]));
        AssertPlanned(Assert.Single(planner.Plan(landing with { LandingId = "l1" }, [Candidate("C1")], false)),
            "C1", ["LandedTests"]);
    }

    [Fact]
    public void OccupiedGateDefersEveryOverlapWithoutRecordingPairs()
    {
        var planner = new LandingInteractionProbePlanner();
        LandingInteractionProbeCandidate[] candidates =
        [
            Candidate("C2", GoalStatus.Verifying),
            Candidate("C1"),
            Candidate("disjoint", GoalStatus.Verified, ["src/B.cs"])
        ];

        Assert.Collection(planner.Plan(Landing(), candidates, true),
            outcome => AssertDeferred(outcome, "C2"),
            outcome => AssertDeferred(outcome, "C1"));
        Assert.Collection(planner.Plan(Landing(), candidates, false),
            outcome => AssertPlanned(outcome, "C2", ["LandedTests"]),
            outcome => AssertPlanned(outcome, "C1", ["LandedTests"]));
        Assert.Empty(planner.Plan(Landing(), candidates, false));
    }

    [Fact]
    public void OccupiedGateDefersPreviouslyPlannedPairWithoutForgettingIt()
    {
        var planner = new LandingInteractionProbePlanner();
        LandingInteractionProbeCandidate[] candidates = [Candidate("C1")];

        AssertPlanned(Assert.Single(planner.Plan(Landing(), candidates, false)), "C1", ["LandedTests"]);
        AssertDeferred(Assert.Single(planner.Plan(Landing(), candidates, true)), "C1");
        Assert.Empty(planner.Plan(Landing(), candidates, false));
    }

    [Theory]
    [InlineData(GoalStatus.Active, false)]
    [InlineData(GoalStatus.Active, true)]
    [InlineData(GoalStatus.AcceptanceFailed, false)]
    [InlineData(GoalStatus.AcceptanceFailed, true)]
    [InlineData(GoalStatus.Completed, false)]
    [InlineData(GoalStatus.Completed, true)]
    public void IneligibleCandidateProducesNoOutcome(GoalStatus status, bool occupied)
    {
        var planner = new LandingInteractionProbePlanner();
        Assert.Empty(planner.Plan(Landing(), [Candidate("C1", status)], occupied));
        AssertPlanned(Assert.Single(planner.Plan(Landing(), [Candidate("C1")], false)), "C1", ["LandedTests"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyChangedFilesProduceNoOutcome(bool occupied)
    {
        var planner = new LandingInteractionProbePlanner();
        Assert.Empty(planner.Plan(Landing(), [Candidate("C1", GoalStatus.Verified, [])], occupied));
        Assert.Empty(planner.Plan(Landing() with { ChangedFiles = [] }, [Candidate("C1")], occupied));
        AssertPlanned(Assert.Single(planner.Plan(Landing(), [Candidate("C1")], false)), "C1", ["LandedTests"]);
    }

    [Fact]
    public void NoLandedClassesPlansOnlyCandidateClassesIncludingEmptyUnion()
    {
        var planner = new LandingInteractionProbePlanner();
        var landing = Landing() with { TestClasses = [] };
        LandingInteractionProbeCandidate[] candidates =
        [
            Candidate("C1", deferredClasses: ["BTests", "ATests", "BTests"]),
            Candidate("C2")
        ];

        Assert.Collection(planner.Plan(landing, candidates, false),
            outcome => AssertPlanned(outcome, "C1", ["ATests", "BTests"]),
            outcome => AssertPlanned(outcome, "C2", []));
        Assert.Empty(planner.Plan(landing, candidates, false));
    }

    [Fact]
    public async Task ConcurrentCallsPlanPairAtMostOnce()
    {
        var planner = new LandingInteractionProbePlanner();
        var results = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => planner.Plan(Landing(), [Candidate("C1")], false))));

        AssertPlanned(Assert.Single(results.SelectMany(result => result)), "C1", ["LandedTests"]);
    }

    [Fact]
    public void InvalidCandidateFailsBeforeAnyPairIsRecorded()
    {
        var planner = new LandingInteractionProbePlanner();
        Assert.Throws<ArgumentException>(() => planner.Plan(Landing(), [Candidate("C1"), Candidate(" ")], false));
        AssertPlanned(Assert.Single(planner.Plan(Landing(), [Candidate("C1")], false)), "C1", ["LandedTests"]);
    }

    private static LandingInteractionProbeLanding Landing() => new("L1", ["src/A.cs"], ["LandedTests"]);

    private static LandingInteractionProbeCandidate Candidate(
        string id,
        GoalStatus status = GoalStatus.Verified,
        IReadOnlyList<string>? files = null,
        IReadOnlyList<string>? deferredClasses = null) =>
        new(id, status, files ?? ["src/A.cs"], deferredClasses ?? []);

    private static void AssertPlanned(LandingInteractionProbeOutcome outcome, string id, string[] classes)
    {
        Assert.Equal(id, outcome.CandidateGoalId);
        Assert.Equal(LandingInteractionProbeOutcomeKind.Planned, outcome.Kind);
        Assert.Equal(classes, outcome.TestClasses);
    }

    private static void AssertDeferred(LandingInteractionProbeOutcome outcome, string id)
    {
        Assert.Equal(id, outcome.CandidateGoalId);
        Assert.Equal(LandingInteractionProbeOutcomeKind.Deferred, outcome.Kind);
        Assert.Empty(outcome.TestClasses);
    }
}
