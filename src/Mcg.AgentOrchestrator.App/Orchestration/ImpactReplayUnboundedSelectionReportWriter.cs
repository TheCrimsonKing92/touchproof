using System.Globalization;
using System.Text;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ImpactReplayUnboundedSelectionReportWriter
{
    internal static void Append(StringBuilder text, IReadOnlyList<ImpactReplayAttemptRecord> attempts)
    {
        var groups = attempts.Where(attempt => attempt.SelectionOutcome == "Abandoned" &&
                ImpactReplayUnboundedSelectionMeasurement.IsBound(attempt.DegradationKind))
            .GroupBy(attempt => attempt.DegradationKind!)
            .OrderBy(group => group.Key, StringComparer.Ordinal).ToArray();
        if (groups.Length == 0) return;
        text.AppendLine();
        text.AppendLine("## Unbounded selections for bound abandonments");
        text.AppendLine();
        foreach (var group in groups)
            text.AppendLine($"- {Cell(group.Key)}: {group.Count()} attempts; " +
                $"class count unavailable: {group.Count(item => item.UnboundedSelectedClassCount is null)}; " +
                $"duration share unavailable: {group.Count(item => item.UnboundedNonFloorTestSecondsShare is null)}");
        text.AppendLine();
        text.AppendLine("| Abandonment kind | Attempt | Unbounded selected classes | Share of non-floor test seconds |");
        text.AppendLine("| --- | --- | ---: | ---: |");
        foreach (var group in groups)
        foreach (var attempt in group.OrderBy(item => item.AttemptId, StringComparer.Ordinal))
        {
            var count = attempt.UnboundedSelectedClassCount?.ToString(CultureInfo.InvariantCulture)
                ?? Unavailable(attempt.UnboundedClassCountUnavailableReason);
            var share = attempt.UnboundedNonFloorTestSecondsShare is { } value
                ? (value * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%"
                : Unavailable(attempt.UnboundedDurationShareUnavailableReason);
            text.AppendLine($"| {Cell(group.Key)} | {Cell(attempt.AttemptId)} | {count} | {share} |");
        }
        text.AppendLine();
    }

    private static string Unavailable(string? reason) => "unavailable: " + Cell(reason ?? "measurement not recorded");
    private static string Cell(string value) => value.Replace("|", "\\|", StringComparison.Ordinal)
        .Replace('\r', ' ').Replace('\n', ' ');
}
