using static FindingStagnationScenario;

public sealed class FindingStagnationRetryInstructionTests
{
    [Xunit.Fact]
    public void Developer_retry_names_blocking_ids_and_exact_blocker_instruction()
    {
        var scenario = new FindingStagnationScenario();
        scenario.Raise(findings: [Finding(), Finding("A second defect needs repair.", "second-defect")]);
        var before = scenario.Retries;

        scenario.Advance();

        scenario.AssertRetried(before);
        Assert.Contains("If a blocking finding cannot be resolved by a code change", scenario.RetryMessage);
        Assert.Contains("report blockers: exact-blocker -", scenario.RetryMessage);
        Assert.Contains("name that finding's stable id verbatim", scenario.RetryMessage);
        Assert.Contains($"Blocking finding stable ids: {StableId}, second-defect.", scenario.RetryMessage);
    }
}
