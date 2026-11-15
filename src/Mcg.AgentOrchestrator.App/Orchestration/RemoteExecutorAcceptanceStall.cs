using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record RemoteExecutorAcceptanceStall(
    bool Stalled, int Offers, DateTimeOffset? LastAcceptedAt, string? LastOfferReason)
{
    internal static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(60);

    internal static RemoteExecutorAcceptanceStall Evaluate(
        IEnumerable<RemoteExecutorOutcomeRow> outcomes, DateTimeOffset now, TimeSpan? window = null)
    {
        var since = now - (window ?? DefaultWindow);
        var offers = 0;
        var acceptedInWindow = false;
        DateTimeOffset? lastAcceptedAt = null;
        RemoteExecutorOutcomeRow? lastOffer = null;
        foreach (var row in outcomes)
        {
            if (row.ObservedAt > now) continue;
            if (row.Outcome == "accepted")
            {
                if (lastAcceptedAt is null || row.ObservedAt > lastAcceptedAt) lastAcceptedAt = row.ObservedAt;
                if (row.ObservedAt >= since) acceptedInWindow = true;
            }
            if (row.ObservedAt < since || RemoteExecutorReport.IsNonAttemptOutcome(row.Outcome)) continue;
            offers++;
            // Later ledger entries win ties, even when the input is not timestamp ordered.
            if (lastOffer is null || row.ObservedAt >= lastOffer.ObservedAt) lastOffer = row;
        }
        return new(offers > 0 && !acceptedInWindow, offers, lastAcceptedAt, lastOffer?.Reason);
    }
}
