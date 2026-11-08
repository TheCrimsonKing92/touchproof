using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ExecutorSlotConcurrencyAttributionTests
{
    private static RemoteExecutorOutcomeRow Row(string id, int start, int end, string outcome = "accepted", string lane = "lane") =>
        new(ExecutorSlotTestData.Now.AddSeconds(end), "exec", id, lane, outcome, null, end - start, ExecutorSlotTestData.Now.AddSeconds(start));

    [Fact]
    public void Attribution_LaterStartingOverlap_AssignsPeakToBothJobs()
    {
        var rows = new[] { Row("a", -30, -10), Row("b", -20, -15), Row("c", -10, -5) };
        var samples = ExecutorSlotConcurrencyAttribution.Build(rows, "exec", ExecutorSlotTestData.Now.AddHours(-1), ExecutorSlotTestData.Now);
        Assert.Equal(new int?[] { 2, 2, 1 }, samples.OrderBy(s => s.AttemptId).Select(s => s.Concurrency));
        var report = RemoteExecutorReport.Build(["exec"], rows, []);
        Assert.Equal(samples.Count(s => s.Concurrency > 1), report.Executors[0].ConcurrentAttempts);
    }

    [Fact]
    public void Attribution_MoreThanFourJobs_ClampsToFour()
    {
        var samples = ExecutorSlotConcurrencyAttribution.Build(Enumerable.Range(0, 6).Select(i => Row(i.ToString(), -10, -1)),
            "exec", ExecutorSlotTestData.Now.AddHours(-1), ExecutorSlotTestData.Now);
        Assert.All(samples, s => Assert.Equal(4, s.Concurrency));
    }

    [Fact]
    public void Population_ExclusionsAndUnknownIntervals_UsesReportDenominator()
    {
        var rows = new[] { Row("retry1", -10, -9, "lane-timeout"), Row("retry2", -8, -7, "trx-incomplete"),
            Row("skip", -6, -5, "executor-busy"), Row("late", -6, -5, "late-after-fallback"),
            Row("preempt", -6, -5, "preempted-by-gate"), Row("tools", -6, -5, "executor-tools-mismatch"),
            Row("exclusive", -6, -5, "not-eligible-exclusive-resource"),
            Row("unknown", -4, -3, "lease-expired") with { StartedAt = null },
            Row("other", -4, -3) with { ExecutorId = "other" }, Row("unassigned", -4, -3) with { ExecutorId = "" } };
        var samples = ExecutorSlotConcurrencyAttribution.Build(rows, "exec", ExecutorSlotTestData.Now.AddHours(-1), ExecutorSlotTestData.Now);
        Assert.Equal(3, samples.Count);
        var result = ExecutorSlotDecision.Decide(samples, ExecutorSlotTestData.Settings, null, 1, false, ExecutorSlotTestData.Now);
        Assert.Equal(1, result.UnattributedSamples);
        var rates = result.Estimates[0].Failures;
        Assert.Equal(2, rates.Attempts);
        Assert.Equal(0.5, rates.LaneTimeoutRate);
        Assert.Equal(0.5, rates.TrxIncompleteRate);
        Assert.Equal(0, rates.LeaseExpiredRate);
    }
}
