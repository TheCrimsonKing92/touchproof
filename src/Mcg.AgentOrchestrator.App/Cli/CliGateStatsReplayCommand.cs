using System.Globalization;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliGateStatsReplayCommand
{
    // Each --gate names a JSON manifest: gateId, receiptsPath, lanes[{name,trxPaths}],
    // optional classes[{fullName,collection}], optional slotCount. Paths are manifest-relative.
    internal static void Execute(IReadOnlyList<string> args, TextWriter output)
    {
        if (args.Count < 4 || (args.Count - 2) % 2 != 0)
            throw new ArgumentException(CliCommandHelp.GateStatsUsage);
        var results = new List<AcceptanceBalancedLaneReplay.Result>();
        for (var index = 2; index < args.Count; index += 2)
        {
            if (args[index] != "--gate" || string.IsNullOrWhiteSpace(args[index + 1]) || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException(CliCommandHelp.GateStatsUsage);
            results.Add(new AcceptanceBalancedLaneReplay().Read(args[index + 1]));
        }
        foreach (var result in results)
        {
            foreach (var lane in result.Lanes)
                output.WriteLine(FormattableString.Invariant($"gate={Cell(result.GateId)}\tlane={Cell(lane.Lane)}\tactual_wall_seconds={lane.ActualWallSeconds:0.###}\tpredicted_wall_seconds={lane.PredictedWallSeconds:0.###}"));
            output.WriteLine($"gate={Cell(result.GateId)}\tnew_max_predicted_wall_seconds={result.NewMaxPredictedWallSeconds.ToString("0.###", CultureInfo.InvariantCulture)}");
        }
    }

    private static string Cell(string value) => value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
}
