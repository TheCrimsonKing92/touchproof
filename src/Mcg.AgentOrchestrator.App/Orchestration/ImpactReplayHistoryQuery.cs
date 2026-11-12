namespace Mcg.AgentOrchestrator.App.Orchestration;

// Invocation-scoped acquisition, after replay selection and local adjudication. No ambient cache.
internal sealed class ImpactReplayHistoryQuery(
    Func<IReadOnlyCollection<string>, ImpactReplayHistoryQuery.Evidence> read)
{
    internal sealed record Evidence(IReadOnlyDictionary<string, GateStatsReport.FlakeRow> Rows,
        bool Complete, IReadOnlyList<string> Diagnostics);
    private readonly Dictionary<string, Evidence> _cache = new(StringComparer.Ordinal);
    internal IReadOnlyList<string> QueriedIdentities => _cache.Keys.Order(StringComparer.Ordinal).ToArray();

    internal IReadOnlyList<ImpactReplayFailingClass> Classify(
        IReadOnlyList<ImpactReplayFailingClass> failures,
        IReadOnlyDictionary<(string ClassName, string Lane), IReadOnlyList<string>> identities,
        ICollection<string> warnings)
    {
        var eligible = failures.Where(failure => failure.IsMiss && !failure.ConfirmedHardFailure).ToArray();
        var requested = eligible.SelectMany(Tests).Distinct(StringComparer.Ordinal)
            .Where(test => !_cache.ContainsKey(test)).Order(StringComparer.Ordinal).ToArray();
        if (requested.Length > 0)
        {
            var evidence = read(requested);
            foreach (var test in requested) _cache.Add(test, evidence);
        }
        return failures.Select(failure =>
        {
            if (!failure.IsMiss || failure.ConfirmedHardFailure) return failure;
            var tests = Tests(failure);
            if (tests.Count == 0)
            {
                warnings.Add($"Historical evidence incomplete for {failure.ClassName}: exact failing identity unavailable. " +
                    "Recover the selected original/rerun receipts before adjudication.");
                return failure;
            }
            // A hard outcome from a covered identity is dispositive for the whole failing class.
            if (tests.Any(test => _cache[test].Complete && _cache[test].Rows.TryGetValue(test, out var row) && row.Test == test && row.Hard > 0))
                return failure;
            var unavailable = tests.Where(test => !_cache[test].Complete).ToArray();
            if (unavailable.Length > 0)
            {
                foreach (var diagnostic in unavailable.SelectMany(test => _cache[test].Diagnostics).Distinct(StringComparer.Ordinal))
                    if (!warnings.Contains(diagnostic)) warnings.Add(diagnostic);
                warnings.Add($"Historical evidence incomplete for {failure.ClassName}: {string.Join(", ", unavailable)}. " +
                    "Recover authoritative exact-identity coverage or authorize a separate diagnostic history scan.");
                return failure;
            }
            var rows = tests.Where(test => _cache[test].Rows.ContainsKey(test))
                .ToDictionary(test => test, test => _cache[test].Rows[test], StringComparer.Ordinal);
            return ImpactReplayHistoricalFlakeClassifier.Classify(failure, tests, rows);
        }).ToArray();

        IReadOnlyList<string> Tests(ImpactReplayFailingClass failure) =>
            failure.Lane is { } lane && identities.TryGetValue((failure.ClassName, lane), out var tests) ? tests : [];
    }
}
