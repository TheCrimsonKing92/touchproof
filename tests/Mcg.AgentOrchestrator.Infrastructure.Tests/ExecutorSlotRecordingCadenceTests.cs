using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its files, controller, samples and injected clock.
public sealed class ExecutorSlotRecordingCadenceTests
{
    [Fact]
    public void UnchangedTicks_ThirtyTicks_RecordOneRowAndOneEventPerExecutor()
    {
        using var fixture = HoldingFixture();
        File.WriteAllText(fixture.ConfigurationPath, """
            { "executors": [{ "id": "exec", "slots": 1 }, { "id": "exec2", "slots": 1 }], "lanes": ["*"] }
            """);
        var controller = fixture.Controller();
        var evaluations = 0;
        for (var tick = 0; tick < 30; tick++)
        {
            var lines = controller.Evaluate();
            Assert.Equal(tick % 6 == 0 ? 2 : 0, lines.Count);
            evaluations += lines.Count;
            fixture.Now = fixture.Now.AddSeconds(10);
        }
        Assert.Equal(10, evaluations); // Five due evaluations per executor, not just a cadence skip.
        var rows = Rows(fixture);
        Assert.Equal(2, rows.Length);
        Assert.Equal(2, fixture.DecisionEvents.Length);
        foreach (var executor in new[] { "exec", "exec2" })
        {
            var row = Assert.Single(rows.Where(r => r.ExecutorId == executor));
            Assert.Equal("hold", row.Decision.Action);
            using var conduct = JsonDocument.Parse(Assert.Single(fixture.DecisionEvents.Where(line =>
            {
                using var envelope = JsonDocument.Parse(line);
                using var detail = JsonDocument.Parse(envelope.RootElement.GetProperty("detail").GetString()!);
                return detail.RootElement.GetProperty("executor_id").GetString() == executor;
            })));
            using var summary = JsonDocument.Parse(conduct.RootElement.GetProperty("detail").GetString()!);
            Assert.Equal(row.EvaluationId, summary.RootElement.GetProperty("evaluation_id").GetString());
        }
    }

    [Fact]
    public void NewSample_BetweenTicks_RecordsNextEvaluation()
    {
        using var fixture = HoldingFixture();
        var controller = fixture.Controller();
        controller.Evaluate();
        fixture.Now = fixture.Now.AddSeconds(10);
        Assert.Empty(controller.Evaluate());
        fixture.Samples = ExecutorSlotTestData.Jobs(count: 5);
        fixture.Now = fixture.Now.AddSeconds(10);
        Assert.Contains("recorded=change", Assert.Single(controller.Evaluate()));
        Assert.Equal(2, Rows(fixture).Length);
        Assert.Equal(2, fixture.DecisionEvents.Length);
        Assert.Equal(5, fixture.Latest.Decision.TotalSamples);
        Assert.Equal(fixture.Now, fixture.Latest.ObservedAt);
        // An early evaluation starts a new 60-second interval.
        fixture.Now = fixture.Now.AddSeconds(59);
        Assert.Empty(controller.Evaluate());
        fixture.Now = fixture.Now.AddSeconds(1);
        Assert.Contains("recorded=no", Assert.Single(controller.Evaluate()));
        Assert.Equal(2, Rows(fixture).Length);
    }

    [Fact]
    public void UnchangedEvaluations_SixtyMinutes_RecordHeartbeat()
    {
        using var fixture = HoldingFixture();
        var controller = fixture.Controller();
        controller.Evaluate();
        var initial = fixture.Latest;
        for (var minute = 1; minute <= 119; minute++)
        {
            fixture.Now = fixture.Now.AddMinutes(1);
            var line = Assert.Single(controller.Evaluate());
            Assert.Contains(minute == 60 ? "recorded=heartbeat" : "recorded=no", line);
            Assert.Equal(minute < 60 ? 1 : 2, Rows(fixture).Length);
            Assert.Equal(minute < 60 ? 1 : 2, fixture.DecisionEvents.Length);
        }
        var heartbeat = fixture.Latest;
        Assert.Equal(initial.ObservedAt.AddHours(1), heartbeat.ObservedAt);
        Assert.NotEqual(initial.EvaluationId, heartbeat.EvaluationId);
        Assert.Equal(initial.Decision.Action, heartbeat.Decision.Action);
        Assert.Equal(initial.Decision.ProposedSlots, heartbeat.Decision.ProposedSlots);
        Assert.Equal(initial.ApplicationStatus, heartbeat.ApplicationStatus);
    }

    [Fact]
    public void DecisionEvent_IsCompactAndNamesLedgerRow()
    {
        using var fixture = HoldingFixture();
        fixture.Controller().Evaluate();
        var eventLine = Assert.Single(fixture.DecisionEvents);
        Assert.True(Encoding.UTF8.GetByteCount(eventLine + Environment.NewLine) < 1000,
            "The complete conduct event envelope must be under 1,000 UTF-8 bytes.");
        using var conduct = JsonDocument.Parse(eventLine);
        using var summary = JsonDocument.Parse(conduct.RootElement.GetProperty("detail").GetString()!);
        var detail = summary.RootElement;
        var row = Assert.Single(Rows(fixture));
        Assert.Equal(row.EvaluationId, detail.GetProperty("evaluation_id").GetString());
        Assert.Equal(row.ExecutorId, detail.GetProperty("executor_id").GetString());
        Assert.Equal(row.Decision.Action, detail.GetProperty("action").GetString());
        Assert.Equal(row.Decision.ProposedSlots, detail.GetProperty("proposed_slots").GetInt32());
        Assert.Equal(row.ApplicationStatus, detail.GetProperty("application_status").GetString());
        Assert.Equal(row.Decision.TotalSamples, detail.GetProperty("total_samples").GetInt32());
        Assert.Equal(row.Decision.UnattributedSamples, detail.GetProperty("unattributed_samples").GetInt32());
        Assert.Equal(row.Decision.Estimates.Select(e => e.Samples),
            detail.GetProperty("samples_per_k").EnumerateArray().Select(e => e.GetInt32()));
        Assert.False(detail.TryGetProperty("estimates", out _));
        Assert.False(detail.TryGetProperty("controller_state", out _));
        using var ledger = JsonDocument.Parse(Assert.Single(File.ReadAllLines(fixture.LedgerPath)));
        var decision = ledger.RootElement.GetProperty("decision");
        Assert.Equal(4, decision.GetProperty("total_samples").GetInt32());
        var estimates = decision.GetProperty("estimates").EnumerateArray().ToArray();
        Assert.Equal(4, estimates.Length);
        foreach (var estimate in estimates)
        {
            Assert.True(estimate.TryGetProperty("samples", out _));
            Assert.True(estimate.TryGetProperty("slowdown", out _));
            Assert.True(estimate.TryGetProperty("throughput", out _));
            var failures = estimate.GetProperty("failures");
            Assert.True(failures.TryGetProperty("lane_timeout_rate", out _));
            Assert.True(failures.TryGetProperty("lease_expired_rate", out _));
            Assert.True(failures.TryGetProperty("trx_incomplete_rate", out _));
        }
        Assert.Equal(JsonValueKind.Number, estimates[0].GetProperty("slowdown").ValueKind);
        Assert.Equal(JsonValueKind.Number, estimates[0].GetProperty("throughput").ValueKind);
    }

    private static ExecutorSlotControllerFixture HoldingFixture()
    {
        var fixture = new ExecutorSlotControllerFixture();
        fixture.Settings = fixture.Settings with { StableWindows = 3 };
        return fixture;
    }

    private static ExecutorSlotDecisionRecord[] Rows(ExecutorSlotControllerFixture fixture) =>
        File.ReadAllLines(fixture.LedgerPath).Select(line =>
            JsonSerializer.Deserialize<ExecutorSlotDecisionRecord>(line, ExecutorSlotDecisionLedger.JsonOptions)!).ToArray();
}
