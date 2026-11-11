using Mcg.AgentOrchestrator.Core;

// Parallel-safe: in-memory goals and fixed timestamps only.
public sealed class ProviderHoldSeedPruningTests
{
    [Fact]
    public void HistoryRows_IdenticalRecoveryRows_CanRequireDifferentRetainedSets()
    {
        var recovered = Goals(recoveryCredential: null);
        var held = Goals(recoveryCredential: @"C:\other-login");
        var recoveredRows = ModelFitHistory.FromGoals(recovered);
        var heldRows = ModelFitHistory.FromGoals(held);
        Assert.Equal(2, recoveredRows.Count);
        Assert.Equal(recoveredRows, heldRows);
        Assert.Contains(recoveredRows, row => row.OutcomeRule == TaskOutcomeRules.ProviderBudgetExhausted.Token);
        Assert.Contains(recoveredRows, row => row.Outcome == WorkTaskStatus.Completed && row.OutcomeClass == TaskOutcomeClass.Success);
        var recoveredHistory = new ProviderHoldHistory();
        var heldHistory = new ProviderHoldHistory();
        recoveredHistory.Retain(recovered, []);
        heldHistory.Retain(held, []);
        Assert.Empty(recoveredHistory.Retained);
        Assert.Equal("aaaaaaaa000000000000000000000000", Assert.Single(heldHistory.Retained).Id.Value);
        Assert.False(ProviderLimitSignals.TryGetProviderBudgetExhaustionHold(recoveredHistory.Scope([]), "xAI", null, out _));
        Assert.True(ProviderLimitSignals.TryGetProviderBudgetExhaustionHold(heldHistory.Scope([]), "xAI", null, out _));
    }

    private static IReadOnlyCollection<Goal> Goals(string? recoveryCredential)
    {
        var kernel = new AgentOrchestratorKernel(new FixedClock());
        var started = DateTimeOffset.Parse("2026-10-17T11:07:00Z");
        for (var index = 0; index < 2; index++)
        {
            var success = index == 1;
            var at = started.AddMinutes(index * 10);
            var id = success ? "bbbbbbbb000000000000000000000000" : "aaaaaaaa000000000000000000000000";
            var goal = kernel.CreateGoal(new GoalId(id), "Pruning evidence",
                [new TaskSpec(new TaskId(id), "Verify fixture", AgentRole.Tester)]);
            kernel.ActivateGoal(goal.Id, [new(new("tester"), "Tester", AgentRole.Tester,
                new("xAI", "fixture", ModelCapability.Text | ModelCapability.Code | ModelCapability.ToolUse, SubscriptionMode.ApiKey))]);
            var task = goal.Tasks.Single();
            kernel.RecordTaskDispatch(goal.Id, task.Id, new("fixture", "fixture", @"C:\fixture", at,
                ProviderName: "xAI", ModelName: "fixture", ClaudeCredentialSourceDirectory: success ? recoveryCredential : null));
            kernel.RecordTaskVerification(goal.Id, task.Id, new("fixture", @"C:\fixture", success ? 0 : 1,
                "", success ? "" : "Provider budget exhausted.", at.AddMinutes(1),
                ProviderFailureKind: success ? ProviderFailureKind.Unknown : ProviderFailureKind.BudgetExhausted,
                DispatchStartedAt: at, CompletionVerdictVerifiedSuccess: success));
            kernel.ReportTaskProgress(goal.Id, task.Id, success ? WorkTaskStatus.Completed : WorkTaskStatus.Failed, "Fixture outcome.");
        }
        return kernel.Goals;
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.Parse("2026-10-17T12:07:00Z");
    }
}
