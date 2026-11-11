using Mcg.AgentOrchestrator.Core;

// Parallel-safe: in-memory goals and fixed timestamps only.
public sealed class ProviderHoldSeedPruningTests
{
    [Theory]
    [InlineData(null, 0)]
    [InlineData(@"C:\other-login", 2)]
    public void RequiredGoals_UsesBindingEvidenceAndMatchesCanonicalRetain(string? credential, int expectedLoads)
    {
        var goals = Goals(credential);
        var candidates = goals.Select(goal => goal.Id.Value).Order(StringComparer.Ordinal).ToArray();
        var evidence = Evidence(goals);
        var required = ProviderHoldSeedPruning.RequiredGoals(candidates, evidence, new(), []);
        Assert.Equal(expectedLoads, required.Count);
        if (expectedLoads > 0) Assert.Equal(candidates, required);
    }

    [Fact]
    public void RequiredGoals_UnsupportedOrIncompleteEvidencePreservesFullLoadPath()
    {
        var goals = Goals(null);
        var candidates = goals.Select(goal => goal.Id.Value).ToArray();
        Assert.Equal(candidates, ProviderHoldSeedPruning.RequiredGoals(candidates, null, new(), []));
        Assert.Equal(candidates, ProviderHoldSeedPruning.RequiredGoals(candidates, Evidence(goals).Take(1).ToArray(), new(), []));
    }

    private static IReadOnlyList<ProviderUsageGoalEvidence> Evidence(IReadOnlyCollection<Goal> goals) =>
        goals.Select(goal =>
        {
            // The test assembly can use the canonical snapshot exporter; no storage is involved.
            var row = goal.ToSnapshot();
            return new ProviderUsageGoalEvidence(row.Id, row.Status,
                row.Tasks.Select(task => new ProviderUsageTaskEvidence(task.Id, task.RequiredRole, task.Status,
                    task.AssignedAgentId, task.LastDispatch, task.DispatchHistory ?? [], task.LastVerification,
                    task.VerificationHistory ?? [], task.LatestProviderBudgetRecoveryAt)).ToArray(), goal.Timeline);
        }).ToArray();

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
