using System.Globalization;
using System.Text;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ImpactReplayClassLevelReportWriter
{
    internal static void AppendSummary(StringBuilder text, ImpactReplayClassLevelSummary summary)
    {
        text.AppendLine($"- Class-level seconds saved (total): {Number(summary.TotalSecondsSaved)}");
        text.AppendLine($"- Class-level seconds saved (median per attempt): {Number(summary.MedianSecondsSaved)}");
        text.AppendLine($"- Share of non-floor test seconds saved at class level: {(summary.ShareOfNonFloorTestSeconds is { } share ? share.ToString("P1", CultureInfo.InvariantCulture) : "unavailable")}");
        text.AppendLine($"- Class-level coverage: {summary.AttemptsMeasured} attempts measured, {summary.AttemptsExcludedForIncompleteTrx} excluded for incomplete TRX, {summary.AttemptsWithUnresolvedSelection} with unresolved selection");
        text.AppendLine("- Class-level CPU saved: unavailable (no per-class CPU data was read for any attempt)");
        text.AppendLine("- Gate test durations include contention with other lanes. Duration savings are an upper estimate of wall-time savings; CPU data, where available, provides a better estimate of CPU savings.");
    }

    internal static void AppendAttempts(StringBuilder text, IReadOnlyList<ImpactReplayAttemptRecord> attempts)
    {
        text.AppendLine();
        text.AppendLine("### Class-level savings by attempt");
        text.AppendLine();
        text.AppendLine("| Attempt | Class-level seconds saved | Non-floor test seconds | CPU saved |");
        text.AppendLine("| --- | --- | --- | --- |");
        foreach (var attempt in attempts)
        {
            var reason = attempt.ClassLevelUnavailableReason;
            var savings = attempt.ClassLevelSecondsSaved is null && reason is not null
                ? $"unavailable ({Cell(reason)})" : Number(attempt.ClassLevelSecondsSaved);
            text.AppendLine($"| {Cell(attempt.AttemptId)} | {savings} | {Number(attempt.ClassLevelNonFloorTestSeconds)} | unavailable |");
        }
    }

    private static string Number(double? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "unavailable";
    private static string Cell(string value) => value.Replace("|", "\\|", StringComparison.Ordinal).Replace('\r', ' ').Replace('\n', ' ');
}
