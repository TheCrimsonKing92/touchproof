using System.Text.Json;

namespace Mcg.AgentOrchestrator.Core;

using BindingKey = SubscriptionLimitEvidence.ProviderBindingKey;

internal sealed record ProviderHoldRetentionEvidence(Goal Goal, ProviderHoldRetentionEvidence.Input InputState)
{
    internal static ProviderHoldRetentionEvidence Read(Goal goal, ProviderHoldRetentionEvidence? previous = null)
    {
        var timeline = goal.Timeline;
        if (previous is not null && previous.InputState.Matches(goal, timeline))
            return previous with { Goal = goal };

        var tasks = goal.Tasks.Select(task => new TaskInput(task.Id, task.Status, task.AssignedAgentId,
            task.LatestProviderBudgetRecoveryAt, task.DispatchHistory.ToArray(), task.VerificationHistory.ToArray())).ToArray();
        return new(goal, new(goal.IsTerminal, tasks, timeline, ProviderHoldHistory.ExhaustedBindings([goal])));
    }

    private static bool SameHistory<T>(IReadOnlyList<T> current, IReadOnlyList<T> previous) =>
        ReferenceEquals(current, previous) ||
        current.Count == previous.Count && current.Zip(previous).All(pair =>
            EqualityComparer<T>.Default.Equals(pair.First, pair.Second) ||
            // Persisted reloads reconstruct nested receipt lists with fresh references.
            JsonElement.DeepEquals(JsonSerializer.SerializeToElement(pair.First),
                JsonSerializer.SerializeToElement(pair.Second)));

    internal sealed class Input
    {
        private readonly bool _terminal;
        private readonly TaskInput[] _tasks;
        private readonly IReadOnlyList<ProgressEvent> _timeline;
        private readonly HashSet<BindingKey> _dispatches = [];
        private readonly HashSet<BindingKey> _scopedMarkers = [];
        private readonly HashSet<string> _legacyMarkers = new(StringComparer.OrdinalIgnoreCase);

        internal IReadOnlyCollection<string> Groups { get; }
        internal IReadOnlyCollection<ProviderHoldBinding> Exhaustions { get; }

        internal Input(bool terminal, TaskInput[] tasks, IReadOnlyList<ProgressEvent> timeline,
            IReadOnlyCollection<ProviderHoldBinding> exhaustions)
        {
            _terminal = terminal;
            _tasks = tasks;
            _timeline = timeline;
            Exhaustions = exhaustions;

            foreach (var dispatch in tasks.SelectMany(task => task.Dispatches))
            {
                _dispatches.Add(ProviderHoldBinding.From(dispatch).ScopedKey);
                if (dispatch.ModelName is { } model)
                    _dispatches.Add(ProviderHoldBinding.From(dispatch, model).ScopedKey);
            }

            foreach (var evt in timeline)
            {
                if (ProviderBudgetHoldSchedules.ReadMarkerBinding(evt) is not { } marker) continue;
                if (marker.Identity is { } identity) _scopedMarkers.Add(identity);
                else _legacyMarkers.Add(marker.Text);
            }

            // Display keys can collide across identities. Legacy markers resolve
            // against evidence for all identities that share their display key.
            Groups = _dispatches.Concat(_scopedMarkers).Select(binding => binding.RenderedKey)
                .Concat(_legacyMarkers).Concat(exhaustions.Select(binding => binding.Key))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        internal bool HasDispatch(BindingKey key) => _dispatches.Contains(key);
        internal bool HasMarker(BindingKey key) => _scopedMarkers.Contains(key) || _legacyMarkers.Contains(key.RenderedKey);

        internal bool Matches(Goal goal, IReadOnlyList<ProgressEvent> timeline)
        {
            if (goal.IsTerminal != _terminal || goal.Tasks.Count != _tasks.Length ||
                !SameHistory(timeline, _timeline)) return false;
            for (var i = 0; i < _tasks.Length; i++)
                if (!_tasks[i].Matches(goal.Tasks[i])) return false;
            return true;
        }
    }

    internal sealed record TaskInput(TaskId Id, WorkTaskStatus Status, AgentId? AgentId, DateTimeOffset? RecoveryAt,
        TaskDispatchRecord[] Dispatches, TaskVerificationRecord[] Verifications)
    {
        internal bool Matches(TaskSpec task) =>
            Id == task.Id && Status == task.Status && AgentId == task.AssignedAgentId &&
            RecoveryAt == task.LatestProviderBudgetRecoveryAt &&
            SameHistory(task.DispatchHistory, Dispatches) && SameHistory(task.VerificationHistory, Verifications);
    }
}
