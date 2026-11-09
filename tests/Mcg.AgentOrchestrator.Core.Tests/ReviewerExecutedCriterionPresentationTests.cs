using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: all briefs and typed obligations live in a private in-memory goal.
public sealed class ReviewerExecutedCriterionPresentationTests
{
    private const string ReviewerCriterion = "Reviewer owns; Reviewer executes. Inspect the diff.";
    private const string DeveloperCriterion = "Developer owns; Reviewer executes. Report all touched classes.";
    private const string AcceptanceCriterion = "Tester owns; Acceptance executes. Run the focused tests.";
    private const string GateHeading = "ACCEPTANCE-GATE-OWNED criteria (the acceptance gate, not the worker, produces this evidence):";

    [Fact]
    public void ReviewerBriefUsesExecutorWithoutChangingTypedGateOwnership()
    {
        string[] criteria = [ReviewerCriterion, DeveloperCriterion, AcceptanceCriterion];
        var scenario = CreateScenario(criteria);
        var obligationsBefore = scenario.Goal.CriterionEvidenceObligations.ToArray();
        Assert.Equal(3, obligationsBefore.Length);
        Assert.All(obligationsBefore, item => Assert.Equal(CriterionEvidenceOwner.Acceptance, item.Owner));

        var brief = scenario.Kernel.BuildTaskBrief(scenario.Goal.Id, scenario.Reviewer.Id).Content;

        Assert.Equal([$"- {AcceptanceCriterion}"], GateLines(brief));
        var criteriaStart = brief.IndexOf("Acceptance criteria:", StringComparison.Ordinal);
        var gateStart = brief.IndexOf(GateHeading, StringComparison.Ordinal);
        Assert.True(criteriaStart >= 0 && gateStart > criteriaStart, "The brief must retain its acceptance criteria before gate ownership.");
        var listedCriteria = brief[criteriaStart..gateStart];
        foreach (var criterion in criteria) Assert.Contains($"- {criterion}", listedCriteria);
        Assert.Equal(obligationsBefore, scenario.Goal.CriterionEvidenceObligations);
        Assert.All(scenario.Goal.CriterionEvidenceObligations,
            item => Assert.Equal(CriterionEvidenceOwner.Acceptance, item.Owner));

        // Other roles retain the existing presentation even for Reviewer-executed criteria.
        var developerBrief = scenario.Kernel.BuildTaskBrief(scenario.Goal.Id, scenario.Developer.Id).Content;
        Assert.Equal(criteria.Select(criterion => $"- {criterion}"), GateLines(developerBrief));
    }

    [Theory]
    [InlineData("Inspect the diff. Reviewer owns; Reviewer executes. TEST-VERIFIABLE.")]
    [InlineData("Inspect the diff. Developer owns; Reviewer executes. TEST-VERIFIABLE.")]
    [InlineData("developer owns; rEvIeWeR executes. Inspect the diff.")]
    public void TrailingAndCaseInsensitiveReviewerDeclarationsUseReviewerEvidence(string criterion)
    {
        var scenario = CreateScenario([criterion, AcceptanceCriterion]);

        var brief = scenario.Kernel.BuildTaskBrief(scenario.Goal.Id, scenario.Reviewer.Id).Content;

        Assert.Equal([$"- {AcceptanceCriterion}"], GateLines(brief));
        Assert.Contains(criterion, brief);
    }

    [Theory]
    [InlineData("Run the focused tests.")]
    [InlineData("Developer owns; Acceptance executes. Run the focused tests.")]
    [InlineData("Inspect the diff. Tester owns; Acceptance executes. TEST-VERIFIABLE.")]
    [InlineData("Reviewer owns; Reviewer executes. Tester owns; Acceptance executes. Inspect the diff.")]
    [InlineData("Reviewer owns; Reviewer executes. Developer owns; Reviewer executes. Inspect the diff.")]
    [InlineData("Unknown owns; Reviewer executes. Inspect the diff.")]
    [InlineData("Reviewer owns; Unknown executes. Inspect the diff.")]
    public void MissingAmbiguousAndOtherExecutorsKeepGatePresentation(string criterion)
    {
        var scenario = CreateScenario([criterion]);

        var brief = scenario.Kernel.BuildTaskBrief(scenario.Goal.Id, scenario.Reviewer.Id).Content;

        Assert.Equal([$"- {criterion}"], GateLines(brief));
    }

    [Fact]
    public void ExplicitOperatorOwnershipStillExcludesGatePresentation()
    {
        const string operatorCriterion = "A human confirms the deployment.";
        var scenario = CreateScenario([AcceptanceCriterion, operatorCriterion], [operatorCriterion]);

        foreach (var task in new[] { scenario.Reviewer, scenario.Developer })
        {
            var brief = scenario.Kernel.BuildTaskBrief(scenario.Goal.Id, task.Id).Content;
            Assert.Equal([$"- {AcceptanceCriterion}"], GateLines(brief));
            Assert.Contains("OPERATOR-OWNED / post-landing criteria", brief);
            Assert.Contains(operatorCriterion, brief);
        }
    }

    private static string[] GateLines(string brief)
    {
        var start = brief.IndexOf(GateHeading, StringComparison.Ordinal);
        Assert.True(start >= 0, "The brief must contain the acceptance gate ownership section.");
        return brief[(start + GateHeading.Length)..].Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .TakeWhile(line => line.StartsWith("- ", StringComparison.Ordinal))
            .ToArray();
    }

    private static Scenario CreateScenario(string[] criteria, string[]? operatorOwned = null)
    {
        var kernel = new AgentOrchestratorKernel();
        var reviewer = new TaskSpec(TaskId.New(), "Review", AgentRole.Reviewer);
        var developer = new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer);
        var goal = kernel.CreateGoal("Present criterion evidence ownership", [developer, reviewer]);
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec("Use the declared executor for Reviewer presentation",
            criteria, VerificationClass.TestVerifiable, [], [])
        {
            AcceptanceGateOwnedAcceptanceCriteria = criteria,
            OperatorOwnedAcceptanceCriteria = operatorOwned ?? []
        });
        return new Scenario(kernel, goal, developer, reviewer);
    }

    private sealed record Scenario(AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Developer, TaskSpec Reviewer);
}
