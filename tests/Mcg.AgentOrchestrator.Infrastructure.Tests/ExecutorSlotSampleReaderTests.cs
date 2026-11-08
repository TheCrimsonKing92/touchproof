using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ExecutorSlotSampleReaderTests
{
    [Fact]
    public void Reader_FocusedAndGateAttempts_PoolsFocusedAndCountsOverlap()
    {
        using var fixture = new ExecutorSlotControllerFixture();
        var path = Path.Combine(fixture.Root, "health.jsonl");
        var at = ExecutorSlotTestData.Now;
        var binding = new RemoteLaneBinding("exec", "lane", "commit", "tree", "main", "filter", "manifest");
        using var status = JsonDocument.Parse("{\"seconds\":10}");
        var detail = new RemoteLaneAttemptDetail([new("run", 0, false, at.AddSeconds(-30), at.AddSeconds(-10), "")],
            null, [], status.RootElement.Clone(), null, null);
        RemoteExecutorHealthLedger.Append(path, new(at.AddSeconds(-10), "exec", "gate", "lane", RemoteLaneOutcomeCode.Accepted,
            null, binding, binding, Attempt: detail));
        RemoteExecutorHealthLedger.Append(path, new(at.AddSeconds(-15), "exec", "focused", "some-filter", RemoteLaneOutcomeCode.Accepted,
            null, binding, binding, GoalId: "goal", RemoteDurationSeconds: 10));
        RemoteExecutorHealthLedger.Append(path, new(at.AddSeconds(-1), "exec", "unknown", "lane", RemoteLaneOutcomeCode.LeaseExpired,
            "executor", binding, null));
        File.AppendAllText(path, "malformed\n");
        var samples = ExecutorSlotSampleReader.Read(path, "exec", at.AddHours(-1), at);
        Assert.Equal(3, samples.Count);
        var focused = samples.Single(s => s.AttemptId == "focused");
        Assert.Equal("focused-evidence", focused.Lane);
        Assert.Equal("remote-duration", focused.IntervalSource);
        Assert.Equal(at.AddSeconds(-25), focused.StartedAt);
        Assert.Equal(2, focused.Concurrency);
        Assert.Equal(2, samples.Single(s => s.AttemptId == "gate").Concurrency);
        Assert.Null(samples.Single(s => s.AttemptId == "unknown").Concurrency);
    }

    [Fact]
    public void Policy_ExplicitSettings_AreValidatedAndUsed()
    {
        using var fixture = new ExecutorSlotControllerFixture();
        var path = Path.Combine(fixture.Root, "conductor-policy.json");
        Assert.Equal(new ExecutorSlotControllerSettings(), ExecutorSlotSampleReader.ReadSettings(path));
        File.WriteAllText(path, """
            { "executorSlotController": { "stableWindows": 7, "trialJobs": 42 } }
            """);
        var settings = ExecutorSlotSampleReader.ReadSettings(path);
        Assert.Equal(7, settings.StableWindows);
        Assert.Equal(42, settings.TrialJobs);
        Assert.Equal(24, settings.LookbackHours);
        File.WriteAllText(path, """
            { "executorSlotController": { "stableWindows": 0 } }
            """);
        Assert.Throws<InvalidOperationException>(() => ExecutorSlotSampleReader.ReadSettings(path));
    }

    [Fact]
    public void Reader_InvalidDurationField_LeavesEstimateUnavailable()
    {
        using var fixture = new ExecutorSlotControllerFixture();
        var path = Path.Combine(fixture.Root, "health.jsonl");
        var at = ExecutorSlotTestData.Now;
        var binding = new RemoteLaneBinding("exec", "lane", "commit", "tree", "main", "filter", "manifest");
        using var status = JsonDocument.Parse("{\"seconds\":\"invalid\"}");
        var detail = new RemoteLaneAttemptDetail([new("run", 0, false, at.AddSeconds(-10), at.AddSeconds(-1), "")],
            null, [], status.RootElement.Clone(), null, null);
        RemoteExecutorHealthLedger.Append(path, new(at.AddSeconds(-1), "exec", "gate", "lane", RemoteLaneOutcomeCode.Accepted,
            null, binding, binding, Attempt: detail));
        var sample = Assert.Single(ExecutorSlotSampleReader.Read(path, "exec", at.AddHours(-1), at));
        Assert.Null(sample.RemoteSeconds);
        var result = ExecutorSlotDecision.Decide([sample], ExecutorSlotTestData.Settings, null, 1, false, at);
        Assert.Equal("hold", result.Action);
        Assert.Null(result.Estimates[0].Throughput);
    }
}
