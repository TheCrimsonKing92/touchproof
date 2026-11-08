using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ExecutorSlotDecisionAuditTests
{
    [Theory]
    [InlineData("hold")]
    [InlineData("trial-up")]
    [InlineData("report-only")]
    public void Evaluation_EveryAction_AppendsOneMatchingRowAndEvent(string action)
    {
        using var fixture = new ExecutorSlotControllerFixture();
        if (action == "hold") fixture.Samples = [];
        fixture.Pinned = action == "report-only";
        fixture.Controller().Evaluate();
        var ledgerText = Assert.Single(File.ReadAllLines(fixture.LedgerPath));
        using var ledger = JsonDocument.Parse(ledgerText);
        using var conduct = JsonDocument.Parse(Assert.Single(fixture.DecisionEvents));
        var payload = conduct.RootElement.GetProperty("detail").GetString();
        using var summary = JsonDocument.Parse(payload!);
        Assert.Equal(ledger.RootElement.GetProperty("evaluation_id").GetString(),
            summary.RootElement.GetProperty("evaluation_id").GetString());
        Assert.True(Guid.TryParse(ledger.RootElement.GetProperty("evaluation_id").GetString(), out _));
        // The compact summary references the row; event_id would trigger a full-log dedup scan.
        Assert.False(conduct.RootElement.TryGetProperty("event_id", out _));
        Assert.False(conduct.RootElement.TryGetProperty("eventId", out _));
        var decision = ledger.RootElement.GetProperty("decision");
        Assert.Equal(action, decision.GetProperty("action").GetString());
        Assert.Equal(fixture.Samples.Count, decision.GetProperty("total_samples").GetInt32());
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
            Assert.True(failures.TryGetProperty("attempts", out _));
        }
        if (action == "hold") Assert.Equal(JsonValueKind.Null, estimates[0].GetProperty("throughput").ValueKind);
        if (action == "report-only") Assert.Equal("trial-up", decision.GetProperty("underlying_action").GetString());
    }

    [Fact]
    public void RepeatedHolds_SameEvidence_RecordOnce()
    {
        using var fixture = new ExecutorSlotControllerFixture();
        fixture.Samples = [];
        var controller = fixture.Controller();
        controller.Evaluate();
        fixture.Now = fixture.Now.AddSeconds(60);
        controller.Evaluate();
        Assert.Single(File.ReadAllLines(fixture.LedgerPath));
        using var conduct = JsonDocument.Parse(Assert.Single(fixture.DecisionEvents));
        using var summary = JsonDocument.Parse(conduct.RootElement.GetProperty("detail").GetString()!);
        Assert.Equal(fixture.Latest.EvaluationId, summary.RootElement.GetProperty("evaluation_id").GetString());
    }

    [Fact]
    public void LedgerFailure_StillWritesEventAndPreventsMutation()
    {
        using var fixture = new ExecutorSlotControllerFixture();
        var original = File.ReadAllBytes(fixture.ConfigurationPath);
        var lines = fixture.Controller((_, _) => throw new IOException("injected-ledger-failure")).Evaluate();
        Assert.Equal(original, File.ReadAllBytes(fixture.ConfigurationPath));
        Assert.Contains("result=audit-failed", Assert.Single(lines));
        using var record = JsonDocument.Parse(Assert.Single(fixture.DecisionEvents));
        using var detail = JsonDocument.Parse(record.RootElement.GetProperty("detail").GetString()!);
        Assert.Equal("failed-ledger", detail.RootElement.GetProperty("audit_status").GetString());
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(fixture.ConfigurationPath)!, "*.bak"));
    }

    [Fact]
    public void EventFailure_StillWritesLedgerAndPreventsMutation()
    {
        using var fixture = new ExecutorSlotControllerFixture();
        var original = File.ReadAllBytes(fixture.ConfigurationPath);
        var lines = fixture.Controller(eventWriter: _ => false).Evaluate();
        Assert.Single(File.ReadAllLines(fixture.LedgerPath));
        Assert.Equal(original, File.ReadAllBytes(fixture.ConfigurationPath));
        Assert.Contains("result=audit-failed", Assert.Single(lines));
        Assert.Equal("pending-handler", fixture.Latest.ApplicationStatus);
        // A failed event never turns a preaudit proposal into an applied trial on restart.
        fixture.Samples = [];
        fixture.Controller().Evaluate();
        Assert.Null(ExecutorSlotDecisionLedger.Restore(fixture.Latest, fixture.Slots)?.Trial);
    }

    [Fact]
    public void Restart_AppliedTrial_RestoresBaselineAndKeepsImprovement()
    {
        using var fixture = new ExecutorSlotControllerFixture();
        fixture.Controller().Evaluate();
        Assert.Equal(2, fixture.Slots);
        fixture.Samples = ExecutorSlotTestData.CompletedTrial();
        fixture.Now = fixture.Now.AddMinutes(2);
        fixture.Controller().Evaluate();
        Assert.Equal("keep", fixture.Latest.Decision.Action);
        Assert.Null(fixture.Latest.ControllerState.Trial);
        Assert.Equal(2, fixture.Slots);
        Assert.Equal(2, fixture.DecisionEvents.Length);
    }

    [Fact]
    public void Restart_StableWindow_DoesNotCountItTwice()
    {
        using var fixture = new ExecutorSlotControllerFixture();
        fixture.Settings = fixture.Settings with { StableWindows = 3 };
        fixture.Controller().Evaluate();
        Assert.Equal(1, fixture.Latest.ControllerState.StableWindows);
        fixture.Controller().Evaluate();
        Assert.Equal(1, fixture.Latest.ControllerState.StableWindows);
        Assert.Equal("hold", fixture.Latest.Decision.Action);
    }
}
