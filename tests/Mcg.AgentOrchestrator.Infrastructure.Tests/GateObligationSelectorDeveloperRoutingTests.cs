using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using static GateObligationSelectorPreAdmissionTests;

// Parallel-safe: the kernel and acceptance evidence directory belong to each fixture.
public sealed class GateObligationSelectorDeveloperRoutingTests
{
    [Theory]
    [InlineData("Unmatched", "A.OtherTests, Z.OtherTests")]
    [InlineData("Ambiguous", "A.PlannedTestsA, Z.PlannedTestsB")]
    public void UnresolvedSelector_HoldsAndRecordsDeveloperFeedback(string kind, string considered)
    {
        using var fixture = new Case("PlannedTests");
        var inventory = kind == "Ambiguous"
            ? Inventory("Z.PlannedTestsB", "A.PlannedTestsA", "OtherTests")
            : Inventory("Z.OtherTests", "A.OtherTests");

        var result = fixture.Apply(inventory);

        Assert.True(result.Hold);
        Assert.Single(result.HoldLines);
        Assert.Empty(fixture.Kernel.HumanInputRequests);
        var obligationId = Assert.Single(fixture.Goal.GateTestObligations).Id;
        var feedback = Assert.Single(fixture.Developer.CriterionRetryFeedback);
        Assert.Contains($"obligation={obligationId} selector=PlannedTests candidate={Candidate} kind={kind}.", feedback);
        Assert.Contains($"Candidate classes considered: {considered}.", feedback);
    }

    [Fact(Timeout = 30_000)]
    [Trait("Category", "CrossTick")]
    public void RepeatedEvaluations_CountEachCandidateOnceEvenAfterKindChanges()
    {
        using var fixture = new Case("PlannedTests");
        var unmatched = Inventory("A.OtherTests");
        var ambiguous = Inventory("A.PlannedTestsOne", "B.PlannedTestsTwo");
        Assert.True(fixture.Apply(unmatched).Hold);
        Assert.True(fixture.Apply(unmatched).Hold);
        Assert.True(fixture.Apply(ambiguous).Hold);

        var first = Assert.Single(fixture.Developer.CriterionRetryFeedback);
        Assert.Contains($"candidate={Candidate} kind=Unmatched", first);
        Assert.Equal(1, fixture.Developer.CriterionRetryCount);
        Assert.Equal(1, fixture.Goal.AutomaticAcceptanceRetryCount);

        Assert.True(fixture.Apply(ambiguous, candidateRevision: NextCandidate).Hold);
        Assert.True(fixture.Apply(ambiguous).Hold);

        Assert.Collection(fixture.Developer.CriterionRetryFeedback,
            feedback => Assert.Equal(first, feedback),
            feedback => Assert.Contains($"candidate={NextCandidate} kind=Ambiguous", feedback));
        Assert.Equal(2, fixture.Developer.CriterionRetryCount);
        Assert.Equal(2, fixture.Goal.AutomaticAcceptanceRetryCount);
    }

    [Fact]
    public void ResolvedAndUniqueSelectors_DoNotRouteAlongsideUnmatchedSelector()
    {
        using var fixture = new Case("ExactTests", "PlannedTests", "MissingTests");
        var inventory = Inventory("Fixture.ExactTests", "Fixture.PlannedTestsLauncher");
        var lines = new List<string>();

        Assert.Contains("MissingTests", Assert.Single(fixture.Apply(inventory, lines.Add).HoldLines));
        Assert.Contains("MissingTests", Assert.Single(fixture.Apply(inventory, lines.Add).HoldLines));

        var request = Assert.Single(fixture.Kernel.HumanInputRequests);
        Assert.Equal("PlannedTests becomes PlannedTestsLauncher", request.Answer);
        Assert.Single(lines, line => line.StartsWith("GATE_TEST_OBLIGATION_PRE_ADMISSION_REBOUND ", StringComparison.Ordinal));
        Assert.Contains("selector=MissingTests", Assert.Single(fixture.Developer.CriterionRetryFeedback));
        Assert.Equal(1, fixture.Developer.CriterionRetryCount);
        Assert.Equal(1, fixture.Goal.AutomaticAcceptanceRetryCount);
    }

    [Fact]
    public void DistinctObligationsAndOrdinalSelectors_EachRecordOnce()
    {
        using var fixture = new Case("PlannedTests", "plannedTests");
        var snapshot = fixture.Kernel.ExportSnapshot();
        var kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select(goal => goal with
            {
                GateTestObligations = [..goal.GateTestObligations!,
                    new("second-obligation", fixture.Goal.AuthoritativeBrief.Version,
                        ["PlannedTests"], null, "Second gate receipt", DateTimeOffset.UnixEpoch)]
            }).ToArray()
        });
        var goal = kernel.GetGoal(fixture.Goal.Id);
        var inventory = Inventory("Fixture.OtherTests");
        for (var tick = 0; tick < 2; tick++)
        {
            var result = GateObligationSelectorPreAdmissionClassifier.Apply(kernel, goal, null,
                Candidate, () => inventory, _ => { });
            Assert.Equal(3, result.HoldLines.Count);
        }

        var task = Assert.Single(goal.Tasks);
        var feedback = task.CriterionRetryFeedback;
        Assert.Equal(3, feedback.Count);
        Assert.Single(feedback, line => line.Contains("obligation=obligation-gate-test selector=PlannedTests ", StringComparison.Ordinal));
        Assert.Single(feedback, line => line.Contains("obligation=obligation-gate-test selector=plannedTests ", StringComparison.Ordinal));
        Assert.Single(feedback, line => line.Contains("obligation=second-obligation selector=PlannedTests ", StringComparison.Ordinal));
        Assert.Equal(3, task.CriterionRetryCount);
        Assert.Equal(3, goal.AutomaticAcceptanceRetryCount);
    }
}
