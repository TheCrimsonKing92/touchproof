using Mcg.AgentOrchestrator.Core;

// Parallel-safe: in-memory goals, fixed timestamps and an invocation-local counter only.
public sealed class ProviderHoldHistoryRetainEvaluationTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-12T00:00:00Z");

    [Fact]
    public void Retain_Recovered65Candidates_MeetsWholeScopeEvaluationBound()
    {
        var candidates = Enumerable.Range(0, 65).Select(index =>
        {
            var exhausted = index >= 62;
            var success = index < 2;
            var minute = exhausted ? (index - 62) * 2 : success ? 100 + index : 10 + index;
            var primary = ObservedTask($"{index:D3}-primary", "xAI", minute,
                exhausted ? false : success ? true : null);
            return new Goal(new($"{index:D3}"), "Retention count fixture",
                index % 2 == 0 ? [primary, ObservedTask($"{index:D3}-other", "OpenAI", 120, null)] : [primary]);
        }).ToArray();
        var bindings = candidates.SelectMany(goal => goal.Tasks).SelectMany(task => task.DispatchHistory)
            .Select(dispatch => (dispatch.ProviderName, dispatch.ClaudeCredentialSourceDirectory)).Distinct().Count();
        Assert.Equal(2, bindings);
        Assert.False(ProviderLimitSignals.TryGetProviderBudgetExhaustionHold(candidates, "xAI", null, out _));

        var evaluations = 0;
        var history = new ProviderHoldHistory((scope, provider, credential) =>
        {
            evaluations++;
            return ProviderHoldHistory.Answer(scope, provider, credential);
        });
        history.Retain(candidates, []);

        Assert.Empty(history.Retained);
        Assert.InRange(evaluations, 1, 2 * bindings + candidates.Length);
        // Without scope-answer reuse, even the exhausted binding alone needs 130 evaluations.
    }

    [Fact]
    public void Retain_ActiveHold_KeepsExactlyTwoArchivedDependencies()
    {
        var earlier = new Goal(new("b"), "Earlier exhaustion", [ObservedTask("b-task", "xAI", 10, false)]);
        var later = new Goal(new("c"), "Later exhaustion", [ObservedTask("c-task", "xAI", 20, false)]);
        var recovery = new Goal(new("a"), "Recovery before exhaustions", [ObservedTask("a-task", "xAI", 0, true)]);
        var unrelated = new Goal(new("d"), "Other provider", [ObservedTask("d-task", "OpenAI", 30, null)]);
        Goal[] candidates = [recovery, earlier, later, unrelated];
        Assert.True(ProviderLimitSignals.TryGetProviderBudgetExhaustionHold(candidates, "xAI", null, out var hold));
        Assert.Equal(2, ProviderBudgetHoldSchedules.Evaluate(candidates, hold, hold.PlacedAt).Attempt);

        var history = new ProviderHoldHistory();
        history.Retain(candidates, []);

        Assert.Equal(new[] { "b", "c" }, history.Retained.Select(goal => goal.Id.Value));
        Assert.True(ProviderLimitSignals.TryGetProviderBudgetExhaustionHold(history.Scope([]), "xAI", null, out var retainedHold));
        Assert.Equal(later.Id, retainedHold.SourceGoalId);
        Assert.Equal(2, ProviderBudgetHoldSchedules.Evaluate(history.Scope([]), retainedHold, retainedHold.PlacedAt).Attempt);
    }

    private static TaskSpec ObservedTask(string id, string provider, int minute, bool? success)
    {
        var task = new TaskSpec(new(id), "Provider observation", AgentRole.Tester);
        var at = Start.AddMinutes(minute);
        task.RecordDispatch(new("fixture", "fixture", @"C:\fixture", at, ProviderName: provider));
        if (success is { } succeeded)
            task.RecordVerification(new("fixture", @"C:\fixture", succeeded ? 0 : 1, "",
                succeeded ? "" : "Provider budget exhausted.", at.AddMinutes(1),
                ProviderFailureKind: succeeded ? ProviderFailureKind.Unknown : ProviderFailureKind.BudgetExhausted,
                DispatchStartedAt: at, CompletionVerdictVerifiedSuccess: succeeded));
        return task;
    }
}
