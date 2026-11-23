namespace Mcg.AgentOrchestrator.Core;

using ProviderBindingKey = SubscriptionLimitEvidence.ProviderBindingKey;

internal sealed record ProviderHoldSeedEvaluation(
    ProviderBudgetHoldSchedule? Schedule, int Count, int ObservationVisits, int ResetVisits, int DispatchVisits);

internal sealed record ProviderHoldSeedIndexResult(
    IReadOnlySet<string> RequiredGoals,
    IReadOnlyDictionary<ProviderBindingKey, ProviderHoldSeedEvaluation> Evaluations,
    int IndexedGoals,
    int DispatchMatchingVisits);

internal static class ProviderHoldSeedIndex
{
    internal static ProviderHoldSeedIndexResult Evaluate(
        IReadOnlyCollection<Goal> working, IReadOnlyCollection<Goal> retained, IReadOnlyCollection<Goal> persisted,
        IReadOnlySet<string> sources)
    {
        var workingIds = working.Select(goal => goal.Id.Value).ToHashSet(StringComparer.Ordinal);
        var persistedById = persisted.ToDictionary(goal => goal.Id.Value, StringComparer.Ordinal);
        foreach (var goal in retained) persistedById.TryAdd(goal.Id.Value, goal);
        var sourceIds = workingIds.Concat(sources).Concat(retained.Select(goal => goal.Id.Value))
            .ToHashSet(StringComparer.Ordinal);
        var scope = working.Concat(persistedById.Values.Where(goal => !workingIds.Contains(goal.Id.Value))
            .OrderBy(goal => goal.Id.Value, StringComparer.Ordinal));

        var buckets = new Dictionary<ProviderBindingKey, ProviderHoldBindingHistory>();
        var indexedGoals = 0;
        ProviderHoldBindingHistory Bucket(ProviderBindingKey key)
        {
            if (!buckets.TryGetValue(key, out var bucket)) buckets.Add(key, bucket = new(key.RenderedKey, key));
            return bucket;
        }
        var bindings = new HashSet<ProviderBindingKey>();
        var dispatchMatchingVisits = 0;
        ProviderHoldBindingHistory.Populate(scope, Bucket, _ => indexedGoals++, observation =>
        {
            if (!observation.Evidence.Confirmed || !sourceIds.Contains(observation.Goal.Id.Value)) return;
            var binding = ProviderHoldBinding.From(observation.Dispatch, observation.Evidence.ScopedModel(observation.Dispatch));
            bindings.Add(binding.ScopedKey);
        }, visits => dispatchMatchingVisits += visits, addNote: (_, key, note) =>
        {
            if (key is not null && buckets.TryGetValue(key, out var bucket)) bucket.Notes.Add(note);
        });

        var evaluations = new Dictionary<ProviderBindingKey, ProviderHoldSeedEvaluation>();
        var required = new HashSet<string>(StringComparer.Ordinal);
        var clearedBindingsBySource = new Dictionary<string, List<ProviderBindingKey>>(StringComparer.Ordinal);
        var clearingEvidence = new Dictionary<ProviderBindingKey, string[]>();
        foreach (var key in bindings)
        {
            var bucket = buckets[key];
            IReadOnlyList<ProviderHoldBindingHistory.Observation> currentHolds = [];
            var schedule = ProviderLimitSignals.TryGetExactHold(bucket, key, out var hold)
                ? ProviderBudgetHoldSchedules.Evaluate(bucket, hold, out currentHolds) : null;
            // Probe and reset generations can refer to an earlier placement. For an
            // ordinary recovery, superseded evidence cannot affect the current schedule.
            var sourceGoals = (schedule is null || bucket.Notes.Count > 0 ? bucket.HoldObservations() : currentHolds)
                .Select(item => item.Goal.Id.Value)
                .Distinct(StringComparer.Ordinal).ToArray();
            var dependencies = bucket.ReadObservations()
                .Where(item => ProviderLimitSignals.HasAvailabilityEvidence(item.Verification))
                .Select(item => item.Goal.Id.Value)
                .Concat(bucket.Notes.Select(note => note.Goal.Id.Value))
                .Concat(bucket.Recoveries.Values.Select(recovery => recovery.Goal.Id.Value));
            if (bucket.Notes.Any(note => ProviderBudgetHoldSchedules.TryParseProbe(note.Event, out _)))
                dependencies = dependencies.Concat(bucket.Dispatches.Select(attempt => attempt.Goal.Id.Value));
            var dependencyIds = dependencies.Distinct(StringComparer.Ordinal).ToArray();
            if (schedule is not null)
            {
                required.UnionWith(sourceGoals);
                required.UnionWith(dependencyIds);
            }
            else
            {
                // A cleared key only needs its witnesses if a source survives via working
                // state or another binding. Retaining the witness can itself keep a source.
                clearingEvidence.Add(key, dependencyIds);
                foreach (var source in sourceGoals)
                {
                    if (!clearedBindingsBySource.TryGetValue(source, out var keys))
                        clearedBindingsBySource.Add(source, keys = []);
                    keys.Add(key);
                }
            }
            evaluations.Add(key, new(schedule, bucket.EvaluationCount, bucket.ObservationVisits,
                bucket.ResetVisits, bucket.DispatchVisits));
        }

        required.UnionWith(workingIds);
        var queue = new Queue<string>(required);
        while (queue.TryDequeue(out var goalId))
            if (clearedBindingsBySource.TryGetValue(goalId, out var keys))
                foreach (var key in keys)
                    if (clearingEvidence.Remove(key, out var witnesses))
                        foreach (var witness in witnesses)
                            if (required.Add(witness)) queue.Enqueue(witness);
        required.ExceptWith(workingIds);
        return new(required, evaluations, indexedGoals, dispatchMatchingVisits);
    }
}
