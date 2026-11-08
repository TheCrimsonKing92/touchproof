using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ExecutorSlotControllerWiringTests
{
    [Fact]
    public void QuiescentTrial_UsesExistingHandler_BackupAndSettingEventProvePath()
    {
        using var fixture = new ExecutorSlotControllerFixture();
        var before = File.ReadAllBytes(fixture.ConfigurationPath);
        var coordinator = new OperatorIntentCoordinator(new SqliteOperatorIntentStore(Path.Combine(fixture.Root, "intents.db"), fixture.Root))
            { SlotController = fixture.Controller() };
        var progress = new List<string>();
        Assert.Empty(ExecutorSlotControllerTick.Service(coordinator, new AgentOrchestratorKernel(), progress.Add));
        Assert.Contains("application=applied", Assert.Single(progress));
        Assert.Equal(2, fixture.Slots);
        var backup = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(fixture.ConfigurationPath)!, "*.bak"));
        Assert.Equal(before, File.ReadAllBytes(backup));
        var setting = Assert.Single(fixture.Events.Where(line => line.Contains("\"executor-setting\"", StringComparison.Ordinal)));
        using var json = JsonDocument.Parse(setting);
        var detail = json.RootElement.GetProperty("detail").GetString()!;
        Assert.Contains("intent=" + fixture.Latest.EvaluationId, detail);
        Assert.Contains("actor=conductor actor_kind=Agent", detail);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    public void AnyHeldSlotLock_Trial_DefersUntilReleased(int slot)
    {
        using var fixture = new ExecutorSlotControllerFixture();
        var before = File.ReadAllBytes(fixture.ConfigurationPath);
        using (var claim = RemoteExecutorOccupancy.TryClaimExclusive(fixture.Root, "exec", slot))
        {
            Assert.NotNull(claim);
            fixture.Controller().Evaluate();
            Assert.Equal("deferred-busy", fixture.Latest.ApplicationStatus);
            Assert.Equal(before, File.ReadAllBytes(fixture.ConfigurationPath));
            Assert.Null(fixture.Latest.BeforeState.Trial);
        }
        fixture.Controller().Evaluate();
        Assert.Equal(2, fixture.Slots);
    }

    [Theory]
    [InlineData("Gate")]
    [InlineData("FocusedEvidence")]
    public void LiveAttempt_Trial_DefersWithNoSettingEvent(string claimClass)
    {
        using var fixture = new ExecutorSlotControllerFixture();
        var before = File.ReadAllBytes(fixture.ConfigurationPath);
        RemoteExecutorOccupancy.Claim(fixture.Root, "exec", "running", 0, Enum.Parse<RemoteExecutorClaimClass>(claimClass));
        try
        {
            Assert.True(RemoteExecutorOccupancy.IsOccupied(fixture.Root, "exec"));
            fixture.Controller().Evaluate();
            Assert.Equal("deferred-busy", fixture.Latest.ApplicationStatus);
            Assert.Equal(before, File.ReadAllBytes(fixture.ConfigurationPath));
            Assert.Single(fixture.Events);
        }
        finally { RemoteExecutorOccupancy.Release(fixture.Root, "exec", "running", 0); }
    }

    [Fact]
    public void PinnedExecutor_ReportsHypotheticalTrial_LeavesSettingAndStateAlone()
    {
        using var fixture = new ExecutorSlotControllerFixture();
        fixture.Pinned = true;
        var before = File.ReadAllBytes(fixture.ConfigurationPath);
        fixture.Controller().Evaluate();
        Assert.Equal(before, File.ReadAllBytes(fixture.ConfigurationPath));
        Assert.Equal("report-only", fixture.Latest.Decision.Action);
        Assert.Equal("trial-up", fixture.Latest.Decision.UnderlyingAction);
        Assert.Equal(2, fixture.Latest.Decision.ProposedSlots);
        Assert.Null(fixture.Latest.Decision.NextState.Trial);
        Assert.Equal("pinned", fixture.Latest.ApplicationStatus);
        Assert.Single(fixture.Events);
    }

    [Fact]
    public void Hold_InsufficientEvidence_LeavesExecutorFileUnchanged()
    {
        using var fixture = new ExecutorSlotControllerFixture();
        fixture.Samples = [];
        var before = File.ReadAllBytes(fixture.ConfigurationPath);
        fixture.Controller().Evaluate();
        Assert.Equal("hold", fixture.Latest.Decision.Action);
        Assert.Equal(before, File.ReadAllBytes(fixture.ConfigurationPath));
        Assert.Single(fixture.Events);
    }

    [Fact]
    public void DisabledExecutor_IsNotEvaluatedOrChanged()
    {
        using var fixture = new ExecutorSlotControllerFixture();
        File.WriteAllText(fixture.ConfigurationPath, """
            { "executors": [{ "id": "exec", "slots": 1, "enabled": false }], "lanes": ["*"] }
            """);
        Assert.Empty(fixture.Controller().Evaluate());
        Assert.False(File.Exists(fixture.LedgerPath));
        Assert.Equal(1, fixture.Slots);
    }
}
