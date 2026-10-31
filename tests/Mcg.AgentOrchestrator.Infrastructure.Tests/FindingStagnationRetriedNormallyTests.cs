using Mcg.AgentOrchestrator.Core;
using static FindingStagnationScenario;

public sealed class FindingStagnationRetriedNormallyTests
{
    [Xunit.Theory]
    [Xunit.InlineData("word-change")]
    [Xunit.InlineData("file-touched")]
    [Xunit.InlineData("diff-unavailable")]
    [Xunit.InlineData("unrelated-blocker")]
    [Xunit.InlineData("latest-result-clears-blocker")]
    public void Actionable_or_unproven_stagnation_retries_normally(string change)
    {
        var scenario = new FindingStagnationScenario();
        scenario.Raise();
        var before = scenario.Retries;
        scenario.Advance();
        scenario.AssertRetried(before);
        scenario.CompleteDeveloper(change == "word-change" || change == "latest-result-clears-blocker" ? Blocker :
            change == "unrelated-blocker" ? "exact-blocker - another-finding cannot be fixed." : "none");
        if (change == "latest-result-clears-blocker")
        {
            scenario.AdvanceClock();
            scenario.Kernel.RetryTask(scenario.Goal.Id, scenario.Developer.Id,
                "Record a newer Developer result after resolving the blocker.");
            scenario.CompleteDeveloper();
        }
        var touched = change is "file-touched" or "unrelated-blocker" or "latest-result-clears-blocker"
            ? new[] { new ReviewFindingLocation("src/Cause.cs", "Cause.DifferentRegion") } : [];
        scenario.Raise(change == "word-change" ? SecondDescription.Replace("missing", "contradictory") : SecondDescription,
            touched: touched, diagnostic: change == "diff-unavailable" ? "Candidate diff unavailable" : null);
        before = scenario.Retries;

        scenario.Advance();

        scenario.AssertRetried(before);
        Assert.Empty(scenario.Decisions);
    }
}
