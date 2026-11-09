using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerEventFields;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// One session's evidence reads and supersession sequence, shared by startup and refresh.
internal sealed class OwnerActivityEvidenceLoader(Func<OwnerConductEvent, OwnerActivityTestEvidence?>? read)
{
    private long _sequence;
    private readonly OwnerActivityEvidenceCache _cache = new();

    internal long Begin() => Interlocked.Increment(ref _sequence);

    internal bool IsCurrent(long ticket) => Interlocked.Read(ref _sequence) == ticket;

    internal IReadOnlyDictionary<OwnerConductEvent, OwnerActivityTestEvidence?> Resolve(
        IEnumerable<OwnerConductEvent> events)
    {
        var resolved = new Dictionary<OwnerConductEvent, OwnerActivityTestEvidence?>();
        if (read is not null)
            foreach (var item in events.Where(NeedsEvidence).Distinct()) resolved.Add(item, _cache.Read(item, read));
        return resolved;
    }

    private static bool NeedsEvidence(OwnerConductEvent item) => item.EventKind switch
    {
        "acceptance" => Field(item, "result") is "failed" or "blocked",
        "acceptance-cohort" => Field(item, "outcome") != "passed" && Field(item, "verdict") != "passed",
        "canary-gate" => Field(item, "result") == "failed",
        _ => false
    };
}
