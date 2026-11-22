namespace Mcg.AgentOrchestrator.Core;

using ResetGeneration = ProviderBudgetHoldSchedules.ResetGeneration;

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
    private Dictionary<TaskId, SortedList<DateTimeOffset, Note>>? _probeNotes;
    private (Note Note, ResetGeneration Generation)[]? _resets;
    private SortedList<DateTimeOffset, ResetGeneration>? _resetGenerations;
    private Dictionary<Goal, SortedList<DateTimeOffset, int>>? _resetNoteIndices;
    private SortedList<DateTimeOffset, (LatestAttempts Holds, LatestAttempts Quotas)>? _placements;
    private HashSet<DateTimeOffset>? _quotaPlacements;
    private HashSet<(TaskSpec, TaskDispatchRecord)>? _observationsByAttempt;
    internal int EvaluationCount { get; set; }
    internal int ObservationVisits { get; private set; }
    internal int ResetVisits { get; private set; }
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
        _probeNotes ??= Notes.Where(note => note.Event.TaskId is not null &&
                ProviderBudgetHoldSchedules.TryParseProbe(note.Event, out _))
            .OrderBy(note => note.Event.OccurredAt).GroupBy(note => note.Event.TaskId!)
            .ToDictionary(group => group.Key, group =>
            {
                var markers = new SortedList<DateTimeOffset, Note>();
                foreach (var note in group) markers[note.Event.OccurredAt] = note;
                return markers;
            });
        return _probeNotes.TryGetValue(task.Id, out var markers)
            ? AtOrBefore(markers, dispatch.DispatchedAt) : null;
    }

    internal ResetGeneration? LatestReset(DateTimeOffset? asOf = null, Note? before = null)
    {
        if (_resetGenerations is null) IndexResets();
        if (before is null || !_resetNoteIndices!.TryGetValue(before.Goal, out var indices) ||
            AtOrBefore(indices, asOf) <= before.Index)
            return AtOrBefore(_resetGenerations!, asOf);

        // A standalone result can precede a reset in the same timeline even when
        // their timestamps tie or arrive out of order. Preserve timeline precedence.
        return _resets!.Where(item =>
            {
                ResetVisits++;
                return (asOf is null || item.Generation.AppliedAt <= asOf) &&
                    (item.Note.Goal != before.Goal || item.Note.Index < before.Index);
            })
            .Select(item => item.Generation)
            .GroupBy(reset => reset.Marker.IntentId, StringComparer.Ordinal)
            .Select(group => group.MinBy(reset => reset.AppliedAt)!)
            .OrderBy(reset => reset.Marker.IssuedAt ?? reset.AppliedAt).ThenBy(reset => reset.AppliedAt).LastOrDefault();
    }

    private void IndexResets()
    {
        var resets = new List<(Note Note, ResetGeneration Generation)>();
        foreach (var note in Notes)
        {
            ResetVisits++;
            if (ProviderBudgetHoldSchedules.TryParseReset(note.Event, out var marker))
                resets.Add((note, new(marker, note.Event.OccurredAt)));
        }
        _resets = resets.ToArray();
        _resetGenerations = new();
        _resetNoteIndices = new();
        var byIntent = new Dictionary<string, (ResetGeneration Generation, int FirstNote)>(StringComparer.Ordinal);
        var ranked = new SortedSet<(DateTimeOffset Issued, DateTimeOffset Applied, int FirstNote)>();
        var generations = new Dictionary<int, ResetGeneration>();
        foreach (var (item, order) in _resets.Select((item, order) => (item, order))
            .OrderBy(entry => entry.item.Generation.AppliedAt))
        {
            ResetVisits++;
            var (note, reset) = item;
            if (!_resetNoteIndices.TryGetValue(note.Goal, out var indices))
                _resetNoteIndices.Add(note.Goal, indices = new());
            indices[reset.AppliedAt] = Math.Max(note.Index + 1, indices.Count == 0 ? 0 : indices.Values[^1]);

            var first = order;
            if (byIntent.TryGetValue(reset.Marker.IntentId, out var previous))
            {
                if (previous.FirstNote <= order) continue;
                ranked.Remove(Rank(previous.Generation, previous.FirstNote));
                generations.Remove(previous.FirstNote);
                reset = previous.Generation;
            }
            byIntent[reset.Marker.IntentId] = (reset, first);
            ranked.Add(Rank(reset, first));
            generations.Add(first, reset);
            _resetGenerations[item.Generation.AppliedAt] = generations[ranked.Max.FirstNote];
        }

        static (DateTimeOffset, DateTimeOffset, int) Rank(ResetGeneration reset, int firstNote) =>
            (reset.Marker.IssuedAt ?? reset.AppliedAt, reset.AppliedAt, firstNote);
    }

    internal bool HasCompetingPlacement(TaskSpec task, TaskDispatchRecord dispatch, DateTimeOffset placed,
        DateTimeOffset? asOf)
    {
        if (_placements is null) IndexPlacements();
        var (holds, quotas) = AtOrBefore(_placements!, asOf);
        return (_quotaPlacements!.Contains(placed) ? quotas : holds).Competes(task, dispatch, placed);
    }

    private void IndexPlacements()
    {
        _placements = new();
        _quotaPlacements = [];
        LatestAttempts holds = default, quotas = default;
        foreach (var group in HoldObservations().GroupBy(item => item.Verification.CompletedAt).OrderBy(group => group.Key))
        {
            // Equal timestamps share the preceding generation. Rejected old-generation
            // results cannot introduce a placement that invalidates later probes.
            var nextHolds = holds;
            var nextQuotas = quotas;
            foreach (var item in group)
            {
                ObservationVisits++;
                var note = DispatchMarker(item.Task, item.Dispatch);
                if (!ProviderBudgetHoldSchedules.MatchesGeneration(this, note, item.Dispatch, group.Key)) continue;
                if (ProviderBudgetHoldSchedules.ParseMarker(note)?.HoldPlacedAt is { } original &&
                    (_quotaPlacements.Contains(original) ? quotas : holds).Latest?.Verification.CompletedAt > original)
                    continue;
                nextHolds = nextHolds.Add(item);
                if (item.Evidence.Kind == ProviderFailureKind.BudgetExhausted)
                    nextQuotas = nextQuotas.Add(item);
            }
            holds = nextHolds;
            quotas = nextQuotas;
            if (quotas.Latest?.Verification.CompletedAt == group.Key) _quotaPlacements.Add(group.Key);
            _placements.Add(group.Key, (holds, quotas));
        }
    }

    private readonly record struct LatestAttempts(Observation? Latest, Observation? Other)
    {
        internal LatestAttempts Add(Observation item) =>
            new(item, SameAttempt(Latest, item.Task, item.Dispatch) ? Other : Latest);

        internal bool Competes(TaskSpec task, TaskDispatchRecord dispatch, DateTimeOffset placed) =>
            (SameAttempt(Latest, task, dispatch) ? Other : Latest)?.Verification.CompletedAt > placed;

        private static bool SameAttempt(Observation? item, TaskSpec task, TaskDispatchRecord dispatch) =>
            item is not null && item.Task == task && item.Dispatch == dispatch;
    }

    private static T? AtOrBefore<T>(SortedList<DateTimeOffset, T> values, DateTimeOffset? at)
    {
        var low = 0;
        var high = values.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (at is null || values.Keys[middle] <= at) low = middle + 1;
            else high = middle;
        }
        return low == 0 ? default : values.Values[low - 1];
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
