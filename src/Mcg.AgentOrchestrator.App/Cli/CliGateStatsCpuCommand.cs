using System.Globalization;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliGateStatsCpuCommand
{
    private static readonly Regex SummaryFile = new(
        @"^(?<attempt>.+-(?<time>\d{17})-[0-9a-f]{32})\.(?<lane>.+)\.launch-lock-summary\.txt$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    internal static void Execute(IReadOnlyList<string> args, string orchestratorDirectory, TextWriter output)
    {
        var flags = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 2; i < args.Count; i++)
        {
            var flag = args[i];
            if (flag is not ("--last" or "--top") || ++i >= args.Count ||
                !int.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
                value <= 0 || !flags.TryAdd(flag, value))
                throw new ArgumentException(CliCommandHelp.GateStatsUsage);
        }

        var skipped = 0;
        var files = new List<(string Path, string Attempt, DateTimeOffset Time)>();
        foreach (var goal in Entries(Path.Combine(orchestratorDirectory, "acceptance-gate-attempts"), true, ref skipped))
        foreach (var path in Entries(goal, false, ref skipped))
        {
            var match = SummaryFile.Match(Path.GetFileName(path));
            if (!match.Success || !DateTimeOffset.TryParseExact(match.Groups["time"].Value, "yyyyMMddHHmmssfff",
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time))
            { skipped++; continue; }
            files.Add((path, Path.Combine(goal, match.Groups["attempt"].Value), time));
        }

        var attempts = files.GroupBy(file => file.Attempt, StringComparer.Ordinal)
            .OrderByDescending(group => group.First().Time).ThenBy(group => group.Key, StringComparer.Ordinal)
            .Take(flags.GetValueOrDefault("--last", 10)).ToArray();
        var totals = new Dictionary<string, ClassTotal>(StringComparer.Ordinal);
        foreach (var attempt in attempts)
        foreach (var file in attempt)
        {
            string[] lines;
            try { lines = File.ReadAllLines(file.Path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; continue; }
            var hasCpuLine = false;
            foreach (var line in lines)
            {
                if (!line.StartsWith("LAUNCH_LOCK_CPU_BY_CLASS", StringComparison.Ordinal)) continue;
                hasCpuLine = true;
                if (!TryParse(line, out var name, out var child, out var inProcess)) { skipped++; continue; }
                if (!totals.TryGetValue(name, out var total)) totals.Add(name, total = new());
                total.Attempts.Add(attempt.Key);
                total.Child += child;
                total.InProcess += inProcess;
            }
            if (!hasCpuLine) skipped++;
        }

        foreach (var pair in totals.OrderByDescending(pair => pair.Value.Child)
                     .ThenBy(pair => pair.Key, StringComparer.Ordinal).Take(flags.GetValueOrDefault("--top", 20)))
            output.WriteLine(FormattableString.Invariant(
                $"class={pair.Key}\tgates={pair.Value.Attempts.Count}\tchild_cpu_ms={pair.Value.Child:F3}\tinproc_active_share_ms={pair.Value.InProcess:F3}"));
        output.WriteLine(FormattableString.Invariant($"attempts={attempts.Length}\tskipped={skipped}"));
    }

    private static string[] Entries(string directory, bool directories, ref int skipped)
    {
        try { return directories ? Directory.GetDirectories(directory) : Directory.GetFiles(directory, "*.launch-lock-summary.txt"); }
        catch (DirectoryNotFoundException) { return []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; return []; }
    }

    private static bool TryParse(string line, out string name, out double child, out double inProcess)
    {
        name = "";
        child = inProcess = 0;
        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length != 5 || tokens[0] != "LAUNCH_LOCK_CPU_BY_CLASS" ||
            !tokens[1].StartsWith("pid=", StringComparison.Ordinal) ||
            !int.TryParse(tokens[1].AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0 ||
            !tokens[2].StartsWith("class=", StringComparison.Ordinal) || tokens[2].Length <= 6 ||
            tokens[2].Any(char.IsWhiteSpace) ||
            !ReadCpu(tokens[3], "child_cpu_ms=", out child) ||
            !ReadCpu(tokens[4], "inproc_cpu_ms_apportioned=", out inProcess)) return false;
        name = tokens[2][6..];
        return true;
    }

    private static bool ReadCpu(string token, string prefix, out double value)
    {
        value = 0;
        return token.StartsWith(prefix, StringComparison.Ordinal) &&
            double.TryParse(token.AsSpan(prefix.Length), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value) &&
            double.IsFinite(value) && value >= 0;
    }

    private sealed class ClassTotal
    {
        internal HashSet<string> Attempts { get; } = new(StringComparer.Ordinal);
        internal double Child;
        internal double InProcess;
    }
}
