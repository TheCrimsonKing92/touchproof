using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.Cli;

internal sealed record RelaunchMetric(int Count, double? TotalMs, double? MedianMs);
internal sealed record RelaunchMeasurements(IReadOnlyList<RelaunchWindow> Windows, int CompleteCount, int IncompleteCount,
    RelaunchMetric Drain, RelaunchMetric Restart, RelaunchMetric Probation, RelaunchMetric Combined,
    DateTimeOffset? EarliestRetainedEvent, int SkippedRecords, int UnavailableInputs, CliLatencyReport CliLatency);

internal static class CliOwnerDigestRelaunch
{
    internal static RelaunchMeasurements Read(OrchestratorWorkspace workspace, DateTimeOffset since, DateTimeOffset until)
    {
        var input = OwnerDigestMeasurementLogs.Read(workspace.LogDirectory, "conduct-events*.log");
        var windows = OwnerDigestRelaunchWindowFold.Fold(input.Records.Where(r => r.Timestamp >= since && r.Timestamp < until));
        RelaunchMetric Metric(Func<RelaunchWindow, double?> select)
        {
            var values = windows.Select(select).OfType<double>().Order().ToArray();
            return values.Length == 0 ? new(0, null, null) : new(values.Length, values.Sum(),
                values.Length % 2 == 1 ? values[values.Length / 2] : (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2);
        }
        var complete = windows.Count(w => w.Status == "complete");
        return new(windows, complete, windows.Count - complete, Metric(w => w.DrainMs), Metric(w => w.RestartMs),
            Metric(w => w.ProbationMs), Metric(w => w.TotalMs), input.Records.FirstOrDefault()?.Timestamp,
            input.Skipped, input.Unavailable, OwnerDigestCliLatency.Read(workspace.LogDirectory, since, until));
    }

    internal static void AddJson(JsonObject root, RelaunchMeasurements report) => root["relaunch"] =
        JsonSerializer.SerializeToNode(report, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    internal static void WriteText(TextWriter writer, RelaunchMeasurements report)
    {
        static string Number(double? value) => value?.ToString("R", CultureInfo.InvariantCulture) ?? "n/a";
        static string At(DateTimeOffset? value) => value?.ToString("O", CultureInfo.InvariantCulture) ?? "n/a";
        writer.WriteLine($"Relaunch windows: complete={report.CompleteCount} incomplete={report.IncompleteCount}");
        writer.WriteLine("Scheduled UTC | Stop UTC | First tick UTC | Adoption UTC | Status | Drain ms | Restart ms | Probation ms | Total ms | Issues");
        foreach (var window in report.Windows)
            writer.WriteLine($"{At(window.Scheduled)} | {At(window.Stopped)} | {At(window.FirstTick)} | {At(window.Adopted)} | {window.Status} | {Number(window.DrainMs)} | {Number(window.RestartMs)} | {Number(window.ProbationMs)} | {Number(window.TotalMs)} | {string.Join(", ", window.Issues)}");
        foreach (var (name, metric) in new[] { ("drain", report.Drain), ("restart", report.Restart), ("probation", report.Probation), ("combined", report.Combined) })
            writer.WriteLine($"{name}: count={metric.Count} total_ms={Number(metric.TotalMs)} median_ms={Number(metric.MedianMs)}");
        writer.WriteLine($"Conduct inputs: earliest={At(report.EarliestRetainedEvent)} skipped={report.SkippedRecords} unavailable={report.UnavailableInputs}");
        writer.WriteLine("CLI latency: path | count | p50 ms | p95 ms");
        foreach (var path in report.CliLatency.Paths)
            writer.WriteLine($"{path.Path} | {path.Count} | {Number(path.P50Ms)} | {Number(path.P95Ms)}");
        writer.WriteLine($"CLI totals: built={report.CliLatency.BuiltCount} unclassified={report.CliLatency.UnclassifiedCount} skipped={report.CliLatency.SkippedRecords} unavailable={report.CliLatency.UnavailableInputs}");
    }
}
