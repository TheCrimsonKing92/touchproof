using Mcg.AgentOrchestrator.Core;

// Retained-set oracle copied from ProviderHoldHistory at 98584b7e8.
internal sealed class ProviderHoldHistoryLegacyRetain
{
    internal delegate ProviderBudgetHoldSchedule? Evaluator(
        IReadOnlyCollection<Goal> scope, string provider, string? credential);

    private SortedDictionary<string, Goal> _retained = new(StringComparer.Ordinal);
    private readonly Evaluator _evaluate;

    public ProviderHoldHistoryLegacyRetain() : this(Answer) { }

    internal ProviderHoldHistoryLegacyRetain(Evaluator evaluator)
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
        var answers = new Dictionary<SubscriptionLimitEvidence.ProviderBindingKey, ProviderBudgetHoldSchedule?>();
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

    internal void Replace(IReadOnlyCollection<Goal> goals)
    {
        var retained = new SortedDictionary<string, Goal>(StringComparer.Ordinal);
        foreach (var goal in goals) retained.Add(goal.Id.Value, goal);
        _retained = retained;
    }

    private bool SameAnswers(Goal goal, IReadOnlyCollection<Goal> scope, IReadOnlyCollection<Goal> without,
        Dictionary<SubscriptionLimitEvidence.ProviderBindingKey, ProviderBudgetHoldSchedule?> answers,
        IReadOnlyCollection<ProviderHoldBinding> exhaustedBindings,
        out Dictionary<SubscriptionLimitEvidence.ProviderBindingKey, ProviderBudgetHoldSchedule?> withoutAnswers)
    {
        withoutAnswers = new();
        var dispatches = goal.Tasks.SelectMany(task => task.DispatchHistory).ToArray();
        var markers = goal.Timeline.Select(ProviderBudgetHoldSchedules.ReadMarkerBinding)
            .Where(marker => marker.HasValue).Select(marker => marker.GetValueOrDefault()).ToArray();
        // Removing a dispatch can make a legacy marker resolve to a different scope with the same text.
        var affectedTexts = exhaustedBindings.Where(binding => dispatches.Any(dispatch =>
                ProviderHoldBinding.Matches(dispatch, binding.ScopedKey)))
            .Select(binding => binding.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in exhaustedBindings)
        {
            var identity = binding.ScopedKey;
            if (!affectedTexts.Contains(binding.Key) &&
                !markers.Any(marker => marker.Identity is { } scoped ? scoped == identity :
                    marker.Text.Equals(binding.Key, StringComparison.OrdinalIgnoreCase))) continue;
            if (!answers.TryGetValue(identity, out var answer))
            {
                answer = Evaluate(scope, binding);
                answers.Add(identity, answer);
            }
            var withoutAnswer = Evaluate(without, binding);
            withoutAnswers.Add(identity, withoutAnswer);
            if (answer != withoutAnswer) return false;
        }
        return true;
    }

    private ProviderBudgetHoldSchedule? Evaluate(IReadOnlyCollection<Goal> scope, ProviderHoldBinding binding) =>
        binding.Model is null ? _evaluate(scope, binding.Provider, binding.Credential) :
            ProviderLimitSignals.TryGetExactHold(scope, binding, out var hold)
                ? ProviderBudgetHoldSchedules.Evaluate(scope, hold, hold.PlacedAt) : null;

    internal static IReadOnlyCollection<ProviderHoldBinding> ExhaustedBindings(
        IReadOnlyCollection<Goal> scope)
    {
        var bindings = new Dictionary<SubscriptionLimitEvidence.ProviderBindingKey, ProviderHoldBinding>();
        foreach (var task in scope.SelectMany(goal => goal.Tasks))
            foreach (var verification in task.VerificationHistory)
                if (ProviderHoldEvidence.Read(verification) is { Confirmed: true } evidence &&
                    ProviderHoldBinding.FindDispatch(task, verification) is { } dispatch)
                {
                    var binding = ProviderHoldBinding.From(dispatch, evidence.ScopedModel(dispatch));
                    bindings.TryAdd(binding.ScopedKey, binding);
                }
        return bindings.Values;
    }

    internal static ProviderBudgetHoldSchedule? Answer(
        IReadOnlyCollection<Goal> scope, string provider, string? credential) =>
        ProviderLimitSignals.TryGetProviderBudgetExhaustionHold(scope, provider, credential, out var hold)
            ? ProviderBudgetHoldSchedules.Evaluate(scope, hold, hold.PlacedAt)
            : null;
}
