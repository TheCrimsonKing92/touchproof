using System.Globalization;
using Mcg.AgentOrchestrator.App.Cli;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleCostGoalOutcomeLines
{
    internal static IEnumerable<string> DetailLines(IReadOnlyList<CostGoalOutcomeAttentionView.Row>? rows)
    {
        if (rows is not { Count: > 0 })
            yield break;

        yield return "Cost-goal outcomes:";
        foreach (var row in rows)
        {
            if (row.Unreadable)
            {
                yield return $"  state={row.State} reason={row.Reason}";
                continue;
            }

            var line = FormattableString.Invariant(
                $"  line={row.LineNumber} metric={row.Metric} state={row.State} before={Number(row.Before) ?? "unavailable"} after={Number(row.After) ?? "unavailable"} target={row.Target} direction={row.Direction} beforeSamples={row.BeforeSampleSize} afterSamples={row.AfterSampleSize}");
            yield return Progress(row) is { } progress ? line + " progress=" + progress : line;
        }
    }

    internal static IEnumerable<OwnerConsoleField> Fields(IReadOnlyList<CostGoalOutcomeAttentionView.Row>? rows)
    {
        if (rows is null)
            yield break;

        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var key = FormattableString.Invariant($"outcome/{index}/");
            yield return new(key + "state", "Outcome state", row.State);
            if (row.Unreadable)
            {
                yield return new(key + "reason", "Outcome reason", row.Reason);
                continue;
            }

            yield return new(key + "line", "Outcome line", row.LineNumber?.ToString(CultureInfo.InvariantCulture));
            yield return new(key + "metric", "Outcome metric", row.Metric);
            yield return new(key + "before", "Outcome before", Number(row.Before));
            yield return new(key + "after", "Outcome after", Number(row.After));
            yield return new(key + "target", "Outcome target", row.Target);
            yield return new(key + "direction", "Outcome direction", row.Direction);
            yield return new(key + "before-samples", "Outcome before samples", row.BeforeSampleSize?.ToString(CultureInfo.InvariantCulture));
            yield return new(key + "after-samples", "Outcome after samples", row.AfterSampleSize?.ToString(CultureInfo.InvariantCulture));
            if (row is { K: { } k, N: { } n })
            {
                yield return new(key + "k", "Outcome pending samples", k.ToString(CultureInfo.InvariantCulture));
                yield return new(key + "n", "Outcome required samples", n.ToString(CultureInfo.InvariantCulture));
            }
        }
    }

    internal static string? Summary(IReadOnlyList<CostGoalOutcomeAttentionView.Row> rows)
    {
        if (rows.Count == 0)
            return null;
        if (rows.Any(row => row.Unreadable))
            return "outcomes unreadable";

        var summary = FormattableString.Invariant(
            $"outcomes pending={rows.Count(row => row.State == "pending")} met={rows.Count(row => row.State == "met")} missed={rows.Count(row => row.State == "missed")} insufficient-data={rows.Count(row => row.State == "insufficient-data")}");
        var progress = new List<string>();
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            if (Progress(row) is { } value)
                progress.Add(FormattableString.Invariant($"line{row.LineNumber ?? index + 1}:{value}"));
        }

        return progress.Count == 0 ? summary : summary + " progress=" + string.Join(",", progress);
    }

    private static string? Progress(CostGoalOutcomeAttentionView.Row row) =>
        row is { K: { } k, N: { } n } ? FormattableString.Invariant($"{k}/{n}") : null;

    private static string? Number(double? value) => value?.ToString("R", CultureInfo.InvariantCulture);
}
