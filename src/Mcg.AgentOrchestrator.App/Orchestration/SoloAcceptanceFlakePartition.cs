using Mcg.AgentOrchestrator.Infrastructure;
using TrxRow = Mcg.AgentOrchestrator.App.Orchestration.SoloAcceptanceFailureReceiptReader.TrxRow;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class SoloAcceptanceFlakePartition
{
    private readonly AcceptanceCheckResult _check;
    private readonly IReadOnlyList<List<TrxRow>> _artifactRows;
    private readonly IReadOnlyList<TrxRow> _rows;
    private readonly HashSet<string> _confirmedFlakes = new(StringComparer.Ordinal);
    private string? _firstFlakeDiagnostic;
    private bool _repeatedFailure;

    private SoloAcceptanceFlakePartition(AcceptanceCheckResult check, IReadOnlyList<List<TrxRow>> artifactRows)
    {
        _check = check;
        _artifactRows = artifactRows;
        _rows = artifactRows.SelectMany(rows => rows).ToArray();
    }

    internal static SoloAcceptanceFlakePartition Classify(AcceptanceCheckResult check,
        IReadOnlyList<List<TrxRow>> artifactRows, bool allowConfirmedFlakes, bool partitionAmbiguousRows = false)
    {
        var partition = new SoloAcceptanceFlakePartition(check, artifactRows);
        foreach (var group in partition._rows.GroupBy(row => row.Key, StringComparer.Ordinal)
            .Where(group => group.Count() > 1 && group.Any(row => row.Value.Outcome == "Failed")))
        {
            var outcomes = group.Select(row => row.Value.Outcome).ToArray();
            // Listing the same artifact twice does not establish separate executions.
            if (group.Select(row => row.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != outcomes.Length)
            {
                if (!partitionAmbiguousRows) throw DuplicateRows(group.Key, outcomes);
                continue;
            }

            if (partitionAmbiguousRows)
            {
                // Repair consumption requires a unique row for each identity, even for reruns.
                partition._repeatedFailure = true;
                continue;
            }

            var lastPassed = Array.LastIndexOf(outcomes, "Passed");
            if (lastPassed >= 0)
            {
                var diagnostic = $"solo-repeated-execution-flake: identity={group.Key}; " +
                    $"outcome={AcceptanceLaneRerunEvidence.Flake}; outcomes={string.Join(",", outcomes)}";
                if (!allowConfirmedFlakes || lastPassed < Array.IndexOf(outcomes, "Failed"))
                    throw new InvalidDataException(diagnostic);
                partition._firstFlakeDiagnostic ??= diagnostic;
                partition._confirmedFlakes.Add(group.Key);
            }
            else if (outcomes.Any(outcome => outcome != "Failed"))
            {
                throw DuplicateRows(group.Key, outcomes);
            }
            partition._repeatedFailure = true;
        }
        return partition;
    }

    internal static SortedSet<string> GetDroppedIdentities(IEnumerable<SoloAcceptanceFlakePartition> partitions)
    {
        var checks = partitions.ToArray();
        var dropped = new SortedSet<string>(checks.SelectMany(check => check._confirmedFlakes), StringComparer.Ordinal);
        if (dropped.Count > 0 && !checks.SelectMany(check => check._rows)
            .Any(row => row.Value.Outcome == "Failed" && !dropped.Contains(row.Key)))
            throw new InvalidDataException(checks.First(check => check._firstFlakeDiagnostic is not null)._firstFlakeDiagnostic);
        return dropped;
    }

    internal IReadOnlyList<TrxRow> ValidateRows()
    {
        // Narrowed reruns record per-run maxima. Other multi-TRX checks retain sums.
        var executedCounts = _artifactRows.Select(rows => rows.Count(row => row.Value.Outcome is "Passed" or "Failed")).ToArray();
        var executed = _repeatedFailure ? executedCounts.Max() : executedCounts.Sum();
        var discoveredRows = _repeatedFailure ? _artifactRows.Max(rows => rows.Count) : _rows.Count;
        if (executed != _check.ExecutedTestCount)
            throw new InvalidDataException($"solo-incomplete-check-rows: executed rows={executed}; recorded={_check.ExecutedTestCount}");
        if (_check.DiscoveredTestCount is { } discovered && discovered != discoveredRows)
            throw new InvalidDataException($"solo-incomplete-check-rows: discovered rows={discoveredRows}; recorded={discovered}");

        // Each check must account for its own failures. A flake proven elsewhere cannot excuse a local mismatch.
        var observed = _rows.Where(row => row.Value.Outcome == "Failed" && !_confirmedFlakes.Contains(row.Key))
            .Select(row => row.Key).Distinct(StringComparer.Ordinal);
        var declared = (_check.FailingTestIdentities ?? []).Where(identity => !_confirmedFlakes.Contains(identity));
        if (!SoloAcceptanceFailureReceiptReader.SameNames(observed, declared))
            throw new InvalidDataException("solo-failing-tests-mismatch");
        return _rows;
    }

    internal IReadOnlyList<TrxRow> ReadFailures(IReadOnlySet<string> dropped) =>
        ValidateRows().Where(row => row.Value.Outcome == "Failed" && !dropped.Contains(row.Key)).ToArray();

    private static InvalidDataException DuplicateRows(string identity, IEnumerable<string> outcomes) =>
        new($"{SoloHoldReasons.DuplicateTestRow}: identity={identity}; outcomes={string.Join(",", outcomes)}");
}
