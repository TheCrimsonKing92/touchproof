using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its host, files, controller and injected clock.
public sealed class ExecutorSlotRevertRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingRevert_EventFailure_RetriesWithFrozenBaseline(bool restart)
    {
        using var fixture = new ExecutorSlotControllerFixture();
        var before = StartRegressedTrial(fixture);
        var eventCalls = 0;
        var controller = fixture.Controller(eventWriter: _ => ++eventCalls > 1);
        Assert.Contains("result=audit-failed", Assert.Single(controller.Evaluate()));
        Assert.Equal(1, eventCalls);
        AssertPendingRevert(fixture, before);

        if (restart) controller = fixture.Controller();
        Assert.Contains("application=applied", Assert.Single(controller.Evaluate()));
        Assert.Equal("revert", fixture.Latest.Decision.Action);
        Assert.Equal(before.Trial!.PriorSlots, fixture.Slots);
        Assert.Null(ExecutorSlotDecisionLedger.Restore(fixture.Latest, fixture.Slots)!.Trial);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingRevert_HandlerFailure_RetriesWithFrozenBaseline(bool restart)
    {
        using var fixture = new ExecutorSlotControllerFixture();
        var before = StartRegressedTrial(fixture);
        string? backupCollision = null;
        var injectFailure = true;
        var controller = fixture.Controller(eventWriter: row =>
        {
            if (!injectFailure) return true;
            // The handler creates this backup before replacing the setting file. An
            // existing file forces its real CreateNew boundary to fail before mutation.
            var stamp = fixture.Now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'",
                System.Globalization.CultureInfo.InvariantCulture) + "." + row.EvaluationId[..8];
            backupCollision = fixture.ConfigurationPath + "." + stamp + ".bak";
            File.WriteAllText(backupCollision, "injected-backup-collision");
            return true;
        });
        Assert.Contains("executors-file-invalid", Assert.Single(controller.Evaluate()));
        Assert.NotNull(backupCollision);
        Assert.Equal("injected-backup-collision", File.ReadAllText(backupCollision));
        AssertPendingRevert(fixture, before);
        File.Delete(backupCollision);
        injectFailure = false;

        if (restart) controller = fixture.Controller();
        Assert.Contains("application=applied", Assert.Single(controller.Evaluate()));
        Assert.Equal("revert", fixture.Latest.Decision.Action);
        Assert.Equal(before.Trial!.PriorSlots, fixture.Slots);
    }

    [Fact]
    public void PendingRevert_InterruptedBeforeHandler_RestartPreservesTrial()
    {
        using var fixture = new ExecutorSlotControllerFixture();
        var before = StartRegressedTrial(fixture);
        var decision = ExecutorSlotDecision.Decide(fixture.Samples, fixture.Settings, before,
            fixture.Slots, false, fixture.Now);
        Assert.Equal("revert", decision.Action);
        ExecutorSlotDecisionLedger.Append(fixture.LedgerPath, new("interrupted", fixture.Now,
            "exec", fixture.Settings, decision, decision.NextState, "pending-handler",
            BeforeTransitionState: before));
        AssertPendingRevert(fixture, before);

        fixture.Controller().Evaluate();
        Assert.Equal("revert", fixture.Latest.Decision.Action);
        Assert.Equal(1, fixture.Slots);
    }

    [Fact]
    public void AppliedRevert_Restart_DoesNotRestoreOldTrial()
    {
        using var fixture = new ExecutorSlotControllerFixture();
        StartRegressedTrial(fixture);
        fixture.Controller().Evaluate();
        Assert.Equal("revert", fixture.Latest.Decision.Action);
        Assert.Equal(1, fixture.Slots);
        Assert.NotNull(fixture.Latest.BeforeTransitionState!.Trial);
        Assert.Null(ExecutorSlotDecisionLedger.Restore(fixture.Latest, fixture.Slots)!.Trial);

        fixture.Controller().Evaluate();
        Assert.Equal("hold", fixture.Latest.Decision.Action);
        Assert.Null(fixture.Latest.ControllerState.Trial);
        Assert.Null(fixture.Latest.BeforeTransitionState);
        Assert.Equal(1, fixture.Slots);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void LegacyPendingRevert_Restart_RecoversCommittedTrial(int version)
    {
        using var fixture = new ExecutorSlotControllerFixture();
        var before = StartRegressedTrial(fixture);
        fixture.Controller(eventWriter: _ => false).Evaluate();
        var lines = File.ReadAllLines(fixture.LedgerPath);
        var json = JsonNode.Parse(lines[^1])!;
        json["version"] = version;
        json.AsObject().Remove("before_transition_state");
        if (version == 1)
        {
            json["before_state"] = JsonSerializer.SerializeToNode(before, ExecutorSlotDecisionLedger.JsonOptions);
            json["decision"]!["next_state"] = json["controller_state"]!.DeepClone();
            json.AsObject().Remove("controller_state");
        }
        lines[^1] = json.ToJsonString();
        File.WriteAllLines(fixture.LedgerPath, lines);
        AssertPendingRevert(fixture, before);

        fixture.Controller().Evaluate();
        Assert.Equal("revert", fixture.Latest.Decision.Action);
        Assert.Equal(1, fixture.Slots);
    }

    [Fact]
    public void LegacyRevert_MissingCommittedHistory_FailsLoudly()
    {
        using var fixture = new ExecutorSlotControllerFixture();
        StartRegressedTrial(fixture);
        fixture.Controller(eventWriter: _ => false).Evaluate();
        var row = fixture.Latest with { Version = 2, BeforeTransitionState = null };
        File.WriteAllText(fixture.LedgerPath, JsonSerializer.Serialize(row,
            ExecutorSlotDecisionLedger.JsonOptions) + "\n");
        var error = Assert.Throws<InvalidOperationException>(() =>
            ExecutorSlotDecisionLedger.Restore(fixture.Latest, fixture.Slots));
        Assert.Equal("missing-executor-slot-revert-checkpoint", error.Message);
        Assert.Equal(2, fixture.Slots);
    }

    private static ExecutorSlotControllerState StartRegressedTrial(ExecutorSlotControllerFixture fixture)
    {
        Assert.Contains("application=applied", Assert.Single(fixture.Controller().Evaluate()));
        Assert.Equal(2, fixture.Slots);
        var before = ExecutorSlotDecisionLedger.Restore(fixture.Latest, fixture.Slots)!;
        Assert.NotNull(before.Trial);
        fixture.Samples = ExecutorSlotTestData.CompletedTrial(failure: "lane-timeout");
        fixture.Now = fixture.Now.AddMinutes(2);
        return before;
    }

    private static void AssertPendingRevert(ExecutorSlotControllerFixture fixture,
        ExecutorSlotControllerState before)
    {
        var row = fixture.Latest;
        Assert.Equal("revert", row.Decision.Action);
        Assert.Equal("pending-handler", row.ApplicationStatus);
        Assert.Null(row.ControllerState.Trial);
        Assert.Equal(2, fixture.Slots);
        var restored = ExecutorSlotDecisionLedger.Restore(row, fixture.Slots)!;
        Assert.Equal(JsonSerializer.Serialize(before, ExecutorSlotDecisionLedger.JsonOptions),
            JsonSerializer.Serialize(restored, ExecutorSlotDecisionLedger.JsonOptions));
    }
}
