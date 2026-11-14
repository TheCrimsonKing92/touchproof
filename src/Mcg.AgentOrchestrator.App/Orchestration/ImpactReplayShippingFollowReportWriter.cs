using System.Globalization;
using System.Text;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ImpactReplayShippingFollowReportWriter
{
    internal static void Append(StringBuilder text, IReadOnlyList<ImpactReplayAttemptRecord> attempts)
    {
        if (!attempts.Any(attempt => attempt.ShippingFollowAddedClasses is not null ||
                attempt.ShippingFollowAddedLanes is not null || attempt.ShippingFollowAddedSeconds.HasValue ||
                attempt.ShippingFollowRemainingSavingPercent.HasValue || attempt.ShippingFollowAttributions is not null ||
                attempt.ShippingFollowClassesUnavailableReason is not null ||
                attempt.ShippingFollowSecondsUnavailableReason is not null ||
                attempt.ShippingFollowRemainingSavingUnavailableReason is not null)) return;

        text.AppendLine();
        text.AppendLine("## Hop-1 shipping consumers followed in all-referencing mode");
        text.AppendLine();
        Distribution(text, "Added classes", attempts.Select(attempt =>
            attempt.ShippingFollowAddedClasses is { } classes ? (double?)classes.Count : null).ToArray(), "");
        Distribution(text, "Remaining saving", attempts.Select(attempt =>
            attempt.ShippingFollowRemainingSavingPercent).ToArray(), "%");
        text.AppendLine();
        text.AppendLine("| Attempt | Added classes | Added lanes | Added seconds | Saving remaining |");
        text.AppendLine("| --- | ---: | --- | ---: | ---: |");
        foreach (var attempt in attempts.OrderBy(item => item.AttemptId, StringComparer.Ordinal))
        {
            var count = attempt.ShippingFollowAddedClasses is { } classes
                ? classes.Count.ToString(CultureInfo.InvariantCulture)
                : Unavailable(attempt.ShippingFollowClassesUnavailableReason);
            var lanes = attempt.ShippingFollowAddedLanes is { } names
                ? names.Count.ToString(CultureInfo.InvariantCulture) +
                    (names.Count == 0 ? "" : ": " + Cell(string.Join(", ", names.Order(StringComparer.Ordinal))))
                : Unavailable(attempt.ShippingFollowClassesUnavailableReason);
            var seconds = Metric(attempt.ShippingFollowAddedSeconds, attempt.ShippingFollowSecondsUnavailableReason);
            var saving = Metric(attempt.ShippingFollowRemainingSavingPercent,
                attempt.ShippingFollowRemainingSavingUnavailableReason, "%");
            text.AppendLine($"| {Cell(attempt.AttemptId)} | {count} | {lanes} | {seconds} | {saving} |");
        }
        text.AppendLine();
        text.AppendLine("### Top shipping types by added classes");
        text.AppendLine();
        text.AppendLine("Associations are deduplicated per attempt/type/class and summed across attempts; a class may have multiple shipping types.");
        text.AppendLine();
        text.AppendLine("| Shipping type | Added classes (summed per attempt) |");
        text.AppendLine("| --- | ---: |");
        var ranked = attempts.SelectMany(attempt => (attempt.ShippingFollowAttributions ?? [])
                .GroupBy(item => item.ShippingType, StringComparer.Ordinal)
                .Select(group => (Type: group.Key, Count: group.SelectMany(item => item.AddedClasses)
                    .Distinct(StringComparer.Ordinal).Count())))
            .GroupBy(item => item.Type, StringComparer.Ordinal)
            .Select(group => (Type: group.Key, Count: group.Sum(item => item.Count)))
            .Where(item => item.Count > 0).OrderByDescending(item => item.Count)
            .ThenBy(item => item.Type, StringComparer.Ordinal).Take(10).ToArray();
        foreach (var item in ranked)
            text.AppendLine($"| {Cell(item.Type)} | {item.Count.ToString(CultureInfo.InvariantCulture)} |");
        if (ranked.Length == 0) text.AppendLine("No added-class attributions recorded.");
        text.AppendLine();
    }

    private static void Distribution(StringBuilder text, string label, double?[] values, string suffix)
    {
        var available = values.Where(value => value.HasValue).Select(value => value!.Value).Order().ToArray();
        var unavailable = values.Length - available.Length;
        if (available.Length == 0)
        {
            text.AppendLine($"- {label}: minimum unavailable; median unavailable; 90th percentile unavailable; maximum unavailable; unavailable: {unavailable}");
            return;
        }
        var middle = available.Length / 2;
        var median = available.Length % 2 == 0 ? (available[middle - 1] + available[middle]) / 2 : available[middle];
        var p90 = available[(int)Math.Ceiling(available.Length * 0.9) - 1];
        text.AppendLine($"- {label}: minimum {Number(available[0])}{suffix}; median {Number(median)}{suffix}; " +
            $"90th percentile {Number(p90)}{suffix}; maximum {Number(available[^1])}{suffix}; unavailable: {unavailable}");
    }

    private static string Metric(double? value, string? reason, string suffix = "") =>
        value.HasValue ? Number(value.Value) + suffix : Unavailable(reason);
    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private static string Unavailable(string? reason) => "unavailable: " + Cell(reason ?? "measurement not recorded");
    private static string Cell(string value) => value.Replace("|", "\\|", StringComparison.Ordinal)
        .Replace('\r', ' ').Replace('\n', ' ');
}
