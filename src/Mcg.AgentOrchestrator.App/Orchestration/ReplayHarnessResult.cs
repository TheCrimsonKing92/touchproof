using System.Collections.ObjectModel;
using System.Globalization;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ReplayHarnessResult
{
    internal ReplayHarnessResult(IEnumerable<ReplayHarnessCase> cases,
        IReadOnlyDictionary<string, double> modeledOutcomes)
    {
        Cases = Array.AsReadOnly(cases.ToArray());
        DecisionDiff = Array.AsReadOnly(Cases.Where(item => item.Replayable &&
            item.CandidateDecision is not null &&
            !string.Equals(item.RecordedDecision, item.CandidateDecision, StringComparison.Ordinal)).ToArray());
        ModeledOutcomes = new ReadOnlyDictionary<string, double>(
            new Dictionary<string, double>(modeledOutcomes, StringComparer.Ordinal));
        ReplayableCount = Cases.Count(item => item.Replayable);
    }

    internal IReadOnlyList<ReplayHarnessCase> Cases { get; }
    internal IReadOnlyList<ReplayHarnessCase> DecisionDiff { get; }
    internal IReadOnlyDictionary<string, double> ModeledOutcomes { get; }
    internal int ReplayableCount { get; }
    internal double? ReplayabilityRate => Cases.Count == 0 ? null : (double)ReplayableCount / Cases.Count;
    internal string ReplayabilityRateText => ReplayabilityRate?.ToString("F3", CultureInfo.InvariantCulture) ?? "n/a";
}
