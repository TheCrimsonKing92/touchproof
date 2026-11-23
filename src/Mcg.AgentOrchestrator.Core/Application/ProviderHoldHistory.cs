namespace Mcg.AgentOrchestrator.Core;

public sealed record ProviderHoldSeedAttempt(string GoalId, string ProviderName, DateTimeOffset Timestamp);

/// <summary>Read-only goal history retained while it changes a hold, diagnosis or probe schedule.</summary>
public sealed class ProviderHoldHistory
{
    internal delegate ProviderBudgetHoldSchedule? Evaluator(
        IReadOnlyCollection<Goal> scope, string provider, string? credential);

    private SortedDictionary<string, Goal> _retained = new(StringComparer.Ordinal);
    private readonly Evaluator _evaluate;

    internal static IReadOnlyList<string> HoldOutcomeRules { get; } =
    [
        TaskOutcomeRules.ProviderBudgetExhausted.Token,
        TaskOutcomeRules.ProviderAuthentication.Token,
        TaskOutcomeRules.SubscriptionLimit.Token,
        TaskOutcomeRules.ProviderRateLimit.Token,
        "recoverable-subscription-limit"
    ];

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

    public void CopyFrom(ProviderHoldHistory other)
    {
        Replace(other.Retained.ToArray());
    }

    internal void Replace(IReadOnlyCollection<Goal> goals)
    {
        var retained = new SortedDictionary<string, Goal>(StringComparer.Ordinal);
        foreach (var goal in goals) retained.Add(goal.Id.Value, goal);
        _retained = retained;
    }

    internal sealed record SeedSelection(IReadOnlySet<string> Sources, IReadOnlySet<string> Dependencies, int RowsRead,
        IReadOnlyDictionary<string, DateTimeOffset> LowerBounds);

    internal static SeedSelection SelectSeedEvidence(
        IReadOnlyList<ModelFitHistoryRow> rows, IReadOnlyList<ProviderHoldSeedAttempt> attempts,
        IReadOnlySet<string> workingGoalIds, IReadOnlyList<ProviderHoldSeedAttempt>? recoveries = null)
    {
        var lowerBounds = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        var sources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (!HoldOutcomeRules.Contains(row.OutcomeRule, StringComparer.Ordinal)) continue;
            AddSource(row.GoalId, row.ProviderName, row.Timestamp);
        }
        foreach (var attempt in attempts) AddSource(attempt.GoalId, attempt.ProviderName, attempt.Timestamp);

        var dependencies = rows.Where(row => row.IsCompleted || row.OutcomeClass == TaskOutcomeClass.Success)
            .Select(row => new ProviderHoldSeedAttempt(row.GoalId, row.ProviderName, row.Timestamp))
            .Concat(recoveries ?? []).Where(attempt =>
                lowerBounds.TryGetValue(attempt.ProviderName, out var first) && attempt.Timestamp >= first &&
                !sources.Contains(attempt.GoalId) && !workingGoalIds.Contains(attempt.GoalId))
            .Select(attempt => attempt.GoalId).ToHashSet(StringComparer.Ordinal);
        return new(sources, dependencies, rows.Count, lowerBounds);

        void AddSource(string goalId, string provider, DateTimeOffset at)
        {
            if (!lowerBounds.TryGetValue(provider, out var first) || at < first) lowerBounds[provider] = at;
            if (!workingGoalIds.Contains(goalId)) sources.Add(goalId);
        }
    }

    internal static IEnumerable<ProviderHoldSeedAttempt> SeedAttempts(Goal goal, Action<int>? recordDispatchVisits = null)
    {
        foreach (var task in goal.Tasks)
        {
            ProviderHoldBinding.DispatchIndex? attempts = null;
            foreach (var verification in task.VerificationHistory)
            {
                if (!ProviderHoldEvidence.Read(verification).Confirmed) continue;
                if (attempts is null)
                {
                    attempts = new();
                    foreach (var dispatch in task.DispatchHistory) attempts.Add(dispatch);
                }
                if (attempts.MatchVerification(verification) is { } matched)
                    yield return new(goal.Id.Value, matched.ProviderName ?? matched.WorkerName, verification.CompletedAt);
            }
            if (attempts is not null) recordDispatchVisits?.Invoke(attempts.Visits);
        }
    }

    internal static IEnumerable<ProviderHoldSeedAttempt> SeedRecoveryAttempts(Goal goal, Action<int>? recordDispatchVisits = null)
    {
        var recoveries = new List<ProviderHoldSeedAttempt>();
        ProviderHoldBindingHistory.Populate([goal], _ => null, observe: observation =>
        {
            if (ProviderLimitSignals.HasAvailabilityEvidence(observation.Verification))
                recoveries.Add(new(goal.Id.Value, observation.Dispatch.ProviderName ?? observation.Dispatch.WorkerName,
                    observation.Verification.CompletedAt));
        }, recordDispatchVisits: recordDispatchVisits);
        return recoveries;
    }

    public static IReadOnlyList<string> SeedCandidates(
        IEnumerable<ModelFitHistoryRow> rows, IReadOnlySet<string> workingGoalIds)
    {
        var history = rows.ToArray();
        var exhaustions = history.Where(row => HoldOutcomeRules.Contains(row.OutcomeRule, StringComparer.Ordinal)).ToArray();
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
        Dictionary<SubscriptionLimitEvidence.ProviderBindingKey, ProviderBudgetHoldSchedule?> answers,
        IReadOnlyCollection<ProviderHoldBinding> exhaustedBindings,
        out Dictionary<SubscriptionLimitEvidence.ProviderBindingKey, ProviderBudgetHoldSchedule?> withoutAnswers)
    {
        withoutAnswers = new();
        var dispatches = goal.Tasks.SelectMany(task => task.DispatchHistory).ToArray();
        var markers = goal.Timeline.Select(ProviderBudgetHoldSchedules.ReadMarkerBinding)
            .Where(marker => marker.HasValue).Select(marker => marker.GetValueOrDefault()).ToArray();
        foreach (var binding in exhaustedBindings)
        {
            var identity = binding.ScopedKey;
            if (!dispatches.Any(dispatch => ProviderHoldBinding.Matches(dispatch, identity)) &&
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
