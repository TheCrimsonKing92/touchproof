using Mcg.AgentOrchestrator.Core;

// Reference: ProviderHoldHistory at eb9c6d908, independent of the optimized Retain.
// Used only by ProviderHoldHistoryRetainEquivalenceTests.
internal sealed class ProviderHoldHistoryReferenceRetain
{
    private readonly SortedDictionary<string, Goal> _retained = new(StringComparer.Ordinal);

    public IReadOnlyCollection<Goal> Retained => _retained.Values;

    public IReadOnlyCollection<Goal> Scope(IReadOnlyCollection<Goal> workingSet)
    {
        if (_retained.Count == 0) return workingSet;
        var ids = workingSet.Select(goal => goal.Id.Value).ToHashSet(StringComparer.Ordinal);
        return workingSet.Concat(_retained.Values.Where(goal => !ids.Contains(goal.Id.Value))).ToArray();
    }

    public void Retain(IReadOnlyCollection<Goal> leaving, IReadOnlyCollection<Goal> workingSet)
    {
        var working = workingSet.ToArray();
        var ids = working.Select(goal => goal.Id.Value).ToHashSet(StringComparer.Ordinal);
        foreach (var goal in leaving)
            if (!ids.Contains(goal.Id.Value)) _retained[goal.Id.Value] = goal;

        bool dropped;
        do
        {
            dropped = false;
            foreach (var goal in _retained.Values.ToArray())
            {
                var scope = Scope(working);
                // An ingested working-set copy already shadows the retained copy.
                var without = ids.Contains(goal.Id.Value)
                    ? scope
                    : scope.Where(item => item.Id != goal.Id).ToArray();
                if (!SameAnswers(goal, scope, without)) continue;
                _retained.Remove(goal.Id.Value);
                dropped = true;
            }
        } while (dropped);
    }

    private static bool SameAnswers(Goal goal, IReadOnlyCollection<Goal> scope, IReadOnlyCollection<Goal> without)
    {
        var bindings = goal.Tasks.SelectMany(task => task.DispatchHistory)
            .Select(dispatch => (Provider: dispatch.ProviderName ?? dispatch.WorkerName,
                Credential: dispatch.ClaudeCredentialSourceDirectory)).Distinct();
        foreach (var binding in bindings)
            if (Answer(scope, binding.Provider, binding.Credential) != Answer(without, binding.Provider, binding.Credential))
                return false;
        return true;
    }

    private static ProviderBudgetHoldSchedule? Answer(
        IReadOnlyCollection<Goal> scope, string provider, string? credential) =>
        ProviderLimitSignals.TryGetProviderBudgetExhaustionHold(scope, provider, credential, out var hold)
            ? ProviderBudgetHoldSchedules.Evaluate(scope, hold, hold.PlacedAt)
            : null;
}
