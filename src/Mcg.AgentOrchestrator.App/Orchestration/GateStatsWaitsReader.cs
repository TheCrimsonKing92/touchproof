using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Optional timing JSON is parsed at observation granularity, never by deserializing the
// verdict/attempt DTO. One malformed number cannot discard another stage or verdict.
internal static class GateStatsWaitsReader
{
    internal sealed record Attempt(string Id, DateTimeOffset Time,
        IReadOnlyList<AcceptanceLaneWaitOccurrence> Occurrences, double? GateWallSeconds);
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private static readonly Regex AttemptName = new(@"^.+-(?<time>\d{17})-[0-9a-f]{32}$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    internal static IReadOnlyList<Attempt> Read(string directory, int last, out int skipped)
    {
        var trx = GateStatsReader.DiscoverWaitRuns(directory, out skipped);
        var prefixes = trx.Select(row => row.Attempt).ToHashSet(StringComparer.Ordinal);
        var root = Path.Combine(directory, "acceptance-gate-attempts");
        try
        {
            if (Directory.Exists(root))
                foreach (var goal in Directory.GetDirectories(root))
                foreach (var path in Directory.GetFiles(goal, "*.json"))
                    if (path.EndsWith(".attempt.json", StringComparison.Ordinal) || path.EndsWith(".result.json", StringComparison.Ordinal))
                        prefixes.Add(path[..path.LastIndexOf('.', path.Length - 6)]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
        var selected = prefixes.Select(prefix => (Prefix: prefix, Time: Time(prefix)))
            .Where(row => row.Time is not null).OrderByDescending(row => row.Time)
            .ThenBy(row => row.Prefix, StringComparer.Ordinal).Take(last).ToArray();
        var wall = ReadWall(directory, ref skipped);
        var attempts = new List<Attempt>();
        foreach (var selectedAttempt in selected)
        {
            var prefix = selectedAttempt.Prefix;
            var id = Path.GetFileName(prefix);
            var occurrences = new Dictionary<string, AcceptanceLaneWaitOccurrence>(StringComparer.Ordinal);
            foreach (var suffix in new[] { ".result.json", ".attempt.json" })
            {
                var path = prefix + suffix;
                if (!File.Exists(path)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(ReadShared(path));
                    var container = doc.RootElement;
                    if (suffix == ".result.json") container = Property(container, "acceptance");
                    if (container.ValueKind != JsonValueKind.Object) continue;
                    var bound = Text(container, "attemptId") == id;
                    var rows = Property(container, "laneWaits");
                    if (rows.ValueKind != JsonValueKind.Array) continue;
                    foreach (var row in rows.EnumerateArray())
                    {
                        var occurrence = Parse(row, id, bound);
                        if (occurrence is null) { skipped++; continue; }
                        if (occurrences.TryGetValue(occurrence.OccurrenceId, out var previous))
                        {
                            if (previous.Lane != occurrence.Lane || previous.Route != occurrence.Route ||
                                previous.Retry != occurrence.Retry || previous.InvocationId != occurrence.InvocationId)
                                occurrences[previous.OccurrenceId] = previous with { Measurements = [] };
                        }
                        else occurrences.Add(occurrence.OccurrenceId, occurrence);
                    }
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { skipped++; }
            }
            foreach (var run in trx.Where(row => row.Attempt == prefix))
                AddLegacy(run.Lane, run.Retry);
            attempts.Add(new(id, selectedAttempt.Time!.Value, occurrences.Values.ToArray(), wall.GetValueOrDefault(id)));

            void AddLegacy(string lane, int retry)
            {
                if (occurrences.Values.Any(o => AcceptanceArtifactFileNames.Slug(o.Lane) == AcceptanceArtifactFileNames.Slug(lane) && o.Retry == retry)) return;
                var key = $"legacy:{lane}:{retry}";
                occurrences.TryAdd(key, new(id, key, lane, "unknown", retry, null, [], []));
            }
        }
        return attempts;
    }

    private static AcceptanceLaneWaitOccurrence? Parse(JsonElement row, string attempt, bool bound)
    {
        var lane = Text(row, "lane");
        var route = Text(row, "route");
        var id = Text(row, "occurrenceId");
        if (string.IsNullOrWhiteSpace(lane) || string.IsNullOrWhiteSpace(id) ||
            !Property(row, "retry").TryInt(out var retry) || retry < 0) return null;
        bound &= route is "local" or "remote";
        if (route is not ("local" or "remote")) route = "unknown";
        var stages = Property(row, "applicableStages");
        var applicable = stages.ValueKind == JsonValueKind.Array ? stages.EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()!).ToArray() : [];
        var measurements = new List<AcceptanceLaneWaitMeasurement>();
        var invocation = Text(row, "invocationId");
        bound &= Property(row, "invocationId").ValueKind is JsonValueKind.String or JsonValueKind.Null or JsonValueKind.Undefined;
        var invocationName = AcceptanceArtifactFileNames.Slug(lane) + ":" + retry.ToString(CultureInfo.InvariantCulture);
        bound &= invocation is null || invocation == $"{attempt}:{invocationName}" ||
            invocation == $"{attempt}:{attempt}.{invocationName}";
        var values = Property(row, "measurements");
        if (bound && Text(row, "attemptId") == attempt && values.ValueKind == JsonValueKind.Array)
            foreach (var value in values.EnumerateArray())
            {
                try
                {
                    var measurement = value.Deserialize<AcceptanceLaneWaitMeasurement>(Options);
                    if (measurement is not null) measurements.Add(measurement);
                }
                catch (JsonException) { /* Only this observation is unknown. */ }
            }
        return new(attempt, id, lane, route, retry, invocation, applicable, measurements);
    }

    private static Dictionary<string, double?> ReadWall(string directory, ref int skipped)
    {
        var values = new Dictionary<string, double?>(StringComparer.Ordinal);
        var path = Path.Combine(directory, GateLaneTimeLedger.FileName);
        try
        {
            if (!File.Exists(path)) return values;
            foreach (var line in SharedJsonlFile.ReadLines(path))
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var row = doc.RootElement;
                    var id = Text(row, "gateAttemptId");
                    var ms = Property(row, "totalMs");
                    if (id is not null && ms.ValueKind == JsonValueKind.Number && ms.TryGetDouble(out var number) && double.IsFinite(number) && number >= 0)
                        values[id] = values.ContainsKey(id) ? null : number / 1000;
                }
                catch (JsonException) { skipped++; }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
        return values;
    }

    private static DateTimeOffset? Time(string prefix) => AttemptName.Match(Path.GetFileName(prefix)) is { Success: true } match &&
        DateTimeOffset.TryParseExact(match.Groups["time"].Value, "yyyyMMddHHmmssfff", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var time) ? time : null;
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    private static JsonElement Property(JsonElement parent, string name) => parent.ValueKind == JsonValueKind.Object &&
        parent.TryGetProperty(name, out var value) ? value : default;
    private static string? Text(JsonElement parent, string name) => Property(parent, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
    private static bool TryInt(this JsonElement value, out int number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out number);
    }
}
