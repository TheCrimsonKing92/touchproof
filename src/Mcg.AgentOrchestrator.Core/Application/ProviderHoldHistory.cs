namespace Mcg.AgentOrchestrator.Core;

/// <summary>Read-only goal history retained while it changes a hold, diagnosis or probe schedule.</summary>
public sealed class ProviderHoldHistory
{
    internal delegate ProviderBudgetHoldSchedule? Evaluator(
        IReadOnlyCollection<Goal> scope, string provider, string? credential);

    private readonly SortedDictionary<string, Goal> _retained = new(StringComparer.Ordinal);
    private readonly Evaluator _evaluate;

    public ProviderHoldHistory() : this(Answer) { }

    internal ProviderHoldHistory(Evaluator evaluator)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        _evaluate = evaluator;
    }

    public IReadOnlyCollection<Goal> Retained => _retained.Values;

    public IReadOnlyCollection<Goal> Scope(IReadOnlyCollection<Goal> workingSet)
    {
        if (_retained.Count == 0) return workingSet;
        var ids = workingSet.Select(goal => goal.Id.Value).ToHashSet(StringComparer.Ordinal);
        return workingSet.Concat(_retained.Values.Where(goal => !ids.Contains(goal.Id.Value))).ToArray();
    }

    public void Retain(Goal leaving, IReadOnlyCollection<Goal> workingSet) => Retain([leaving], workingSet);

    public void Retain(IReadOnlyCollection<Goal> leaving, IReadOnlyCollection<Goal> workingSet)
    {
        var working = workingSet.ToArray();
        var ids = working.Select(goal => goal.Id.Value).ToHashSet(StringComparer.Ordinal);
        foreach (var goal in leaving)
            if (!ids.Contains(goal.Id.Value)) _retained[goal.Id.Value] = goal;

        var scope = Scope(working);
        var answers = new Dictionary<(string Provider, string? Credential), ProviderBudgetHoldSchedule?>();
        var exhaustedBindings = ExhaustedBindings(scope);
        bool dropped;
        do
        {
            dropped = false;
            foreach (var goal in _retained.Values.ToArray())
            {
                // An ingested working-set copy already shadows the retained copy.
                if (ids.Contains(goal.Id.Value))
                {
                    _retained.Remove(goal.Id.Value);
                    dropped = true;
                    continue;
                }

                var without = scope.Where(item => item.Id != goal.Id).ToArray();
                if (!SameAnswers(goal, scope, without, answers, exhaustedBindings, out var withoutAnswers)) continue;
                _retained.Remove(goal.Id.Value);
                dropped = true;
                // Only answers computed over this exact smaller scope survive a removal.
                scope = without;
                answers = withoutAnswers;
                exhaustedBindings = ExhaustedBindings(scope);
            }
        } while (dropped);
    }

    public void CopyFrom(ProviderHoldHistory other)
    {
        var goals = other.Retained.ToArray();
        _retained.Clear();
        foreach (var goal in goals) _retained.Add(goal.Id.Value, goal);
    }

    public static IReadOnlyList<string> SeedCandidates(
        IEnumerable<ModelFitHistoryRow> rows, IReadOnlySet<string> workingGoalIds)
    {
        var history = rows.ToArray();
        var exhaustions = history.Where(row => string.Equals(
            row.OutcomeRule, TaskOutcomeRules.ProviderBudgetExhausted.Token, StringComparison.Ordinal)).ToArray();
        var candidates = exhaustions.Select(row => row.GoalId).ToHashSet(StringComparer.Ordinal);
        foreach (var provider in exhaustions.GroupBy(row => row.ProviderName, StringComparer.OrdinalIgnoreCase))
        {
            // Diagnosis, attempts and intervening recoveries all affect the projection. Load
            // the indexed candidates, then let Retain prune by the complete binding answer.
            var firstExhaustion = provider.Min(row => row.Timestamp);
            foreach (var row in history.Where(row =>
                    string.Equals(row.ProviderName, provider.Key, StringComparison.OrdinalIgnoreCase) &&
                    row.Timestamp >= firstExhaustion))
                candidates.Add(row.GoalId);
        }
        return candidates.Where(id => !workingGoalIds.Contains(id)).Order(StringComparer.Ordinal).ToArray();
    }

    private bool SameAnswers(Goal goal, IReadOnlyCollection<Goal> scope, IReadOnlyCollection<Goal> without,
        Dictionary<(string Provider, string? Credential), ProviderBudgetHoldSchedule?> answers,
        HashSet<SubscriptionLimitEvidence.ProviderBindingKey> exhaustedBindings,
        out Dictionary<(string Provider, string? Credential), ProviderBudgetHoldSchedule?> withoutAnswers)
    {
        withoutAnswers = new();
        var bindings = goal.Tasks.SelectMany(task => task.DispatchHistory)
            .Select(dispatch => (Provider: dispatch.ProviderName ?? dispatch.WorkerName,
                Credential: dispatch.ClaudeCredentialSourceDirectory)).Distinct();
        foreach (var binding in bindings)
        {
            // TryGetProviderBudgetExhaustionHold requires a matched exhaustion verification.
            // Without one on this binding, neither this scope nor any subset can hold it.
            if (!exhaustedBindings.Contains(SubscriptionLimitEvidence.BuildProviderBindingKey(
                    binding.Provider, binding.Credential))) continue;
            if (!answers.TryGetValue(binding, out var answer))
            {
                answer = _evaluate(scope, binding.Provider, binding.Credential);
                answers.Add(binding, answer);
            }
            var withoutAnswer = _evaluate(without, binding.Provider, binding.Credential);
            withoutAnswers.Add(binding, withoutAnswer);
            if (answer != withoutAnswer) return false;
        }
        return true;
    }

    private static HashSet<SubscriptionLimitEvidence.ProviderBindingKey> ExhaustedBindings(
        IReadOnlyCollection<Goal> scope)
    {
        var bindings = new HashSet<SubscriptionLimitEvidence.ProviderBindingKey>();
        foreach (var task in scope.SelectMany(goal => goal.Tasks))
            foreach (var verification in task.VerificationHistory)
                if (verification.ProviderFailureKind == ProviderFailureKind.BudgetExhausted &&
                    SubscriptionLimitEvidence.FindDispatchForVerification(task, verification) is { } dispatch)
                    bindings.Add(SubscriptionLimitEvidence.BuildProviderBindingKey(
                        dispatch.ProviderName ?? dispatch.WorkerName, dispatch.ClaudeCredentialSourceDirectory));
        return bindings;
    }

    internal static ProviderBudgetHoldSchedule? Answer(
        IReadOnlyCollection<Goal> scope, string provider, string? credential) =>
        ProviderLimitSignals.TryGetProviderBudgetExhaustionHold(scope, provider, credential, out var hold)
            ? ProviderBudgetHoldSchedules.Evaluate(scope, hold, hold.PlacedAt)
            : null;
}
