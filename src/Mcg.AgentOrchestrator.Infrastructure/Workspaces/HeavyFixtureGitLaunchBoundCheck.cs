using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Measures executions owned by this gate; never executes test bodies or reuses an earlier gate's census.
internal static class HeavyFixtureGitLaunchBoundCheck
{
    internal sealed record Baseline(string ClassName, int Launches, int Rows);

    // Gate 208e909d-0-20261106160206117, main 0798b6829, first-run slot-timing rows.
    // Bound operator answer f9e25b4c9cf64f44a92318e95bcfd93d. Launches remain historical.
    internal static IReadOnlyList<Baseline> Baselines { get; } = Array.AsReadOnly(new Baseline[]
    {
        new("AcceptanceCohortWorkflowTestsTrainRedAttribution", 1530, 43),
        new("MergeTrainFocusedRecheckReceiptAcceptanceTests", 1349, 31),
        new("AcceptanceCohortWorkflowTestsMergeTrain", 1042, 20),
        new("PullRequestTrunkDriftTests", 959, 20),
        new("CliCommandTestsTerminalSweepCommands", 930, 55)
    });

    internal static AcceptanceCheckResult? Evaluate(IEnumerable<AcceptanceCheckResult> checks)
    {
        var coverage = Baselines.ToDictionary(entry => entry.ClassName,
            _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        var launches = Baselines.ToDictionary(entry => entry.ClassName, _ => 0L, StringComparer.Ordinal);
        var incomplete = new List<string>();
        var malformed = new List<string>();
        var relevant = false;
        foreach (var path in ReceiptPaths(checks))
        {
            var extracted = AcceptanceTrxExecutedTestIdentityExtractor.Extract([path]);
            if (!File.Exists(path) || extracted.UnreadableReceiptPaths.Count > 0)
                incomplete.Add($"unreadable TRX: {path}");
            var identities = extracted.Identities.Where(identity => ClassFor(identity) is not null).ToArray();
            foreach (var identity in identities) coverage[ClassFor(identity)!].Add(identity);
            var fileRelevant = identities.Length > 0;
            var timingCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var fileIncomplete = new List<string>();
            var fileMalformed = new List<string>();
            var sidecar = LaneSlotTimingReader.PathFor(path);
            try
            {
                var lineNumber = 0;
                foreach (var line in File.ReadLines(sidecar))
                {
                    lineNumber++;
                    try
                    {
                        using var document = JsonDocument.Parse(line);
                        var row = document.RootElement;
                        if (row.ValueKind != JsonValueKind.Object ||
                            !row.TryGetProperty("test_name", out var name) || name.ValueKind != JsonValueKind.String ||
                            string.IsNullOrWhiteSpace(name.GetString()))
                        {
                            fileMalformed.Add($"invalid identity: {sidecar}:{lineNumber}");
                            continue;
                        }
                        var identity = AcceptanceTrxTestIdentityResolver.NormalizeSelector(name.GetString()!);
                        var className = ClassFor(identity);
                        if (className is null) continue;
                        fileRelevant = true;
                        if (!row.TryGetProperty("git_launches", out var count))
                        {
                            fileIncomplete.Add($"missing git_launches: {sidecar}:{lineNumber}");
                            continue;
                        }
                        if (count.ValueKind != JsonValueKind.Number || !count.TryGetInt64(out var value) || value < 0)
                        {
                            fileMalformed.Add($"invalid git_launches: {sidecar}:{lineNumber}");
                            continue;
                        }
                        launches[className] = checked(launches[className] + value);
                        timingCounts[identity] = checked(timingCounts.GetValueOrDefault(identity) + 1);
                    }
                    catch (Exception error) when (error is JsonException or OverflowException)
                    {
                        fileMalformed.Add($"{error.GetType().Name}: {sidecar}:{lineNumber}");
                    }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                fileIncomplete.Add($"unreadable slot timing: {sidecar} ({error.GetType().Name})");
            }
            if (!fileRelevant) continue;
            relevant = true;
            incomplete.AddRange(fileIncomplete);
            malformed.AddRange(fileMalformed);
            // Timing identities omit theory arguments. Require a measured window for each executed row,
            // per receipt and method, so a partial sidecar cannot certify a complete TRX.
            foreach (var group in identities.GroupBy(AcceptanceTrxTestIdentityResolver.NormalizeSelector))
                if (timingCounts.GetValueOrDefault(group.Key) < group.Count())
                    incomplete.Add($"missing measured rows: {group.Key} ({timingCounts.GetValueOrDefault(group.Key)}/{group.Count()}) in {sidecar}");
        }
        if (!relevant) return null;

        long total = 0;
        try { foreach (var count in launches.Values) total = checked(total + count); }
        catch (OverflowException) { malformed.Add("aggregate launch count overflow"); }
        var detail = string.Join("; ", Baselines.Select(entry =>
            $"{entry.ClassName}: rows={coverage[entry.ClassName].Count}/{entry.Rows}, launches={launches[entry.ClassName]}"));
        foreach (var entry in Baselines)
        {
            if (coverage[entry.ClassName].Count > entry.Rows)
                malformed.Add($"excess distinct rows: {entry.ClassName}");
            else if (coverage[entry.ClassName].Count < entry.Rows)
                incomplete.Add($"missing rows: {entry.ClassName} ({coverage[entry.ClassName].Count}/{entry.Rows})");
        }
        var historical = Baselines.Sum(entry => entry.Launches);
        var maximum = historical * 3L / 4;
        var skipped = malformed.Count == 0 && incomplete.Count > 0;
        var passed = !skipped && malformed.Count == 0 && total <= maximum;
        var status = skipped ? "skipped" : passed ? "passed" : "failed";
        var reason = malformed.Count > 0 ? "malformed: " + string.Join("; ", malformed)
            : skipped ? string.Join("; ", incomplete) : passed ? "complete census" : "above 75% bound";
        var summary = $"status={status}; reason={reason}; launches={total}; historical={historical}; maximum={maximum}; rows={coverage.Values.Sum(rows => rows.Count)}";
        return new AcceptanceCheckResult("heavy-fixture git launch bound", passed, skipped ? null : passed ? 0 : 1,
            summary + Environment.NewLine + detail, ResultSummary: summary, Advisory: skipped);
    }

    private static string? ClassFor(string identity)
    {
        var selector = AcceptanceTrxTestIdentityResolver.NormalizeSelector(identity);
        var separator = selector.LastIndexOf('.');
        if (separator < 0) return null;
        var className = selector[..separator];
        return Baselines.Any(entry => entry.ClassName.Equals(className, StringComparison.Ordinal)) ? className : null;
    }

    private static IEnumerable<string> ReceiptPaths(IEnumerable<AcceptanceCheckResult> checks) => checks
        .Where(check => !check.TestResultIsExplicitCrossAttemptReuse)
        .SelectMany(check => (check.CrosswalkDiagnostics is not null
                ? check.NormalizedTestResultPaths ?? [] : check.TestResultPaths ?? [])
            .Concat(check.LaneRerun?.FirstTestResultPaths ?? [])
            .Concat(check.LaneRerun?.RerunTestResultPaths ?? [])
            .Concat(check.WithinAttemptRerun?.RerunTestResultPaths ?? []))
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Select(Path.GetFullPath)
        .Distinct(StringComparer.OrdinalIgnoreCase);
}
