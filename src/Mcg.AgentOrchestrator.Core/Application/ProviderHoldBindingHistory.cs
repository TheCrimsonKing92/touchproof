namespace Mcg.AgentOrchestrator.Core;

using ResetGeneration = ProviderBudgetHoldSchedules.ResetGeneration;
using ProviderBindingKey = SubscriptionLimitEvidence.ProviderBindingKey;

/// <summary>The dispatches and evidence that can affect one exact hold binding.</summary>
internal sealed class ProviderHoldBindingHistory(string key, ProviderBindingKey? identity = null)
{
    internal sealed record Observation(Goal Goal, TaskSpec Task, TaskVerificationRecord Verification,
        TaskDispatchRecord Dispatch, ProviderHoldEvidence Evidence);
    internal sealed record Note(Goal Goal, ProgressEvent Event, int Index);

    internal sealed class VerifiedAvailability
    {
        private readonly Dictionary<(TaskSpec, TaskDispatchRecord), DateTimeOffset> _completed = [];
        private DateTimeOffset? _latestStart;

        internal VerifiedAvailability(IEnumerable<Observation> observations)
        {
            foreach (var (_, task, verification, dispatch, _) in observations)
            {
                var key = (task, dispatch);
                if (!_completed.TryGetValue(key, out var completed) || verification.CompletedAt > completed)
                    _completed[key] = verification.CompletedAt;
                if (_latestStart is null || dispatch.DispatchedAt > _latestStart)
                    _latestStart = dispatch.DispatchedAt;
            }
        }

        internal bool Supersedes(Observation refusal) =>
            _latestStart >= refusal.Verification.CompletedAt ||
            _completed.TryGetValue((refusal.Task, refusal.Dispatch), out var completed) &&
            completed > refusal.Verification.CompletedAt;
    }

    internal string Key { get; } = key;
    internal List<(Goal Goal, TaskSpec Task, TaskDispatchRecord Dispatch)> Dispatches { get; } = [];
    internal List<Observation> Observations { get; } = [];
    internal Dictionary<TaskSpec, (Goal Goal, DateTimeOffset At)> Recoveries { get; } = [];
    internal List<Note> Notes { get; } = [];
    private Dictionary<TaskId, SortedList<DateTimeOffset, Note>>? _probeNotes;
    private Dictionary<(Goal Goal, TaskId TaskId), List<(TaskSpec Task, TaskDispatchRecord Dispatch)>>? _probeDispatches;
    private ResetEntry[]? _resetsByTime;
    private SortedList<DateTimeOffset, (ResetGeneration? Latest, int Count)>? _resetGenerations;
    private Dictionary<Goal, SortedList<DateTimeOffset, int>>? _resetNoteIndices;
    private SortedList<DateTimeOffset, (LatestAttempts Holds, LatestAttempts Quotas)>? _placements;
    private HashSet<DateTimeOffset>? _quotaPlacements;
    private HashSet<(TaskSpec, TaskDispatchRecord)>? _observationsByAttempt;
    internal int EvaluationCount { get; set; }
    internal int ObservationVisits { get; private set; }
    internal int ResetVisits { get; private set; }
    internal int DispatchVisits { get; private set; }
    internal bool HasExplicitRecovery(TaskSpec task, DateTimeOffset exhaustedAt)
    {
        DispatchVisits++;
        return Recoveries.TryGetValue(task, out var recovery) && recovery.At > exhaustedAt;
    }
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

    internal IEnumerable<Observation> HoldObservations() => ReadObservations().Where(IsHoldObservation);

    internal bool IsHoldObservation(Observation item)
    {
        if (!item.Evidence.Confirmed) return false;
        var binding = ProviderHoldBinding.From(item.Dispatch, item.Evidence.ScopedModel(item.Dispatch));
        return identity is null ? binding.Key.Equals(Key, StringComparison.OrdinalIgnoreCase) : binding.ScopedKey == identity;
    }

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

    internal (TaskSpec Task, TaskDispatchRecord Dispatch)? FindProbeDispatch(Note probe)
    {
        if (probe.Event.TaskId is not { } taskId) return null;
        if (_probeDispatches is null)
        {
            _probeDispatches = [];
            foreach (var (goal, task, dispatch) in Dispatches)
            {
                DispatchVisits++;
                if (!_probeDispatches.TryGetValue((goal, task.Id), out var attempts))
                    _probeDispatches.Add((goal, task.Id), attempts = []);
                // Only a new timestamp maximum can be the first match in stored order.
                if (attempts.Count == 0 || dispatch.DispatchedAt > attempts[^1].Dispatch.DispatchedAt)
                    attempts.Add((task, dispatch));
            }
        }

        if (!_probeDispatches.TryGetValue((probe.Goal, taskId), out var candidates)) return null;
        var low = 0;
        var high = candidates.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            DispatchVisits++;
            if (candidates[middle].Dispatch.DispatchedAt < probe.Event.OccurredAt) low = middle + 1;
            else high = middle;
        }
        return low < candidates.Count ? candidates[low] : null;
    }

    internal ResetGeneration? LatestReset(DateTimeOffset? asOf = null)
    {
        if (_resetGenerations is null) IndexResets();
        return AtOrBefore(_resetGenerations!, asOf).Latest;
    }

    internal IReadOnlyDictionary<Note, ResetGeneration?> ResetsBefore(IEnumerable<Note> results)
    {
        if (_resetGenerations is null) IndexResets();
        var answers = new Dictionary<Note, ResetGeneration?>();
        var timelineQueries = new List<(Note Note, int Count)>();
        foreach (var result in results)
        {
            var at = result.Event.OccurredAt;
            var (latest, count) = AtOrBefore(_resetGenerations!, at);
            if (_resetNoteIndices!.TryGetValue(result.Goal, out var indices) && AtOrBefore(indices, at) > result.Index)
                timelineQueries.Add((result, count));
            else
                answers[result] = latest;
        }
        if (timelineQueries.Count > 0) ResolveTimelineResets(timelineQueries, answers);
        return answers;
    }

    private void ResolveTimelineResets(List<(Note Note, int Count)> queries, Dictionary<Note, ResetGeneration?> answers)
    {
        var resets = _resetsByTime!;
        var timeline = new ResetEntry[resets.Length];
        foreach (var reset in resets)
        {
            ResetVisits++;
            timeline[reset.Order] = reset;
        }
        var byGoal = new Dictionary<Goal, List<ResetEntry>>();
        foreach (var reset in timeline)
        {
            ResetVisits++;
            if (!byGoal.TryGetValue(reset.Note.Goal, out var goalTimeline))
                byGoal.Add(reset.Note.Goal, goalTimeline = []);
            goalTimeline.Add(reset);
        }
        foreach (var goalTimeline in byGoal.Values)
            goalTimeline.Sort(static (left, right) => left.Note.Index.CompareTo(right.Note.Index));
        var active = new ActiveResets(timeline);
        var visibleByTime = 0;
        Note? before = null;
        IReadOnlyList<ResetEntry> goalResets = [];
        var excluded = 0;

        // A reset must be visible by timestamp and must precede the result in its
        // own goal. Group nearby timestamp cutoffs, then sweep each timeline in
        // order within the group. Within a group, time moves only across that
        // group's cutoffs, and each goal's excluded suffix moves in one direction.
        var blockSize = (int)Math.Ceiling(resets.Length / Math.Sqrt(queries.Count));
        foreach (var (result, cutoff) in queries.OrderBy(query => query.Count / blockSize)
            .ThenBy(query => query.Note.Goal.Id.Value, StringComparer.Ordinal).ThenBy(query => query.Note.Index))
        {
            while (visibleByTime < cutoff) MoveTime(resets[visibleByTime++], add: true);
            while (visibleByTime > cutoff) MoveTime(resets[--visibleByTime], add: false);
            if (before?.Goal != result.Goal)
            {
                while (excluded < goalResets.Count) MoveTimeline(goalResets[excluded++], add: true, result);
                goalResets = byGoal[result.Goal];
                excluded = goalResets.Count;
            }
            while (excluded > 0 && goalResets[excluded - 1].Note.Index >= result.Index)
                MoveTimeline(goalResets[--excluded], add: false, result);
            while (excluded < goalResets.Count && goalResets[excluded].Note.Index < result.Index)
                MoveTimeline(goalResets[excluded++], add: true, result);
            before = result;
            answers[result] = active.Latest;
        }

        void MoveTime(ResetEntry entry, bool add)
        {
            ResetVisits++;
            if (before is null || before.Goal != entry.Note.Goal || entry.Note.Index < before.Index)
                active.Update(entry, add);
        }

        void MoveTimeline(ResetEntry entry, bool add, Note result)
        {
            ResetVisits++;
            if (entry.Generation.AppliedAt <= result.Event.OccurredAt)
                active.Update(entry, add);
        }
    }

    private void IndexResets()
    {
        var resets = new List<ResetEntry>();
        foreach (var note in Notes)
        {
            ResetVisits++;
            if (ProviderBudgetHoldSchedules.TryParseReset(note.Event, out var marker))
                resets.Add(new(note, new(marker, note.Event.OccurredAt), resets.Count));
        }
        _resetsByTime = resets.OrderBy(entry => entry.Generation.AppliedAt).ToArray();
        _resetGenerations = new();
        _resetNoteIndices = new();
        var active = new ActiveResets(resets);
        for (var i = 0; i < _resetsByTime.Length; i++)
        {
            ResetVisits++;
            var entry = _resetsByTime[i];
            var (note, reset, _) = entry;
            if (!_resetNoteIndices.TryGetValue(note.Goal, out var indices))
                _resetNoteIndices.Add(note.Goal, indices = new());
            indices[reset.AppliedAt] = Math.Max(note.Index + 1, indices.Count == 0 ? 0 : indices.Values[^1]);
            active.Update(entry, add: true);
            _resetGenerations[reset.AppliedAt] = (active.Latest, i + 1);
        }
    }

    private readonly record struct ResetEntry(Note Note, ResetGeneration Generation, int Order);

    private sealed class ActiveResets(IReadOnlyList<ResetEntry> timeline)
    {
        private sealed class IntentResets
        {
            internal SortedSet<(DateTimeOffset Applied, int Order)> ByTime { get; } = [];
            internal SortedSet<int> ByNote { get; } = [];
        }

        private readonly Dictionary<string, IntentResets> _byIntent = new(StringComparer.Ordinal);
        private readonly SortedSet<(DateTimeOffset Issued, DateTimeOffset Applied, int FirstNote, int EarliestReset)> _ranked = [];

        internal ResetGeneration? Latest => _ranked.Count == 0 ? null : timeline[_ranked.Max.EarliestReset].Generation;

        internal void Update(ResetEntry entry, bool add)
        {
            var reset = entry.Generation;
            if (!_byIntent.TryGetValue(reset.Marker.IntentId, out var intent))
                _byIntent.Add(reset.Marker.IntentId, intent = new());
            if (intent.ByNote.Count > 0) _ranked.Remove(Rank(intent));
            if (add)
            {
                intent.ByTime.Add((reset.AppliedAt, entry.Order));
                intent.ByNote.Add(entry.Order);
            }
            else
            {
                intent.ByTime.Remove((reset.AppliedAt, entry.Order));
                intent.ByNote.Remove(entry.Order);
            }
            if (intent.ByNote.Count > 0) _ranked.Add(Rank(intent));
        }

        private (DateTimeOffset Issued, DateTimeOffset Applied, int FirstNote, int EarliestReset) Rank(IntentResets intent)
        {
            // Replays cannot refresh an intent. The first note breaks ties between intents.
            var (applied, order) = intent.ByTime.Min;
            return (timeline[order].Generation.Marker.IssuedAt ?? applied, applied, intent.ByNote.Min, order);
        }
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

    internal static ProviderHoldBindingHistory Read(IEnumerable<Goal> goals, string key) => Read(goals, key, null);

    internal static ProviderHoldBindingHistory Read(IEnumerable<Goal> goals, ProviderBindingKey binding) =>
        Read(goals, binding.RenderedKey, binding);

    private static ProviderHoldBindingHistory Read(IEnumerable<Goal> goals, string key, ProviderBindingKey? binding)
    {
        var history = new ProviderHoldBindingHistory(key, binding);
        if (binding is null)
            Populate(goals, candidate => candidate.RenderedKey.Equals(key, StringComparison.OrdinalIgnoreCase) ? history : null,
                addNote: (text, _, note) =>
                {
                    if (text.Equals(key, StringComparison.OrdinalIgnoreCase)) history.Notes.Add(note);
                });
        else
            Populate(goals, candidate => candidate == binding ? history : null,
                addNote: (_, candidate, note) =>
                {
                    if (candidate == binding) history.Notes.Add(note);
                });
        return history;
    }

    internal static void Populate(IEnumerable<Goal> goals, Func<ProviderBindingKey, ProviderHoldBindingHistory?> getHistory,
        Action<Goal>? visit = null, Action<Observation>? observe = null, Action<int>? recordDispatchVisits = null,
        Action<string, ProviderBindingKey?, Note>? addNote = null)
    {
        var bindingsByText = new Dictionary<string, HashSet<ProviderBindingKey>>(StringComparer.OrdinalIgnoreCase);
        var pendingNotes = new List<(string Text, ProviderBindingKey? Identity, Note Note,
            Dictionary<ProviderBindingKey, DateTimeOffset>? TaskBindings)>();
        foreach (var goal in goals)
        {
            visit?.Invoke(goal);
            var notes = new List<(string Text, ProviderBindingKey? Identity, Note Note)>();
            var sourceTasks = new Dictionary<TaskId, Dictionary<ProviderBindingKey, DateTimeOffset>>();
            if (addNote is not null)
            {
                var timeline = goal.Timeline;
                for (var index = 0; index < timeline.Count; index++)
                {
                    var evt = timeline[index];
                    (string Text, ProviderBindingKey? Identity)? marker =
                        ProviderBudgetHoldSchedules.TryParseProbe(evt, out var probe) ? (probe.BindingKey, probe.BindingIdentity)
                        : ProviderBudgetHoldSchedules.TryParseReset(evt, out var reset) ? (reset.BindingKey, reset.BindingIdentity)
                        : ProviderBudgetHoldSchedules.TryParseStandaloneResult(evt, out var result) ? (result.BindingKey, result.BindingIdentity)
                        : null;
                    if (marker is not { } parsed) continue;
                    notes.Add((parsed.Text, parsed.Identity, new(goal, evt, index)));
                    if (parsed.Identity is null && evt.TaskId is { } taskId && !sourceTasks.ContainsKey(taskId))
                        sourceTasks.Add(taskId, []);
                }
            }

            foreach (var task in goal.Tasks)
            {
                var attempts = new ProviderHoldBinding.DispatchIndex();
                foreach (var dispatch in task.DispatchHistory)
                {
                    attempts.Add(dispatch);
                    foreach (var history in Histories(dispatch))
                        history.Dispatches.Add((goal, task, dispatch));
                }
                foreach (var verification in task.VerificationHistory)
                {
                    var dispatch = attempts.MatchVerification(verification);
                    if (dispatch is null) continue;
                    var observation = new Observation(goal, task, verification, dispatch, ProviderHoldEvidence.Read(verification));
                    observe?.Invoke(observation);
                    foreach (var history in Histories(dispatch)) history.Observations.Add(observation);
                    if (addNote is not null && observation.Evidence.Confirmed)
                    {
                        var binding = ProviderHoldBinding.From(dispatch, observation.Evidence.ScopedModel(dispatch)).ScopedKey;
                        var text = binding.RenderedKey;
                        if (!bindingsByText.TryGetValue(text, out var matches))
                            bindingsByText.Add(text, matches = []);
                        matches.Add(binding);
                        if (sourceTasks.TryGetValue(task.Id, out var taskBindings) &&
                            (!taskBindings.TryGetValue(binding, out var first) || verification.CompletedAt < first))
                            taskBindings[binding] = verification.CompletedAt;
                    }
                }
                if (task.LatestProviderBudgetRecoveryAt is { } at &&
                    attempts.LastStoredAtOrBefore(at) is { } recovered)
                    foreach (var history in Histories(recovered)) history.Recoveries[task] = (goal, at);
                recordDispatchVisits?.Invoke(attempts.Visits);
            }
            foreach (var (text, identity, note) in notes)
                pendingNotes.Add((text, identity, note,
                    note.Event.TaskId is { } taskId ? sourceTasks.GetValueOrDefault(taskId) : null));
        }

        foreach (var (text, explicitIdentity, note, taskBindings) in pendingNotes)
        {
            if (explicitIdentity is not null)
            {
                addNote!(text, explicitIdentity, note);
                continue;
            }
            // Old markers carry display text. Prefer an unambiguous refusal on
            // the marker's source task, or an unambiguous binding in the history.
            var local = taskBindings?.Where(pair => pair.Value <= note.Event.OccurredAt &&
                    pair.Key.RenderedKey.Equals(text, StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Key).Take(2).ToArray() ?? [];
            var identity = local.Length == 1 ? local[0] :
                local.Length == 0 && bindingsByText.TryGetValue(text, out var matches) && matches.Count == 1
                    ? matches.Single() : null;
            addNote!(text, identity, note);
        }

        IEnumerable<ProviderHoldBindingHistory> Histories(TaskDispatchRecord dispatch)
        {
            var binding = ProviderHoldBinding.From(dispatch);
            if (getHistory(binding.ScopedKey) is { } provider) yield return provider;
            if (dispatch.ModelName is { } model && getHistory((binding with { Model = model }).ScopedKey) is { } scoped)
                yield return scoped;
        }
    }
}
