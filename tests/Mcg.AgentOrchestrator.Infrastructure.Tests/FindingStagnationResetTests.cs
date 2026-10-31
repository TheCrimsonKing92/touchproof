using Mcg.AgentOrchestrator.Core;
using static FindingStagnationScenario;

public sealed class FindingStagnationResetTests
{
    [Xunit.Theory]
    [Xunit.InlineData("criterion")]
    [Xunit.InlineData("ruling")]
    public void Operator_action_releases_wait_and_starts_a_fresh_period(string action)
    {
        var scenario = new FindingStagnationScenario();
        var decision = scenario.BlockSecondRound();
        scenario.AdvanceClock();
        if (action == "criterion")
            scenario.Kernel.RecordGoalRefinement(scenario.Goal.Id, Spec("Explain only the recorded cause evidence"));
        else
            scenario.Kernel.SubmitHumanInput(decision.Id, "Ruling: proceed with the available evidence and new guidance.");
        var before = scenario.Retries;

        scenario.Advance();

        scenario.AssertRetried(before);
        Assert.True(Assert.Single(scenario.Decisions).IsCompleted);
        scenario.CompleteDeveloper(Blocker);
        scenario.Raise(SecondDescription);
        before = scenario.Retries;
        scenario.Advance();
        scenario.AssertRetried(before);
        scenario.CompleteDeveloper(Blocker);
        scenario.Raise(SecondDescription);
        before = scenario.Retries;
        scenario.Advance();
        scenario.AssertHeld(before);
        Assert.Equal(2, scenario.Decisions.Length);
        Assert.Contains("rounds=2", Assert.Single(scenario.Decisions.Where(request => !request.IsCompleted)).Question);
    }

    [Xunit.Fact]
    public void Main_receipt_and_feedback_churn_does_not_reset_the_count()
    {
        var scenario = new FindingStagnationScenario();
        scenario.Raise();
        var first = Assert.IsType<TaskVerificationRecord>(scenario.Reviewer.LastVerification);
        var before = scenario.Retries;
        scenario.Advance();
        scenario.AssertRetried(before);
        scenario.IntegrateMain();
        scenario.CompleteDeveloper(Blocker);
        scenario.Raise(SecondDescription);
        var second = scenario.Reviewer.LastVerification!;
        Assert.NotEqual(first.ReviewedCommit, second.ReviewedCommit);
        Assert.NotEqual(Assert.Single(first.FindingEvidenceReceipts!).ReceiptId,
            Assert.Single(second.FindingEvidenceReceipts!).ReceiptId);
        const string feedback = "Retry feedback reworded; receipt identities changed.";
        scenario.Kernel.RecordCriterionRetryFeedback(scenario.Goal.Id, scenario.Developer.Id, [feedback]);
        Assert.Contains(feedback, scenario.Developer.CriterionRetryFeedback);
        before = scenario.Retries;
        scenario.Advance();
        scenario.AssertHeld(before);
        Assert.Contains("rounds=2", Assert.Single(scenario.Decisions).Question);
        scenario.AdvanceClock();
        scenario.Kernel.RecordGoalRefinement(scenario.Goal.Id, Spec("Explain the historical cause"));
        scenario.Advance();
        scenario.AssertHeld(before);
        Assert.Single(scenario.Decisions);
    }
}
