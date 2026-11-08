using System.Globalization;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Terminal.Gui.Text;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Owns timing calculations and cell layout; the diagram owns boxes and field mapping.
internal static class OwnerFleetLaneTable
{
    internal static IReadOnlyList<string> Rows(IReadOnlyList<RemoteExecutorLaneSeconds> lanes, int columns, bool expanded)
    {
        var cells = lanes.OrderByDescending(lane => Cost(lane))
            .ThenBy(lane => lane.Lane, StringComparer.Ordinal)
            .Select(lane => new[] { Clean(lane.Lane), lane.Samples.ToString(CultureInfo.InvariantCulture),
                Number(lane.SoloMedianSeconds), Number(lane.ConcurrentMedianSeconds), RatioText(Ratio(lane)) }).ToArray();
        string[] header = ["lane", "n", "solo", "conc", "ratio"];
        var widths = Enumerable.Range(1, 4).Select(index => Math.Max(header[index].Length,
            cells.Select(row => row[index].Length).DefaultIfEmpty(0).Max())).ToArray();
        // Reserve a lane label even for unusually large numeric values.
        for (var index = 0; widths.Sum() + 8 > columns; index = (index + 1) % widths.Length)
            if (widths[index] > header[index + 1].Length) widths[index]--;
        var laneWidth = columns - widths.Sum() - 4;
        var result = new List<string> { Row(header) };
        var shown = expanded ? cells.Length : Math.Min(5, cells.Length);
        result.AddRange(cells.Take(shown).Select(Row));
        if (shown < cells.Length) result.Add($"+{cells.Length - shown} more lanes");
        return result;

        string Row(string[] values) => Pad(values[0], laneWidth, false) + " " + string.Join(" ",
            Enumerable.Range(1, 4).Select(index => Pad(values[index], widths[index - 1], true)));
    }

    internal static string Summary(IReadOnlyList<RemoteExecutorLaneSeconds> lanes,
        ExecutorSlotDecisionRecord? decision, int columns)
    {
        var eligible = lanes.Where(lane => lane.Samples > 0 && Ratio(lane).HasValue).ToArray();
        var weight = eligible.Sum(lane => (double)lane.Samples);
        var overall = weight > 0 ? eligible.Sum(lane => lane.Samples / weight * Ratio(lane)!.Value) : (double?)null;
        var prefix = $"slowdown {RatioText(overall)} decision ";
        if (decision is null) return OwnerConsoleLineFitter.Cut(prefix + "none", columns);
        var slots = FormattableString.Invariant($" {decision.Decision.CurrentSlots}->{decision.Decision.ProposedSlots}");
        return prefix + OwnerConsoleLineFitter.Cut(Clean(decision.Decision.Action),
            Math.Max(0, columns - prefix.GetColumns() - slots.Length)) + slots;
    }

    private static double Cost(RemoteExecutorLaneSeconds lane) =>
        double.IsFinite(lane.Samples * lane.MedianSeconds) ? lane.Samples * lane.MedianSeconds : double.NegativeInfinity;

    private static double? Ratio(RemoteExecutorLaneSeconds lane)
    {
        if (lane.SoloMedianSeconds is not { } solo || !double.IsFinite(solo) || solo <= 0 ||
            lane.ConcurrentMedianSeconds is not { } concurrent || !double.IsFinite(concurrent)) return null;
        var ratio = concurrent / solo;
        return double.IsFinite(ratio) ? ratio : null;
    }

    private static string Number(double? value) => value is { } number && double.IsFinite(number)
        ? number.ToString("0.0", CultureInfo.InvariantCulture) : "-";
    private static string RatioText(double? value) => value is { } number && double.IsFinite(number) ? Number(number) + "x" : "-";
    private static string Clean(string value) => new(value.Select(ch => char.IsControl(ch) ? ' ' : ch).ToArray());
    private static string Pad(string value, int columns, bool right)
    {
        var text = OwnerConsoleLineFitter.Cut(value, columns);
        var spaces = new string(' ', columns - text.GetColumns());
        return right ? spaces + text : text + spaces;
    }
}
