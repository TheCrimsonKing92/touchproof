using Mcg.AgentOrchestrator.Core;
using static FindingStagnationScenario;

public sealed class FindingStagnationReplayTests
{
    [Xunit.Theory]
    [Xunit.InlineData(AgentRole.Reviewer)]
    [Xunit.InlineData(AgentRole.Tester)]
    public void Repeated_finding_blocks_third_round_once_across_restart(AgentRole role)
    {
        var scenario = new FindingStagnationScenario(role);
        scenario.Raise();
        var before = scenario.Retries;
        scenario.Advance();
        scenario.AssertRetried(before);
        scenario.IntegrateMain();
        scenario.CompleteDeveloper(Blocker);
        Assert.NotNull(scenario.Developer.LastDispatch!.PreDispatchIntegrationReceipt);
        // Touch the cited file so only the explicit Developer blocker can stop this retry.
        scenario.Raise(SecondDescription, touched: [Finding().Location]);
        var task = role == AgentRole.Reviewer ? scenario.Reviewer : scenario.Tester;
        Assert.Equal("receipt-2", Assert.Single(task.LastVerification!.FindingEvidenceReceipts!).ReceiptId);
        before = scenario.Retries;

        scenario.Advance();

        scenario.AssertHeld(before);
        var decision = Assert.Single(scenario.Decisions);
        Assert.Equal(HumanWaitKind.RecoveryChoice, decision.Kind);
        Assert.False(decision.IsAutoDefaultable);
        Assert.False(decision.IsDismissible);
        Assert.Contains(StableId, decision.Question);
        Assert.Contains(SecondDescription, decision.Question);
        Assert.Contains(Blocker, decision.Question);
        Assert.Contains("Cited files unchanged: False", decision.Question);
        Assert.Contains("rounds=2", decision.Question);
        Assert.Contains("goal-amend", decision.Question);
        Assert.Contains("revise", decision.Question);
        Assert.Contains("retry --goal", decision.Question);
        scenario.Advance();
        scenario.AssertHeld(before);
        scenario.Restore();
        scenario.Advance();
        scenario.AssertHeld(before);
        Assert.Equal(decision.Id, Assert.Single(scenario.Decisions).Id);
    }

    [Xunit.Fact]
    public void Untouched_cited_file_blocks_without_developer_blocker()
    {
        var scenario = new FindingStagnationScenario();
        scenario.BlockSecondRound("none");
        Assert.Contains("no Developer blocker reported", Assert.Single(scenario.Decisions).Question);
        Assert.Contains("Cited files unchanged: True", Assert.Single(scenario.Decisions).Question);
    }
}
