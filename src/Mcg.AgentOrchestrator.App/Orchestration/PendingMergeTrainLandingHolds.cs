using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Batch-loop-owned lifetime for passed receipts held by the landing executor before mutation.
// Expired entries remain observed so retrying Land cannot restart the thirty-minute budget.
internal sealed class PendingMergeTrainLandingHolds(Func<DateTimeOffset>? utcNow = null)
{
    private readonly Func<DateTimeOffset> _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    private readonly Dictionary<string, (MergeTrainReceipt Receipt, DateTimeOffset FirstHeldAt)> _held =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _observed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _expired = new(StringComparer.Ordinal);

    internal IEnumerable<GoalId> HeldGoalIds => _held.Values
        .SelectMany(hold => hold.Receipt.Identity.Members).Select(member => member.GoalId);

    internal void BeginPass() => _observed.Clear();

    internal void ObserveExisting(MergeTrainReceipt receipt)
    {
        if (_held.ContainsKey(receipt.ReceiptId)) _observed.Add(receipt.ReceiptId);
    }

    internal void Observe(MergeTrainReceipt receipt)
    {
        if (receipt.Outcome != MergeTrainGateOutcome.Passed)
            throw new InvalidOperationException("Only a passed receipt can be waiting for landing.");
        _observed.Add(receipt.ReceiptId);
        _held.TryAdd(receipt.ReceiptId, (receipt, _utcNow()));
    }

    internal void Release(string receiptId)
    {
        _held.Remove(receiptId);
        _expired.Remove(receiptId);
    }

    internal void Clear()
    {
        _held.Clear();
        _expired.Clear();
    }

    internal void EndPass()
    {
        foreach (var id in _held.Keys.Where(id => !_observed.Contains(id)).ToArray()) Release(id);
    }

    internal IReadOnlyList<MergeTrainReceipt> Active(Action<MergeTrainReceipt> onExpired)
    {
        var now = _utcNow();
        var active = new List<MergeTrainReceipt>();
        foreach (var (id, hold) in _held.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (_expired.Contains(id)) continue;
            if (now - hold.FirstHeldAt >= TimeSpan.FromMinutes(30))
            {
                if (_expired.Add(id)) onExpired(hold.Receipt);
            }
            else
                active.Add(hold.Receipt);
        }
        return active;
    }
}
