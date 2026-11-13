using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Snapshots are append-only in the canonical run-event store. Maintenance only prunes
// conductor.tick and goal.operation, so coverage and unresolved misses survive maintenance.
internal sealed class SelectedExecutionBackstopStore(SqliteRunEventStore events)
{
    internal const string EventType = "acceptance.selected-execution-backstop";
    private readonly SemaphoreSlim _projection = new(1, 1);
    private long _afterSequence;
    private SelectedExecutionBackstopState? _latest;
    internal async Task<SelectedExecutionBackstopState?> ReadAsync()
    {
        await _projection.WaitAsync().ConfigureAwait(false);
        try
        {
            while (true)
            {
                var page = await events.ReadByTypeSinceAsync(EventType, afterSequence: _afterSequence, maxCount: 5000).ConfigureAwait(false);
                if (page.Count == 0) return _latest;
                _latest = JsonSerializer.Deserialize<SelectedExecutionBackstopState>(page[^1].PayloadJson!)
                    ?? throw new InvalidDataException("Backstop state is unreadable.");
                _afterSequence = page[^1].Sequence;
            }
        }
        finally { _projection.Release(); }
    }

    internal async Task<bool> HasLandingAsync(string goalId, string commit) =>
        await events.ReadByEventIdAsync(LandingId(goalId, commit)).ConfigureAwait(false) is not null;

    internal static string LandingId(string goalId, string commit) => $"selected-execution-backstop:landing:{goalId}:{commit}";

    internal async Task WriteAsync(string operation, SelectedExecutionBackstopState state, DateTimeOffset now, string? eventId = null)
    {
        await events.AppendAsync(new(EventType, null, operation, operation, null,
            JsonSerializer.Serialize(state), now, eventId)).ConfigureAwait(false);
    }
}
