using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CostGoalOutcomeAttentionView
{
    internal sealed record Row(string GoalId, string State)
    {
        public int? Line { get; init; }
        public int? LineNumber { get; init; }
        public string? Metric { get; init; }
        public double? Before { get; init; }
        public double? After { get; init; }
        public string? Target { get; init; }
        public string? Direction { get; init; }
        public int? BeforeSampleSize { get; init; }
        public int? AfterSampleSize { get; init; }
        public int? Window { get; init; }
        public int? K { get; init; }
        [JsonPropertyName("N")]
        public int? N { get; init; }
        public bool Unreadable { get; init; }
        public string? Reason { get; init; }
    }

    internal static IReadOnlyList<Row> Read(IEnumerable<string> landedGoalIds, string orchestratorDirectory)
    {
        var store = new CostGoalOutcomeStore(orchestratorDirectory);
        return Array.AsReadOnly(landedGoalIds.Order(StringComparer.Ordinal)
            .SelectMany(goalId => ReadGoal(store, goalId)).ToArray());
    }

    private static Row[] ReadGoal(CostGoalOutcomeStore store, string goalId)
    {
        try
        {
            var record = store.Read(new GoalId(goalId));
            if (record is null)
                return [];
            if (record.Filing?.Baselines is null || record.FinalVerdicts is null || record.PendingResults is null)
                throw new InvalidDataException();

            var results = record.FinalVerdicts.Concat(record.PendingResults).ToArray();
            if (results.Any(result => result?.Line is null || !Enum.IsDefined(result.State)))
                throw new InvalidDataException();

            // The two lists are state partitions. The stored index restores the original line order.
            return results.DistinctBy(result => result.LineIndex).OrderBy(result => result.LineIndex)
                .Select(result => ToRow(goalId, result)).ToArray();
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException
            or UnauthorizedAccessException or NotSupportedException or FormatException)
        {
            return [new Row(goalId, "unreadable")
            {
                Unreadable = true,
                Reason = $"unreadable outcome record: {ex.GetType().Name}"
            }];
        }
    }

    private static Row ToRow(string goalId, CostGoalOutcomeResult result)
    {
        var pending = result.State == CostGoalOutcomeState.Pending;
        var state = result.State == CostGoalOutcomeState.InsufficientData
            ? "insufficient-data"
            : result.State.ToString().ToLowerInvariant();
        return new Row(goalId, state)
        {
            Line = result.LineIndex,
            LineNumber = result.LineIndex + 1,
            Metric = result.Line.Name,
            Before = result.Before,
            After = result.After,
            Target = result.Line.Target,
            Direction = result.Line.Direction,
            BeforeSampleSize = result.BeforeSampleSize,
            AfterSampleSize = result.AfterSampleSize,
            Window = result.Line.Window,
            K = pending ? result.AfterSampleSize : null,
            N = pending ? result.Line.Window : null,
            Reason = result.Reason
        };
    }

    internal static Dictionary<string, object?> WithRows(
        Dictionary<string, object?> payload, IReadOnlyList<Row>? rows, bool includeCounts)
    {
        if (rows is not { Count: > 0 })
            return payload;

        payload.Add("costGoalOutcomes", rows);
        if (includeCounts)
        {
            payload.Add("costGoalOutcomeCounts", new
            {
                pending = rows.Count(row => row.State == "pending"),
                met = rows.Count(row => row.State == "met"),
                missed = rows.Count(row => row.State == "missed"),
                insufficientData = rows.Count(row => row.State == "insufficient-data")
            });
        }
        return payload;
    }

    internal static void Print(TextWriter writer, IReadOnlyList<Row> rows)
    {
        if (rows.Count == 0)
            return;

        writer.WriteLine("Cost-goal outcomes:");
        foreach (var row in rows)
        {
            if (row.Unreadable)
            {
                writer.WriteLine($"  goal={row.GoalId} state={row.State} reason={row.Reason}");
                continue;
            }

            var detail = FormattableString.Invariant(
                $"  goal={row.GoalId} line={row.LineNumber} metric={row.Metric} state={row.State} before={Format(row.Before)} after={Format(row.After)} target={row.Target} direction={row.Direction} beforeSamples={row.BeforeSampleSize} afterSamples={row.AfterSampleSize} window={row.Window}");
            if (row.K is not null)
                detail += FormattableString.Invariant($" progress={row.K}/{row.N}");
            writer.WriteLine(detail);
        }
    }

    private static string Format(double? value) => value?.ToString("R", CultureInfo.InvariantCulture) ?? "unavailable";
}
