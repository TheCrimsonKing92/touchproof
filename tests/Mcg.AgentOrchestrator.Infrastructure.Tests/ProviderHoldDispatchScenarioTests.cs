using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private event logs, an injected clock and no provider processes.
public sealed class ProviderHoldDispatchScenarioTests
{
    [Theory]
    [InlineData("You've hit your weekly limit · resets Oct 7, 12pm (America/Chicago)", "", ProviderFailureKind.BudgetExhausted, "2026-10-07T17:00:00.0000000+00:00")]
    [InlineData("You've hit your weekly limit", "", ProviderFailureKind.BudgetExhausted, "unknown")]
    [InlineData("", "ERROR: 401 invalid credentials", ProviderFailureKind.AuthenticationRequired, "unknown")]
    public void PreflightStatusFleetAndWatchAgree(string stdout, string stderr, ProviderFailureKind kind, string reset)
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var clock = new ProviderHoldScenarioClock();
        var kernel = new AgentOrchestratorKernel(clock);
        Record(kernel, clock, stdout, stderr, @"C:\private\account-a");
        var goal = NewGoal(kernel);
        var findings = Findings(kernel, goal, @"C:\private\account-a");
        var blocked = Assert.Single(findings);
        var row = Assert.Single(ProviderUsageHoldView.Read(kernel.Goals, clock.UtcNow));
        Assert.Equal(kind, row.Hold.Reason);
        Assert.Contains($"binding {row.Hold.BindingKey}", blocked);
        var json = JsonSerializer.SerializeToElement(ProviderUsageHoldJson.Row(row));
        Assert.Equal(kind.ToString(), json.GetProperty("reason").GetString());
        Assert.Equal(reset, json.GetProperty("providerResetAt").GetString());
        var fleet = OwnerFleetViewModelBuilder.Build(RemoteExecutorReport.Build([], [], []), true, TimeZoneInfo.Utc, providerHolds: [row]);
        var node = Assert.Single(fleet.ProviderHolds!);
        Assert.Equal(kind, node.HoldReason);
        Assert.Equal(reset, ProviderUsageHoldView.FormatProviderReset(node.ProviderResetAt));
        Assert.Contains(kind == ProviderFailureKind.AuthenticationRequired ? "sign-in required" : "provider reset:",
            OwnerFleetDiagram.Render(fleet, 100));
        var writer = new ConductEventLogWriter(fixture.Workspace.ConductEventsLogPath);
        new ConductorProviderHoldWatch(writer).ObserveTick(kernel.Goals, clock.UtcNow);
        var line = Assert.Single(File.ReadAllLines(writer.CurrentPath));
        Assert.True(ConductEventFileSource.TryParse(line, out var evt));
        Assert.DoesNotContain("private", evt!.Detail);
        Assert.True(OwnerProviderHoldNarration.TryNarrate(evt, out _, out var reason, out _, out _));
        if (kind == ProviderFailureKind.AuthenticationRequired)
        {
            Assert.Contains("sign in", blocked);
            Assert.Contains("authentication-required", reason);
        }
        else
        {
            Assert.Contains($"provider_reset={reset}", blocked);
            Assert.Contains($"provider reset: {reset}", reason);
        }
    }

    [Fact]
    public void QuotaRefusalsDoNotSpendTheReviewBudgetAndTimeoutDoesNotPlaceAHold()
    {
        var clock = new ProviderHoldScenarioClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = NewGoal(kernel);
        Record(kernel, clock, "You've hit your weekly limit", "", null, goal: goal);
        clock.UtcNow = clock.UtcNow.AddHours(1);
        Record(kernel, clock, "You've hit your weekly limit", "", null, goal: goal);
        var task = goal.Tasks.Single();
        Assert.Equal(2, SubscriptionLimitEvidence.CountRecoverableSubscriptionLimitFailures(task));
        Assert.Equal(0, SubscriptionLimitEvidence.CountRecoverableSubscriptionLimitFailures(task, includeProviderHolds: false));
        Assert.False(ProviderLimitSignals.RequiresSubscriptionLimitReview(task));
        Assert.Empty(task.RetryAdmissionHistory);

        var timeout = new AgentOrchestratorKernel(clock);
        Record(timeout, clock, "", "Provider process timed out", null);
        Assert.Empty(ProviderUsageHoldView.Read(timeout.Goals, clock.UtcNow));
    }

    internal static Goal NewGoal(AgentOrchestratorKernel kernel)
    {
        var goal = kernel.CreateGoal("Provider hold fixture", [new TaskSpec(TaskId.New(), "Verify fixture", AgentRole.Tester)]);
        kernel.ActivateGoal(goal.Id, [ProviderHoldAgentSubstitutionTests.Scenario.Agent("claude-tester", "Anthropic", "claude-cli", "claude-sonnet-4-6")]);
        return goal;
    }

    internal static Goal Record(AgentOrchestratorKernel kernel, ProviderHoldScenarioClock clock, string stdout,
        string stderr, string? credential, string model = "claude-sonnet-4-6", Goal? goal = null, string? apiRoute = null)
    {
        goal ??= NewGoal(kernel);
        var task = goal.Tasks.Single();
        var start = clock.UtcNow.AddSeconds(-1);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new("claude-cli", "fixture", ".", start, ProviderName: "Anthropic", ModelName: model,
            ClaudeCredentialSourceDirectory: credential, ClaudeApiHoldIdentity: apiRoute));
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new("fixture", ".", 1, stdout, stderr,
            clock.UtcNow, DispatchStartedAt: start));
        return goal;
    }

    internal static List<string> Findings(AgentOrchestratorKernel kernel, Goal candidate, string? credential, string model = "claude-sonnet-4-6")
    {
        var findings = new List<string>();
        WorkerSubscriptionPreflightFindings.AddProviderBudgetExhaustionFinding(findings, kernel.ProviderHoldScope,
            "Anthropic", credential, model, candidate, candidate.Tasks.Single(), candidate.Tasks.Single().LastDispatch?.DispatchedAt ?? DateTimeOffset.MinValue);
        return findings;
    }
}

internal sealed class ProviderHoldScenarioClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 5, 10, 0, 0, TimeSpan.Zero);
}
