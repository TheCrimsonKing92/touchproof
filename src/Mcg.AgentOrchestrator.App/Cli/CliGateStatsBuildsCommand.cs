using System.Globalization;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliGateStatsBuildsCommand
{
    internal static void Execute(IReadOnlyList<string> args, string orchestratorDirectory, TextWriter output,
        TimeProvider? clock = null)
    {
        DateTimeOffset? since = null;
        if (args.Count != 2)
        {
            if (args.Count != 4 || args[2] != "--last-hours" ||
                !int.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out var hours) || hours <= 0)
                throw new ArgumentException(CliCommandHelp.GateStatsUsage);
            var now = (clock ?? TimeProvider.System).GetUtcNow();
            since = hours > (now - DateTimeOffset.MinValue).TotalHours ? DateTimeOffset.MinValue : now.AddHours(-hours);
        }
        var report = new BuildReceiptReport(SharedJsonlFile.ReadLines(Path.Combine(orchestratorDirectory, "build-receipts.jsonl")), since);
        foreach (var row in report.Origins)
            output.WriteLine(FormattableString.Invariant($"origin={row.Origin}\tbuilds={row.Builds}\tcpu_ms={row.CpuMs}\twall_ms={row.WallMs}\tcache_hits={row.CacheHits}\tcpu_unavailable={row.CpuUnavailable}"));
        output.WriteLine(FormattableString.Invariant($"repeated_trees={report.Repeats.Count}\trepeat_builds={report.Repeats.Sum(row => row.Compiles - 1)}\trepeat_cpu_ms={report.Repeats.Sum(row => row.RepeatCpuMs)}"));
        foreach (var row in report.Repeats)
            output.WriteLine(FormattableString.Invariant($"tree={row.Tree}\tcompiles={row.Compiles}\torigins={string.Join(',', row.Origins)}\trepeat_cpu_ms={row.RepeatCpuMs}"));
        output.WriteLine(FormattableString.Invariant($"lines={report.Lines}\tskipped={report.Skipped}"));
    }
}
