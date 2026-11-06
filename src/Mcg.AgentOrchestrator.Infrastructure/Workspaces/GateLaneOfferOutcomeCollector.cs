using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record GateLaneOfferOutcome(string Outcome, string Reason);

// One gate invocation owns the latest recorded pair and distinct placements for each lane.
internal sealed class GateLaneOfferOutcomeCollector
{
    private readonly object _gate = new();
    private readonly Dictionary<string, GateLaneOfferOutcome> _offers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _remote = new(StringComparer.Ordinal);
    private bool _summaryAttempted;

    internal void Record(string lane, string outcome, string reason)
    {
        lock (_gate) _offers[lane] = new(outcome, reason);
    }

    internal GateLaneOfferOutcome? Latest(string lane)
    {
        lock (_gate) return _offers.GetValueOrDefault(lane);
    }

    internal int OfferedCount { get { lock (_gate) return _offers.Count; } }
    internal int RemoteCount { get { lock (_gate) return _remote.Count; } }

    internal void RecordAccepted(string worktreePath, string? goalId, AcceptanceCheckResult accepted,
        string executorId, TimeSpan remoteDuration, DateTimeOffset now,
        IReadOnlyDictionary<string, string>? testSettings = null, string? testSettingsHost = null)
    {
        try
        {
            GateLaneOfferOutcome? offer;
            lock (_gate)
            {
                _remote.Add(accepted.Name);
                offer = _offers.GetValueOrDefault(accepted.Name);
            }
            GateLaneReceiptLedger.RecordRemote(worktreePath, goalId, accepted.Name, accepted,
                executorId, remoteDuration, now, offer, testSettings: testSettings, testSettingsHost: testSettingsHost);
        }
        catch (Exception) { /* Diagnostic collection cannot change acceptance. */ }
    }

    internal BatchSummary BeginBatchSummary(string worktreePath, string? goalId, TimeProvider clock,
        Func<ValueTask>? settleOffers = null) => new(this, worktreePath, goalId, clock, settleOffers);

    private void CompleteBatch(string worktreePath, string? goalId, TimeProvider clock)
    {
        try
        {
            int offered;
            lock (_gate)
            {
                if (_summaryAttempted || _offers.Count == 0 || _remote.Count != 0) return;
                _summaryAttempted = true;
                offered = _offers.Count;
            }
            GateLaneReceiptLedger.RecordOfferSummary(worktreePath, goalId, offered, 0, clock.GetUtcNow());
        }
        catch (Exception) { /* Attempt once even if persistence fails. */ }
    }

    internal sealed class BatchSummary(GateLaneOfferOutcomeCollector owner, string worktreePath,
        string? goalId, TimeProvider clock, Func<ValueTask>? settleOffers) : IDisposable, IAsyncDisposable
    {
        public void Dispose() => owner.CompleteBatch(worktreePath, goalId, clock);

        public async ValueTask DisposeAsync()
        {
            try
            {
                // A prebuild failure or cancellation can bypass adoption of early offers.
                if (settleOffers is not null) await settleOffers().ConfigureAwait(false);
            }
            catch (Exception) { /* Preserve the batch's original failure or cancellation. */ }
            finally { Dispose(); }
        }
    }
}
