using System.Globalization;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Reuses existing adjudication receipts as a positive identity index, never as an exhaustive census.
// Adjacent normalized peers are not source-bound native indexes: they cannot prove absence of hard history.
internal sealed class ImpactReplayHistoricalEvidenceReader(
    Func<IReadOnlyList<AcceptanceLaneFlakeRow>> readReceipts,
    Func<string, IReadOnlyList<GateStatsReader.TestResult>?>? readTrx = null)
{
    private IReadOnlyList<AcceptanceLaneFlakeRow>? _receipts;
    private string? _metadataProblem;
    private readonly Dictionary<string, IReadOnlyList<GateStatsReader.TestResult>?> _reads = new(StringComparer.OrdinalIgnoreCase);
    internal int MetadataReads { get; private set; }
    internal IReadOnlyList<string> ParsedPaths => _reads.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray();

    internal ImpactReplayHistoryQuery.Evidence Read(IReadOnlyCollection<string> identities)
    {
        if (identities.Count == 0) return new(new Dictionary<string, GateStatsReport.FlakeRow>(), true, []);
        if (_receipts is null)
        {
            MetadataReads++;
            try { _receipts = readReceipts(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
            {
                _receipts = [];
                _metadataProblem = $"Historical receipt metadata unavailable: {ex.GetType().Name}. Recover the lane-flakes receipt provenance.";
            }
        }
        var requested = identities.ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();
        if (_metadataProblem is not null) problems.Add(_metadataProblem);
        var runs = new Dictionary<(string Attempt, string Lane, int Run, string Path), GateStatsReader.LaneRun>();
        foreach (var row in _receipts.Where(row => row.FirstFailingTestIdentities.Any(requested.Contains) ||
                     row.RerunFailingTestIdentities.Any(requested.Contains)))
        {
            if (string.IsNullOrWhiteSpace(row.AttemptId) || string.IsNullOrWhiteSpace(row.CandidateTreeSha) ||
                string.IsNullOrWhiteSpace(row.FirstInvocationId) || string.IsNullOrWhiteSpace(row.RerunInvocationId) ||
                row.FirstTestResultPaths.Count == 0 || row.RerunTestResultPaths.Count == 0)
            {
                problems.Add($"Historical receipt provenance incomplete: {row.AttemptId}, {row.LaneName}.");
                continue;
            }
            var firstPaths = row.FirstTestResultPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var rerunPaths = row.RerunTestResultPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            // Receipt membership binds identities to an invocation, not to individual files.
            // With multiple files there is no authoritative per-path identity mapping; do not
            // open unrelated TRX to discover which one contains the requested identity.
            if (firstPaths.Length != 1 || rerunPaths.Length != 1)
            {
                problems.Add($"Historical receipt lacks per-path identity provenance: {row.AttemptId}, {row.LaneName}. " +
                    "Recover authoritative per-path exact-identity metadata; a diagnostic scan requires separate operator authorization.");
                continue;
            }
            foreach (var path in firstPaths.Concat(rerunPaths).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                // A relative reference has no owning root in the ledger contract; do not guess one.
                if (!Path.IsPathFullyQualified(path))
                {
                    problems.Add($"Historical receipt has an unrooted TRX reference: {path}.");
                    continue;
                }
                var suffix = Regex.Match(Path.GetFileNameWithoutExtension(path), @"-run-(\d+)$", RegexOptions.CultureInvariant);
                var ordinal = 0;
                if (suffix.Success && !int.TryParse(suffix.Groups[1].Value, NumberStyles.None,
                        CultureInfo.InvariantCulture, out ordinal))
                {
                    problems.Add($"Historical receipt has an invalid run ordinal: {path}.");
                    continue;
                }
                if (!_reads.TryGetValue(path, out var results))
                    _reads[path] = results = (readTrx ?? GateStatsReader.ReadTrx)(path);
                if (results is null)
                {
                    problems.Add($"Historical TRX unavailable: {path}.");
                    continue;
                }
                runs.TryAdd((row.AttemptId, row.LaneName, ordinal, path), new(row.LaneName, ordinal,
                    results.Where(result => result.Test is { } test && requested.Contains(test)).ToArray()));
            }
        }
        var attempts = runs.GroupBy(pair => pair.Key.Attempt, StringComparer.Ordinal)
            .Select(group => new GateStatsReader.GateRunAttempt(group.Key, default, group.Select(pair => pair.Value).ToArray())).ToArray();
        var history = GateStatsReport.Flakes(attempts, problems.Count, int.MaxValue).Tests
            .ToDictionary(row => row.Test, StringComparer.Ordinal);
        problems.Add("Historical coverage unavailable: lane-flakes receipts index adjudications only; retained native TRX " +
            "has no exhaustive source-bound exact-identity index. Recover authoritative coverage metadata; " +
            "an exhaustive compatibility scan requires separate operator authorization.");
        return new(history, false, problems);
    }
}
