using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static AcceptanceSelectedExecutionFixture;
using static AcceptanceSelectedExecutionObligationTests;

// Parallel-safe: every test owns its in-memory kernel and optional evidence directory.
public sealed class GateTestObligationEffectiveSelectorsTests
{
    [Theory]
    [InlineData("old")]
    [InlineData("foreign")]
    [InlineData("waived")]
    public void ExcludedObligationCannotRetainClassOrChangeBytes(string exclusion)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Current obligation eligibility");
        GateTestObligationEvaluatorWaiverTests.RecordRefinement(kernel, goal, new string('a', 40));
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        if (exclusion == "old") kernel.ReviseGoalBrief(goal.Id, "Second brief version");
        var eligibleKernel = GateTestObligationIntakeTests.Seed(kernel, goal, ["B"]);
        var eligibleGoal = eligibleKernel.GetGoal(goal.Id);
        var eligible = GateTestObligationEffectiveSelectors.ForMembers([eligibleGoal], [eligibleGoal.Id]);
        Assert.Contains("Candidate.B", Observe(WithObligations(eligible)).SelectedExecution!.Lanes
            .SelectMany(lane => lane.SelectedClasses));
        var identity = new PlannerEvidenceIdentity(goal.Id.Value, "planner", goal.AuthoritativeBrief.Version, 1, "gate");
        var obligation = new GateTestObligation(identity.StableId, goal.AuthoritativeBrief.Version,
            ["B", "FullyQualifiedName~Invalid"], "planner", "gate evidence", DateTimeOffset.UnixEpoch, identity);
        if (exclusion == "old") obligation = obligation with { BriefVersion = goal.AuthoritativeBrief.Version - 1, PlannerIdentity = null };
        if (exclusion == "foreign") obligation = obligation with { RepositoryKey = "another-repository" };
        var snapshot = kernel.ExportSnapshot();
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select(item => item with { GateTestObligations = [obligation] }).ToArray()
        });
        goal = kernel.GetGoal(goal.Id);
        if (exclusion == "waived") GateTestObligationEvaluatorWaiverTests.Waive(kernel, goal, 1, "Operator waiver");
        var selectors = GateTestObligationEffectiveSelectors.ForMembers([goal], [goal.Id], kernel.HumanInputRequests);
        Assert.Empty(selectors);
        var actual = Observe(WithObligations(selectors));
        var baseline = Observe(WithObligations(null));
        actual = actual with { SelectedExecution = actual.SelectedExecution! with { SelectionMilliseconds = 0 } };
        baseline = baseline with { SelectedExecution = baseline.SelectedExecution! with { SelectionMilliseconds = 0 } };
        Assert.Equal(baseline.ToJson(), actual.ToJson());
        Assert.DoesNotContain("Candidate.B", actual.SelectedExecution!.Lanes.SelectMany(lane => lane.SelectedClasses));
    }

    [Fact]
    public void AnsweredSelectorEditsAgreeWithEvaluatorAndRetainReplacementOnly()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var kernel = GateTestObligationIntakeTests.Seed(fixture.Kernel, fixture.Goal, ["OldTests", "X"]);
        var goal = kernel.GetGoal(fixture.Goal.Id);
        var obligation = Assert.Single(goal.GateTestObligations);
        kernel.RecordAnsweredOperatorHumanInput(goal.Id, "replace selector",
            AnsweredSelectorRebinding.FormatSelectorEdit("OldTests", "B", [obligation.Id]));
        kernel.RecordAnsweredOperatorHumanInput(goal.Id, "drop selector",
            AnsweredSelectorRebinding.FormatSelectorEdit("X", null, [obligation.Id]));
        var selectors = GateTestObligationEffectiveSelectors.ForMembers([goal], [goal.Id], kernel.HumanInputRequests);
        Assert.Equal(new[] { "B" }, selectors);
        var evaluated = Assert.Single(GateTestObligationEvaluator.Evaluate(goal, fixture.OrchestratorDirectory,
            fixture.CandidateSha, answeredRequests: kernel.HumanInputRequests));
        Assert.Equal(selectors, evaluated.Obligation.Selectors);
        Assert.Equal(new[] { "Candidate.B" }, Observe(WithObligations(selectors)).SelectedExecution!.ObligationClasses);
    }

    [Fact]
    public void MissingMemberOrInvalidatedEnumerationForcesFullGate()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("gate member");
        IEnumerable<Goal> Invalidated()
        {
            yield return goal;
            throw new InvalidOperationException("member snapshot invalidated");
        }
        foreach (var selectors in new[]
        {
            GateTestObligationEffectiveSelectors.ForMembers([goal], [GoalId.New()]),
            GateTestObligationEffectiveSelectors.ForMembers(Invalidated(), [goal.Id])
        })
        {
            Assert.StartsWith("Obligation snapshot unavailable: ", Assert.Single(selectors));
            var record = Observe(WithObligations(selectors)).SelectedExecution!;
            Assert.Equal("full-gate", record.Decision);
            Assert.StartsWith("UnresolvedObligationSelector: ", record.FullGateReason);
        }
    }
}
