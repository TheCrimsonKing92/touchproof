using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// A started run owns its recorded identity and obligations until a terminal entry replaces it.
internal static class PreTesterStartedEvidence
{
    // Notices belong to the receipt for the conductor process lifetime, across driver instances.
    private static readonly ConcurrentDictionary<string, byte> DriftNotices = new(StringComparer.Ordinal);

    internal static Action<string> DriftProgress(string receiptId) => line =>
    {
        if (DriftNotices.TryAdd(receiptId, 0)) Console.WriteLine(line);
    };

    internal static bool TryAbandon(PreTesterEvidenceEntry? prior, string reason, Action<string> record)
    {
        if (prior?.Outcome != "started") return false;
        var abandoned = prior with { Outcome = "abandoned", ResultPath = null, FailingTests = [] };
        var slug = Regex.Replace(reason, "(?<!^)([A-Z])", "-$1").ToLowerInvariant();
        record(PreTesterEvidenceIndexLines.FormatMarker(abandoned) + "; abandoned_reason=" + slug);
        return true;
    }
}
