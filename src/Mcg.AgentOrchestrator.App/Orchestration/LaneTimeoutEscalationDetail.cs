using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class LaneTimeoutEscalationDetail
{
    private static readonly JsonSerializerOptions ReceiptJson = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private static readonly Regex ProgressFields = new(
        """(?:^|\s)(?<key>[\w]+)=(?<value>"(?:\\.|[^"\\])*"|[^\s"]+)""", RegexOptions.CultureInvariant);

    internal static string TimeoutGateAttemptKey(GoalOperationJournalEntry entry) =>
        $"candidate={entry.BranchHeadSha}/{entry.MainHeadSha} started={entry.At:O} attempt=";

    internal static string? TimeoutLane(string name) =>
        FailedGoalTimedOutSelectionRerunRule.TryParseSelection(name, out var lane) ? lane : null;

    internal static string Format(
        AcceptanceVerificationSummary acceptance, IEnumerable<string> repeatedChecks, GoalOperationJournalEntry attempt,
        string executionDirectory, string? conductLogPath)
    {
        var checkNames = repeatedChecks.ToHashSet(StringComparer.Ordinal);
        var lanes = acceptance.UnmetCriteria.Where(check => !check.Passed && checkNames.Contains(check.Name))
            .GroupBy(check => TimeoutLane(check.Name)!, StringComparer.OrdinalIgnoreCase);
        var storePath = GateLaneReceiptLedger.ResolveStorePath(executionDirectory);
        var goalId = attempt.GoalId.Value;
        return string.Join('\n', lanes.Select(lane =>
        {
            var slowest = FormatTrx(lane, acceptance.AttemptId);
            var memory = ReadReceipt(storePath, goalId, lane.Key, attempt.At, acceptance.AttemptId);
            var gates = ReadConcurrentGates(conductLogPath, goalId[..8], lane.Key, attempt.At);
            return $"LANE_TIMEOUT_DETAIL lane={lane.Key} {slowest} host_available_memory={memory} concurrent_gates={gates}";
        }));
    }

    private static string FormatTrx(IEnumerable<AcceptanceCheckResult> checks, string? attemptId)
    {
        var unavailable = new List<string>();
        var durations = new Dictionary<string, double>(StringComparer.Ordinal);
        var paths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var check in checks)
        {
            if (check.TestResultPaths is not { Count: > 0 }) continue;
            if (check.TestResultIsExplicitCrossAttemptReuse ||
                (check.TestResultAttemptId is { } recordedId && recordedId != attemptId))
            {
                unavailable.Add("trx-paths-from-another-attempt");
                continue;
            }

            foreach (var path in check.TestResultPaths)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    unavailable.Add("empty-trx-path");
                    continue;
                }

                string fullPath;
                try { fullPath = Path.GetFullPath(path); }
                catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException)
                {
                    unavailable.Add("invalid-trx-path");
                    continue;
                }
                if (!paths.Add(fullPath)) continue;
                try { ReadTrx(fullPath, durations, unavailable); }
                catch (Exception error) when (error is ArgumentException or NotSupportedException or FormatException)
                {
                    unavailable.Add($"trx-unreadable:{SingleToken(Path.GetFileName(fullPath))}");
                }
            }
        }

        if (durations.Count == 0)
            return "slowest=" + Unavailable(unavailable.Count == 0 ? "no-trx-paths" : string.Join(',', unavailable.Distinct()));

        var slowest = durations.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Take(3).Select(pair => FormattableString.Invariant($"{pair.Key} {pair.Value:0.###}s"));
        var omissions = unavailable.Count == 0 ? "" : $" trx_unavailable=[{string.Join(", ", unavailable.Distinct())}]";
        return $"slowest=[{string.Join(", ", slowest)}]{omissions}";
    }

    private static void ReadTrx(string path, Dictionary<string, double> durations, List<string> unavailable)
    {
        var (results, _) = ImpactReplayTrxDurations.Read(path, []);
        var executed = results.Where(result => !string.IsNullOrWhiteSpace(result.ClassName) && result.Seconds is not null)
            .ToArray();
        foreach (var (name, seconds) in executed)
            durations[name!] = durations.GetValueOrDefault(name!) + seconds!.Value;

        var reason = results.Count == 0 ? AcceptanceTrxFailureReader.Read(path).Status switch
        {
            AcceptanceTrxReadStatus.Missing => "trx-missing",
            AcceptanceTrxReadStatus.Unreadable => "trx-unreadable",
            AcceptanceTrxReadStatus.Unparseable => "trx-unparseable",
            _ => "no-executed-class-durations"
        } : executed.Length == 0 ? "no-executed-class-durations" :
            executed.Length < results.Count ? "incomplete-class-durations" : null;
        if (reason is not null)
            unavailable.Add($"{reason}:{SingleToken(Path.GetFileName(path))}");
    }

    private static string ReadReceipt(string path, string goalId, string lane, DateTimeOffset since, string? attemptId) =>
        ReadLatest(path, goalId, since, ReceiptJson, "no-lane-receipt-store", "no-lane-receipt-row",
            "lane-receipts-unreadable", (LaneMemoryRow row) =>
            {
                if (row.GoalId != goalId || row.RowKind is not null || !MatchesLane(row.Lane, lane) ||
                    (row.AttemptId is not null && row.AttemptId != attemptId))
                    return null;
                var memory = row.HostAvailableMemoryMb is double mb && mb >= 0 && double.IsFinite(mb)
                    ? FormattableString.Invariant($"{mb:0.###} MiB")
                    : Unavailable(row.HostAvailableMemoryMbUnavailableReason ?? "memory-not-recorded");
                return (row.RecordedAt, memory);
            });

    private static string ReadConcurrentGates(string? path, string goalPrefix, string lane, DateTimeOffset since)
    {
        if (path is null) return Unavailable("conduct-event-log-unbound");
        return ReadLatest(path, goalPrefix, since, JsonSerializerOptions.Web, "no-conduct-event-log",
            "no-shard-complete-event", "conduct-events-unreadable", (ConductEventRecord evt) =>
            {
                if (evt.EventKind != "gate-progress" || evt.GoalId != goalPrefix ||
                    evt.Detail?.StartsWith("PHASE_PROGRESS ", StringComparison.Ordinal) != true)
                    return null;

                string? phase = null, target = null, gates = null;
                foreach (Match field in ProgressFields.Matches(evt.Detail))
                {
                    var value = field.Groups["value"].Value;
                    switch (field.Groups["key"].Value)
                    {
                        case "phase": phase = value; break;
                        case "target": target = value.Trim('"').Replace("\\\"", "\"", StringComparison.Ordinal); break;
                        case "load_concurrent_gates": gates = value; break;
                    }
                }
                if (phase != "shard-complete" || !MatchesLane(target, lane)) return null;
                var sample = int.TryParse(gates, NumberStyles.None, CultureInfo.InvariantCulture, out var count)
                    ? count.ToString(CultureInfo.InvariantCulture)
                    : Unavailable("gate-count-not-recorded");
                return (evt.Timestamp, sample);
            });
    }

    private static string ReadLatest<T>(
        string path, string goalKey, DateTimeOffset since, JsonSerializerOptions json, string missing, string unmatched,
        string unreadable, Func<T, (DateTimeOffset At, string Value)?> select) where T : class
    {
        (DateTimeOffset At, string Value)? latest = null;
        var malformed = false;
        try
        {
            if (!File.Exists(path)) return Unavailable(missing);
            foreach (var line in SharedJsonlFile.ReadLines(path))
            {
                if (!line.Contains(goalKey, StringComparison.Ordinal)) continue;
                try
                {
                    if (JsonSerializer.Deserialize<T>(line, json) is { } row && select(row) is { } sample &&
                        sample.At >= since && (latest is null || sample.At >= latest.Value.At))
                        latest = sample;
                }
                catch (JsonException) { malformed = true; }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Unavailable(unreadable);
        }
        return latest?.Value ?? Unavailable(malformed ? unreadable : unmatched);
    }

    private static bool MatchesLane(string? recorded, string lane) =>
        !string.IsNullOrWhiteSpace(recorded) &&
        AcceptanceArtifactFileNames.Slug(recorded).Equals(lane, StringComparison.OrdinalIgnoreCase);

    private static string Unavailable(string reason) => $"unavailable({SingleToken(reason)})";

    private static string SingleToken(string value) => Regex.Replace(value, @"[\s;]+", "-");

    private sealed record LaneMemoryRow(
        string? GoalId, string? Lane, string? RowKind, string? AttemptId, DateTimeOffset RecordedAt,
        double? HostAvailableMemoryMb, string? HostAvailableMemoryMbUnavailableReason);
}
