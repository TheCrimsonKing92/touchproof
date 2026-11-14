using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using static AcceptanceSelectedExecutionFixture;

// Parallel-safe: candidate inventory plus uniquely owned gate-result artifacts; no processes.
public sealed class SelectedExecutionObligationGateSeamTests
{
    [Theory]
    [InlineData("B")]
    [InlineData("Candidate.B")]
    public void DroppedImpactClassRunsThroughGateSeamAndSatisfiesObligation(string selector)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var kernel = GateTestObligationIntakeTests.Seed(fixture.Kernel, fixture.Goal, [selector]);
        var goal = kernel.GetGoal(fixture.Goal.Id);
        var options = SoloLandingGateExecutionOptions.Create(goal, kernel.HumanInputRequests);
        var session = new AcceptanceSelectedExecutionSession(AcceptanceSelectedExecutionMode.Enforce, "candidate", Changes,
            read: (_, _) => new(Mcg.AgentOrchestrator.Core.ReverseDependencyTestSelection.Resolved(["A"]),
                Classes, ["Candidate.Guard"]), obligationSelectors: options.GateTestObligationSelectors);
        var observation = Observe(session);
        // Only classes in executable filters produce result rows; obligation provenance alone is insufficient.
        var executed = session.Transform(Lanes);
        Assert.True(session.Applied);
        var matchers = executed.Select(AcceptanceLaneMembership.CreateClassMatcher).ToArray();
        var rows = Classes.Where(item => matchers.Any(matches => matches(item.FullName)))
            .Select(item => new GroupedGateTrxSamples.Row(item.FullName, item.FullName,
                "Check", item.FullName + ".Check", "Passed")).ToArray();
        // Real TRX definitions supply class identity, including the short-name alias for B.
        var trx = GroupedGateTrxSamples.Write(fixture.Root, "selected-execution.trx", rows);
        var (attempt, _, _) = fixture.WriteAttempt([], additionalPaths: [trx]);
        var result = Assert.Single(GateTestObligationEvaluator.Evaluate(goal, fixture.OrchestratorDirectory,
            fixture.CandidateSha, answeredRequests: kernel.HumanInputRequests));
        Assert.Equal(GateTestObligationEvaluator.Outcome.Satisfied, result.Outcome);
        Assert.NotEqual(GateTestObligationEvaluator.Outcome.SelectorNotFound, result.Outcome);
        Assert.Equal(attempt, result.SourceId);
        Assert.Contains(rows, row => row.ClassName == "Candidate.B" && row.Outcome == "Passed");
        Assert.Equal(new[] { "Candidate.B" }, observation.SelectedExecution!.ObligationClasses);
    }
}
