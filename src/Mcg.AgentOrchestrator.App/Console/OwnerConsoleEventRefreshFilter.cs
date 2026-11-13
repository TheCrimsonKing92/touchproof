using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleEventRefreshFilter
{
    private readonly object _sync = new();
    private DateTimeOffset? _skipped;
    private readonly OwnerConsoleEventRoute _route = new();
    private readonly List<OwnerConductEvent> _deferred = [];

    // Maps protects activity; committed changes protect board and decisions;
    // gate/evidence kinds and ACCEPTANCE details protect the gate pane; tick protects latest tick.
    internal static bool ChangesView(OwnerConductEvent item) =>
        OwnerActivityEventMap.Maps(item) ||
        ChangeStreamRecord.TryClassify(item.EventKind, item.GoalId, out _) ||
        item.EventKind is "gate-progress" or "remote-lane" or "EVIDENCE_START" or "EVIDENCE_END" ||
        item.Detail.StartsWith("ACCEPTANCE", StringComparison.Ordinal) ||
        item.EventKind.StartsWith("tick", StringComparison.OrdinalIgnoreCase);

    internal void Route(OwnerConductEvent item, OwnerConsoleRefreshCoalescer coalescer)
    {
        bool repeated;
        lock (_sync)
        {
            repeated = _route.IsRepeatedGoal(item);
            if (repeated)
            {
                _deferred.Add(item);
                if (_deferred.Count > OwnerConsoleStartupActivity.MaxRawEvents) _deferred.RemoveAt(0);
            }
        }
        if (!repeated && ChangesView(item))
        {
            coalescer.Request(item, OwnerConsoleRefreshScope.None);
            return;
        }
        lock (_sync)
        {
            if (_skipped is null || item.Timestamp > _skipped) _skipped = item.Timestamp;
        }
    }

    internal IReadOnlyList<OwnerConductEvent> TakeDeferred()
    {
        lock (_sync)
        {
            var items = _deferred.ToArray();
            _deferred.Clear();
            return items;
        }
    }

    internal DateTimeOffset? FoldLast(DateTimeOffset? last)
    {
        lock (_sync)
        {
            if (_skipped is { } skipped && (last is null || skipped > last)) last = skipped;
            _skipped = null;
            return last;
        }
    }
}
