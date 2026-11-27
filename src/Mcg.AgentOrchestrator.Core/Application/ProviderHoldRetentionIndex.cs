namespace Mcg.AgentOrchestrator.Core;

using BindingKey = SubscriptionLimitEvidence.ProviderBindingKey;
using Evidence = ProviderHoldRetentionEvidence;

/// <summary>Indexes the immutable retained inputs while the eviction path finds the legacy fixed point.</summary>
internal sealed class ProviderHoldRetentionIndex(
    SortedDictionary<string, Goal> retained, Func<IReadOnlyCollection<Goal>, ProviderHoldBinding, ProviderBudgetHoldSchedule?> evaluate)
{
    private readonly Dictionary<string, Evidence> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SortedDictionary<string, Evidence>> _retainedGroups = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, Evidence> _workingById = new(StringComparer.Ordinal);
    private Dictionary<string, List<Evidence>> _workingGroups = new(StringComparer.OrdinalIgnoreCase);
    private bool _settled;
    private int _evaluations;
    private int _goalVisits;

    internal (int BindingEvaluations, int GoalVisits) Retain(
        IReadOnlyCollection<Goal> leaving, IReadOnlyCollection<Goal> workingSet)
    {
        _evaluations = 0;
        _goalVisits = 0;
        var dirty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new SortedSet<string>(StringComparer.Ordinal);
        if (!_settled)
        {
            // A startup seed or copied history has no fixed point for these working inputs.
            foreach (var goal in retained.Values) Add(goal, dirty, candidates);
        }

        UpdateWorking(workingSet, dirty);
        foreach (var id in _workingById.Keys)
        {
            if (!Remove(id, dirty)) continue;
            retained.Remove(id);
            candidates.Remove(id);
        }

        foreach (var goal in leaving)
        {
            var id = goal.Id.Value;
            if (_workingById.ContainsKey(id)) continue;
            Remove(id, dirty);
            candidates.Remove(id);
            retained[id] = goal;
            Add(goal, dirty, candidates);
        }

        // A goal can be kept by a second binding and later become removable
        // there. Close over these dependencies before running the greedy pass.
        var pending = new Queue<string>(dirty);
        while (pending.TryDequeue(out var key))
        {
            if (!_retainedGroups.TryGetValue(key, out var group)) continue;
            foreach (var (id, item) in group)
            {
                _goalVisits++;
                if (!candidates.Add(id)) continue;
                foreach (var dependency in item.InputState.Groups)
                    if (dirty.Add(dependency)) pending.Enqueue(dependency);
            }
        }

        var active = new Dictionary<string, BindingGroup>(StringComparer.OrdinalIgnoreCase);
        bool dropped;
        do
        {
            dropped = false;
            foreach (var id in candidates.ToArray())
            {
                _goalVisits++;
                var item = _entries[id];
                var groups = item.InputState.Groups.Select(Group).ToArray();
                var answers = new Dictionary<BindingKey, ProviderBudgetHoldSchedule?>?[groups.Length];
                for (var i = 0; i < groups.Length; i++)
                {
                    answers[i] = groups[i].Without(item);
                    if (answers[i] is null) break;
                }
                if (answers.Any(answer => answer is null)) continue;

                Remove(id);
                retained.Remove(id);
                candidates.Remove(id);
                for (var i = 0; i < groups.Length; i++) groups[i].AfterRemoval(answers[i]!);
                dropped = true;
            }
        } while (dropped);

        _settled = true;
        return (_evaluations, _goalVisits);

        BindingGroup Group(string key)
        {
            if (!active.TryGetValue(key, out var group))
            {
                group = new(this, key, _workingGroups.GetValueOrDefault(key) ?? [], _retainedGroups[key]);
                active.Add(key, group);
            }
            return group;
        }
    }

    private void UpdateWorking(IReadOnlyCollection<Goal> workingSet, HashSet<string> dirty)
    {
        var byId = new Dictionary<string, Evidence>(StringComparer.Ordinal);
        var groups = new Dictionary<string, List<Evidence>>(StringComparer.OrdinalIgnoreCase);
        foreach (var goal in workingSet)
        {
            _goalVisits++;
            var id = goal.Id.Value;
            var item = Evidence.Read(goal, _workingById.GetValueOrDefault(id));
            byId[id] = item;
            foreach (var key in item.InputState.Groups)
            {
                if (!groups.TryGetValue(key, out var group)) groups.Add(key, group = []);
                group.Add(item);
            }
        }

        foreach (var (key, group) in groups)
            if (!_workingGroups.TryGetValue(key, out var previous) ||
                !group.Select(item => item.InputState).SequenceEqual(previous.Select(item => item.InputState)))
                dirty.Add(key);
        foreach (var key in _workingGroups.Keys)
            if (!groups.ContainsKey(key)) dirty.Add(key);

        _workingById = byId;
        _workingGroups = groups;
    }

    private void Add(Goal goal, HashSet<string> dirty, SortedSet<string> candidates)
    {
        _goalVisits++;
        var item = Evidence.Read(goal);
        var id = goal.Id.Value;
        _entries.Add(id, item);
        if (item.InputState.Groups.Count == 0) candidates.Add(id);
        foreach (var key in item.InputState.Groups)
        {
            if (!_retainedGroups.TryGetValue(key, out var group))
                _retainedGroups.Add(key, group = new(StringComparer.Ordinal));
            group.Add(id, item);
            dirty.Add(key);
        }
    }

    private bool Remove(string id, HashSet<string>? dirty = null)
    {
        if (!_entries.Remove(id, out var item)) return false;
        foreach (var key in item.InputState.Groups)
        {
            var group = _retainedGroups[key];
            group.Remove(id);
            if (group.Count == 0) _retainedGroups.Remove(key);
            dirty?.Add(key);
        }
        return true;
    }

    private ProviderBudgetHoldSchedule? Evaluate(ProviderHoldBinding binding, Goal[] scope)
    {
        _evaluations++;
        return evaluate(scope, binding);
    }

    private sealed class BindingGroup(ProviderHoldRetentionIndex owner, string key,
        List<Evidence> working, SortedDictionary<string, Evidence> archived)
    {
        private ProviderHoldBinding[]? _exhausted;
        private Dictionary<BindingKey, ProviderBudgetHoldSchedule?> _answers = [];
        private Goal[]? _scope;

        internal Dictionary<BindingKey, ProviderBudgetHoldSchedule?>? Without(Evidence item)
        {
            _exhausted ??= ReadEntries().SelectMany(entry => entry.InputState.Exhaustions)
                .Where(binding => binding.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                .DistinctBy(binding => binding.ScopedKey).ToArray();
            var dispatchAffectsGroup = _exhausted.Any(binding => item.InputState.HasDispatch(binding.ScopedKey));
            var answers = new Dictionary<BindingKey, ProviderBudgetHoldSchedule?>();
            Goal[]? without = null;
            foreach (var binding in _exhausted)
            {
                var identity = binding.ScopedKey;
                if (!dispatchAffectsGroup && !item.InputState.HasMarker(identity)) continue;
                _scope ??= ReadEntries().Select(entry => entry.Goal).ToArray();
                if (!_answers.TryGetValue(identity, out var answer))
                    _answers.Add(identity, answer = owner.Evaluate(binding, _scope));
                without ??= _scope.Where(goal => goal.Id != item.Goal.Id).ToArray();
                // Count the supplied evaluation inputs even when a previously
                // built scope is reused, since the evaluator reads them again.
                owner._goalVisits += without.Length + _scope.Length;
                var next = owner.Evaluate(binding, without);
                if (answer != next) return null;
                answers.Add(identity, next);
            }
            return answers;
        }

        internal void AfterRemoval(Dictionary<BindingKey, ProviderBudgetHoldSchedule?> answers)
        {
            _exhausted = null;
            _scope = null;
            _answers = answers;
        }

        private IEnumerable<Evidence> ReadEntries()
        {
            foreach (var entry in working.Concat(archived.Values))
            {
                owner._goalVisits++;
                yield return entry;
            }
        }
    }
}
