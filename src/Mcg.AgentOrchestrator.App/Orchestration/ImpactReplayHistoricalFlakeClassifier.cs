using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ImpactReplayHistoricalFlakeClassifier
{
    internal static IReadOnlyList<ImpactReplayFailingClass> Classify(
        IReadOnlyList<ImpactReplayFailingClass> failures, IReadOnlyList<ImpactReplayLaneOutcome> lanes,
        IReadOnlyList<AcceptanceTestClassDescriptor> classes,
        IReadOnlyDictionary<string, GateStatsReport.FlakeRow> history)
    {
        if (history.Count == 0) return failures;
        var reads = new Dictionary<string, AcceptanceTrxReadResult>(StringComparer.OrdinalIgnoreCase);
        return failures.Select(failure =>
        {
            if (!failure.IsMiss || failure.ConfirmedHardFailure) return failure;
            var paths = lanes.Where(lane => lane.Lane == failure.Lane).SelectMany(lane => lane.TrxPaths)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var tests = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in paths)
            {
                if (!reads.TryGetValue(path, out var read)) reads[path] = read = AcceptanceTrxFailureReader.Read(path);
                if (read.Status != AcceptanceTrxReadStatus.Readable) return failure;
                foreach (var result in read.Failures.Where(result => AcceptanceTrxOutcomeTaxonomy.IsFatal(result.Outcome)))
                {
                    if (ImpactReplayTrxEvidence.ResolveClass(result.TestName, classes) != failure.ClassName) continue;
                    if (string.IsNullOrWhiteSpace(result.TestName)) return failure;
                    tests.Add(result.TestName);
                }
            }
            return Classify(failure, tests, history);
        }).ToArray();
    }

    internal static ImpactReplayFailingClass Classify(ImpactReplayFailingClass failure,
        IReadOnlyCollection<string> tests, IReadOnlyDictionary<string, GateStatsReport.FlakeRow> history)
    {
        // Every exact failing identity needs positive history; a flaky sibling cannot excuse a hard failure.
        if (!failure.IsMiss || failure.ConfirmedHardFailure || tests.Count == 0 ||
            tests.Any(test => !history.TryGetValue(test, out var row) ||
                !string.Equals(row.Test, test, StringComparison.Ordinal) || row.Flake <= 0 || row.Hard != 0))
            return failure;
        var evidence = tests.Order(StringComparer.Ordinal).Select(test =>
        {
            var row = history[test];
            return $"gate-stats-history: {test} attempts={row.Attempts} flake={row.Flake} hard={row.Hard}";
        });
        return failure with { FlakeEvidence = string.Join("; ", evidence) };
    }
}
