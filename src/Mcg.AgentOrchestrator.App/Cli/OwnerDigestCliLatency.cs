using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Cli;

internal sealed record CliLatencyPath(string Path, int Count, double? P50Ms, double? P95Ms);
internal sealed record CliLatencyReport(IReadOnlyList<CliLatencyPath> Paths, int BuiltCount, int UnclassifiedCount,
    int SkippedRecords, int UnavailableInputs);

internal static class OwnerDigestCliLatency
{
    internal static CliLatencyReport Read(string directory, DateTimeOffset since, DateTimeOffset until)
    {
        var input = OwnerDigestMeasurementLogs.Read(directory, "cli-timing*.jsonl");
        var samples = new Dictionary<string, List<double>> { ["fast"] = [], ["fresh"] = [], ["built"] = [] };
        var unclassified = 0;
        var skipped = input.Skipped;
        foreach (var item in input.Records.Where(r => r.Timestamp >= since && r.Timestamp < until))
        {
            var path = OwnerDigestMeasurementLogs.String(item.Data, "launcher_path");
            if (path is null || !samples.TryGetValue(path, out var values)) { unclassified++; continue; }
            if (!item.Data.TryGetProperty("launcher_elapsed_ms", out var elapsed) || elapsed.ValueKind != JsonValueKind.Number ||
                !elapsed.TryGetDouble(out var ms) || !double.IsFinite(ms) || ms < 0)
            { skipped++; continue; }
            values.Add(ms);
        }
        var paths = samples.Select(pair =>
        {
            var sorted = pair.Value.Order().ToArray();
            double? Percentile(double p) => sorted.Length == 0 ? null : sorted[(int)Math.Ceiling(p * sorted.Length) - 1];
            return new CliLatencyPath(pair.Key, sorted.Length, Percentile(.5), Percentile(.95));
        }).ToArray();
        return new(paths, samples["built"].Count, unclassified, skipped, input.Unavailable);
    }
}
