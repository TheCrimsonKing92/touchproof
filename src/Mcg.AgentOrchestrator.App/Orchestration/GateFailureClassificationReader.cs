using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// All state belongs to this observation. Artifact errors remain visible rather than
// turning an incomplete window into an apparently successful empty census.
internal static class GateFailureClassificationReader
{
    internal sealed record Attribution(string CheckName, int? Origin, int? Cause, string? Evidence);
    internal sealed record Attempt(string Id, string GoalPrefix, DateTimeOffset? StartedAt,
        string? Kind, string? FaultKind, string? State, string? EarlyOutcomeKind, string? InfrastructureReasonCode,
        bool? Passed, IReadOnlyList<string> FailedChecks, IReadOnlyList<Attribution> Attributions,
        IReadOnlyList<GateStatsReader.LaneRun> Runs, string? Log, IReadOnlyList<string> Problems,
        IReadOnlyList<string> ResultProblems, string? Exclusion);

    internal static IReadOnlyList<Attempt> Read(string directory, int last, ICollection<string> problems, out int skipped)
    {
        // Match GateStatsWaitsReader.Read's TRX/result/attempt union and ordering.
        // TRX-only discovery would hide identity-stale and pre-test apparatus faults.
        var trx = GateStatsReader.DiscoverWaitRuns(directory, out skipped);
        if (skipped > 0) problems.Add($"TRX discovery skipped {skipped} unreadable or unrecognized entries");
        var prefixes = trx.Select(run => run.Attempt).ToHashSet(StringComparer.Ordinal);
        var root = Path.Combine(directory, "acceptance-gate-attempts");
        if (File.Exists(root)) problems.Add($"{root}: discovery requires a directory, found a file");
        try
        {
            foreach (var goal in Directory.GetDirectories(root))
            {
                try
                {
                    foreach (var path in Directory.GetFiles(goal, "*.json"))
                        if (path.EndsWith(".attempt.json", StringComparison.Ordinal) || path.EndsWith(".result.json", StringComparison.Ordinal))
                            prefixes.Add(path[..path.LastIndexOf('.', path.Length - 6)]);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { problems.Add($"{goal}: discovery {ex.GetType().Name}"); }
            }
        }
        catch (DirectoryNotFoundException) { /* No home gate attempts yet. */ }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { problems.Add($"{root}: discovery {ex.GetType().Name}"); }
        return prefixes.Select(prefix => (Prefix: prefix, Time: Time(prefix)))
            .Where(row => row.Time is not null).OrderByDescending(row => row.Time)
            .ThenBy(row => row.Prefix, StringComparer.Ordinal).Take(last)
            .Select(row => ReadAttempt(row.Prefix, row.Time)).ToArray();
    }

    private static Attempt ReadAttempt(string prefix, DateTimeOffset? time)
    {
        var problems = new List<string>();
        var resultProblems = new List<string>();
        var result = ReadJson(prefix + ".result.json", resultProblems);
        var kind = Text(result, "kind");
        if (kind is null && resultProblems.Count == 0) resultProblems.Add("result.json: missing or invalid kind");
        var acceptance = Property(result, "acceptance");
        var passedField = Property(acceptance, "passed");
        bool? passed = passedField.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
        var failed = Property(acceptance, "failedChecks");
        var checks = failed.ValueKind == JsonValueKind.Array ? failed.EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()!).ToArray() : [];
        if (kind == "accepted" && (passed is null || failed.ValueKind != JsonValueKind.Array ||
            failed.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String)))
            resultProblems.Add("result.json: missing or invalid acceptance.passed/failedChecks");
        var attributions = new List<Attribution>();
        var values = Property(acceptance, "checkAttributions");
        if (values.ValueKind == JsonValueKind.Array)
            foreach (var value in values.EnumerateArray())
            {
                var name = Text(value, "checkName");
                var origin = Number(value, "origin");
                var cause = Number(value, "cause");
                if (name is null || origin is null or < 0 or > 2 || cause is null or < 0 or > 2)
                    resultProblems.Add($"result.json: invalid attribution checkName/origin/cause: {value.GetRawText()}");
                attributions.Add(new(name ?? "unknown", origin, cause, Text(value, "evidence")));
            }
        else if (values.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            resultProblems.Add("result.json: invalid acceptance.checkAttributions");

        // attempt.json is optional: the attempt id also records its start stamp and goal prefix.
        var metadata = File.Exists(prefix + ".attempt.json") ? ReadJson(prefix + ".attempt.json", problems) : default;
        // Attempt metadata calls this 'kind' (:130); result artifacts call it
        // 'dispatchKind' (:3566), in ConductorParallelAcceptanceAttempts.cs.
        var dispatch = Text(metadata, "kind") ?? Text(result, "dispatchKind");
        var exclusion = kind == "focused-evidence" ? "kind=focused-evidence" :
            dispatch is not null and not "gate" ? $"dispatchKind={dispatch}" : null;
        if (Text(metadata, "startedAt") is { } started)
        {
            if (DateTimeOffset.TryParse(started, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) time = parsed;
            else problems.Add("attempt.json: invalid startedAt; using recorded attempt id timestamp");
        }
        var id = Path.GetFileName(prefix);
        var goalPrefix = id.Split('-')[0];
        var runs = GateStatsReader.ReadAttemptRuns(prefix, problems);
        string? log = null;
        // Early/fault artifacts can precede every lane and legitimately have no log/TRX.
        if (File.Exists(prefix + ".out.log") || kind == "accepted")
            log = ReadText(prefix + ".out.log", problems);
        if (kind == "accepted" && runs.Count == 0 && passed != true)
            problems.Add($"{id} TRX: no readable first-run or rerun test outcomes recorded");
        return new(id, goalPrefix, time, kind, Text(result, "faultKind"), Text(result, "state"),
            Text(result, "earlyOutcomeKind"), Text(result, "infrastructureReasonCode"), passed, checks,
            attributions, runs, log, problems, resultProblems, exclusion);
    }

    private static DateTimeOffset? Time(string prefix)
    {
        var match = Regex.Match(Path.GetFileName(prefix), @"^.+-(?<time>\d{17})-[0-9a-f]{32}$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        return match.Success && DateTimeOffset.TryParseExact(match.Groups["time"].Value, "yyyyMMddHHmmssfff",
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time) ? time : null;
    }

    private static JsonElement ReadJson(string path, ICollection<string> problems)
    {
        var text = ReadText(path, problems);
        if (text is null) return default;
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind == JsonValueKind.Object) return document.RootElement.Clone();
            problems.Add($"{Path.GetFileName(path)}: expected JSON object");
        }
        catch (JsonException) { problems.Add($"{Path.GetFileName(path)}: malformed JSON"); }
        return default;
    }

    private static string? ReadText(string path, ICollection<string> problems)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"{Path.GetFileName(path)}: {ex.GetType().Name} (missing, locked or unreadable evidence; attempt may still be running)");
            return null;
        }
    }

    private static JsonElement Property(JsonElement parent, string name) => parent.ValueKind == JsonValueKind.Object &&
        parent.TryGetProperty(name, out var value) ? value : default;
    private static string? Text(JsonElement parent, string name) => Property(parent, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
    private static int? Number(JsonElement parent, string name) => Property(parent, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var number) ? number : null;
}
