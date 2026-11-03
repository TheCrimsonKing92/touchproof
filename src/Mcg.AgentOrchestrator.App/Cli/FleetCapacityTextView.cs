using System.Globalization;
using System.Text;
using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class FleetCapacityTextView
{
    internal static string Render(FleetCapacityResult result)
    {
        var text = new StringBuilder();
        text.AppendLine($"Fleet capacity observed at {result.ObservedAt:O}");
        text.AppendLine("Recorded task workers; not confirmed live processes or paid sessions.");
        text.AppendLine("Observation time does not establish persisted process-flag freshness.");
        foreach (var project in result.Projects)
        {
            var aliases = project.Aliases.Count == 0 ? "" : $" (aliases: {Cell(string.Join(", ", project.Aliases.Take(4)))}{(project.Aliases.Count > 4 ? $", +{project.Aliases.Count - 4} more" : "")})";
            text.AppendLine($"{Cell(project.Project)}{aliases}: {project.Availability}; recorded workers={Number(project.PersistedRunningWorkers)}, configured local cap={Number(project.ConfiguredProjectWorkerCap)}");
            foreach (var error in project.Errors)
                text.AppendLine($"  {error.Metric}: {error.Code}: {Cell(error.Message)}");
        }
        var subset = result.Complete ? "Known totals" : "Known partial totals";
        text.AppendLine($"{subset}: recorded workers={result.KnownPersistedRunningWorkers}; theoretical sum of configured local limits={result.TheoreticalConfiguredProjectWorkerCapSum}.");
        text.AppendLine("Local-limit sum is not a shared host limit or guaranteed available slots.");
        text.AppendLine($"Complete={result.Complete.ToString().ToLowerInvariant()}; unavailable emitted projects={result.UnavailableProjectCount}; catalog complete={result.CatalogComplete.ToString().ToLowerInvariant()}.");
        foreach (var error in result.Errors) text.AppendLine($"{error.Code}: {Cell(error.Message)}");
        return text.ToString().TrimEnd();
    }

    private static string Number(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
    private static string Cell(string value) => new string(value.Take(120).Select(ch => char.IsControl(ch) ? ' ' : ch).ToArray()) +
        (value.Length > 120 ? "..." : "");
}
