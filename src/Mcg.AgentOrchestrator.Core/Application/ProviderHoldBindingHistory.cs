namespace Mcg.AgentOrchestrator.Core;

/// <summary>The dispatches and evidence that can affect one exact hold binding.</summary>
internal sealed class ProviderHoldBindingHistory(string key)
{
    internal sealed record Observation(Goal Goal, TaskSpec Task, TaskVerificationRecord Verification,
        TaskDispatchRecord Dispatch, ProviderHoldEvidence Evidence);
    internal sealed record Note(Goal Goal, ProgressEvent Event, int Index);

    internal string Key { get; } = key;
    internal List<(Goal Goal, TaskSpec Task, TaskDispatchRecord Dispatch)> Dispatches { get; } = [];
    internal List<Observation> Observations { get; } = [];
    internal List<(Goal Goal, DateTimeOffset At)> Recoveries { get; } = [];
    internal List<Note> Notes { get; } = [];
    private ILookup<TaskId?, Note>? _probeNotes;
    private (Note Note, ProviderBudgetHoldSchedules.ResetMarker Marker)[]? _resets;
    private HashSet<(TaskSpec, TaskDispatchRecord)>? _observationsByAttempt;
    internal int EvaluationCount { get; set; }
    internal int ObservationVisits { get; private set; }
    internal IReadOnlySet<(TaskSpec, TaskDispatchRecord)> ObservationsByAttempt =>
        _observationsByAttempt ??= ReadObservations().Select(item => (item.Task, item.Dispatch)).ToHashSet();

    internal IEnumerable<Observation> ReadObservations()
    {
        foreach (var observation in Observations)
        {
            ObservationVisits++;
            yield return observation;
        }
    }

    internal IEnumerable<Observation> HoldObservations() => ReadObservations().Where(item =>
        item.Evidence.Confirmed &&
        string.Equals(ProviderHoldBinding.From(item.Dispatch, item.Evidence.ScopedModel(item.Dispatch)).Key,
            Key, StringComparison.OrdinalIgnoreCase));

    internal Note? DispatchMarker(TaskSpec task, TaskDispatchRecord dispatch)
    {
        _probeNotes ??= Notes.Where(note => ProviderBudgetHoldSchedules.TryParseProbe(note.Event, out _))
            .OrderBy(note => note.Event.OccurredAt).ToLookup(note => note.Event.TaskId);
        return _probeNotes[task.Id].LastOrDefault(note => note.Event.OccurredAt <= dispatch.DispatchedAt);
    }

    internal ProviderBudgetHoldSchedules.ResetGeneration? LatestReset(
        DateTimeOffset? asOf = null, Note? before = null)
    {
        _resets ??= Notes.Select(note =>
                (Note: note, Marker: ProviderBudgetHoldSchedules.TryParseReset(note.Event, out var marker) ? marker : null))
            .Where(item => item.Marker is not null).Select(item => (item.Note, item.Marker!)).ToArray();
        return _resets.Where(item => (asOf is null || item.Note.Event.OccurredAt <= asOf) &&
                (before is null || item.Note.Goal != before.Goal || item.Note.Index < before.Index))
            .Select(item => new ProviderBudgetHoldSchedules.ResetGeneration(item.Marker, item.Note.Event.OccurredAt))
            .GroupBy(reset => reset.Marker.IntentId, StringComparer.Ordinal)
            .Select(group => group.MinBy(reset => reset.AppliedAt)!)
            .OrderBy(reset => reset.Marker.IssuedAt ?? reset.AppliedAt).ThenBy(reset => reset.AppliedAt).LastOrDefault();
    }

    internal static ProviderHoldBindingHistory Read(IEnumerable<Goal> goals, string key)
    {
        var history = new ProviderHoldBindingHistory(key);
        Populate(goals, candidate => candidate.Equals(key, StringComparison.OrdinalIgnoreCase) ? history : null);
        return history;
    }

    internal static void Populate(IEnumerable<Goal> goals, Func<string, ProviderHoldBindingHistory?> getHistory,
        Action<Goal>? visit = null, Action<Observation>? observe = null)
    {
        foreach (var goal in goals)
        {
            visit?.Invoke(goal);
            foreach (var task in goal.Tasks)
            {
                var attempts = new Dictionary<DateTimeOffset, TaskDispatchRecord>();
                foreach (var dispatch in task.DispatchHistory)
                {
                    attempts[dispatch.DispatchedAt] = dispatch;
                    foreach (var history in Histories(dispatch))
                        history.Dispatches.Add((goal, task, dispatch));
                }
                foreach (var verification in task.VerificationHistory)
                {
                    var dispatch = verification.DispatchAttemptAt is { } attempt
                        ? attempts.GetValueOrDefault(attempt) : ProviderHoldBinding.FindDispatch(task, verification);
                    if (dispatch is null) continue;
                    var observation = new Observation(goal, task, verification, dispatch, ProviderHoldEvidence.Read(verification));
                    observe?.Invoke(observation);
                    foreach (var history in Histories(dispatch)) history.Observations.Add(observation);
                }
                if (task.LatestProviderBudgetRecoveryAt is { } at &&
                    task.DispatchHistory.LastOrDefault(dispatch => dispatch.DispatchedAt <= at) is { } recovered)
                    foreach (var history in Histories(recovered)) history.Recoveries.Add((goal, at));
            }
            var timeline = goal.Timeline;
            for (var index = 0; index < timeline.Count; index++)
            {
                var evt = timeline[index];
                var binding = ProviderBudgetHoldSchedules.TryParseProbe(evt, out var probe) ? probe.BindingKey
                    : ProviderBudgetHoldSchedules.TryParseReset(evt, out var reset) ? reset.BindingKey
                    : ProviderBudgetHoldSchedules.TryParseStandaloneResult(evt, out var result) ? result.BindingKey : null;
                if (binding is not null) getHistory(binding)?.Notes.Add(new(goal, evt, index));
            }
        }

        IEnumerable<ProviderHoldBindingHistory> Histories(TaskDispatchRecord dispatch)
        {
            var binding = ProviderHoldBinding.From(dispatch);
            if (getHistory(binding.Key) is { } provider) yield return provider;
            if (dispatch.ModelName is { } model && getHistory((binding with { Model = model }).Key) is { } scoped)
                yield return scoped;
        }
    }
}
