using System.Globalization;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliGateStatsMemoryCommand
{
    private const string MemoryKind = "LAUNCH_LOCK_MEMORY_BY_CLASS";
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
        var files = FindSummaries(orchestratorDirectory, ref skipped);
        var attempts = files.GroupBy(file => file.Attempt, StringComparer.Ordinal)
            .OrderByDescending(group => group.First().Time).ThenBy(group => group.Key, StringComparer.Ordinal)
            .Take(flags.GetValueOrDefault("--last", 10)).ToArray();
        var totals = new Dictionary<string, List<(decimal Heap, decimal Private)>>(StringComparer.Ordinal);
        foreach (var attempt in attempts)
        {
            var paths = attempt.Select(file => file.Path).Order(StringComparer.Ordinal);
            var observations = ReadObservations(paths, ref skipped);
            foreach (var group in observations.GroupBy(pair => pair.Key.Class, StringComparer.Ordinal))
            {
                if (!totals.TryGetValue(group.Key, out var growth))
                    totals.Add(group.Key, growth = []);
                growth.Add((group.Sum(pair => (decimal)pair.Value.Heap), group.Sum(pair => (decimal)pair.Value.Private)));
            }
        }

        var ranking = totals.Select(pair => (Class: pair.Key, Gates: pair.Value.Count,
                Heap: Median(pair.Value.Select(growth => growth.Heap)),
                Private: Median(pair.Value.Select(growth => growth.Private))))
            .OrderByDescending(row => row.Heap).ThenBy(row => row.Class, StringComparer.Ordinal)
            .Take(flags.GetValueOrDefault("--top", 20));
        foreach (var row in ranking)
            output.WriteLine(FormattableString.Invariant(
                $"class={row.Class}\tgates={row.Gates}\tmedian_heap_growth_bytes={row.Heap:0.#}\tmedian_private_growth_bytes={row.Private:0.#}"));
        output.WriteLine(FormattableString.Invariant($"attempts={attempts.Length}\tskipped={skipped}"));
    }

    private static List<(string Path, string Attempt, DateTimeOffset Time)> FindSummaries(
        string orchestratorDirectory, ref int skipped)
    {
        var files = new List<(string Path, string Attempt, DateTimeOffset Time)>();
        foreach (var goal in Entries(Path.Combine(orchestratorDirectory, "acceptance-gate-attempts"), true, ref skipped))
        foreach (var path in Entries(goal, false, ref skipped))
        {
            var match = SummaryFile.Match(Path.GetFileName(path));
            if (!match.Success || !DateTimeOffset.TryParseExact(match.Groups["time"].Value, "yyyyMMddHHmmssfff",
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time))
            {
                skipped++;
                continue;
            }
            files.Add((path, Path.Combine(goal, match.Groups["attempt"].Value), time));
        }
        return files;
    }

    private static string[] Entries(string directory, bool directories, ref int skipped)
    {
        try { return directories ? Directory.GetDirectories(directory) : Directory.GetFiles(directory, "*.launch-lock-summary.txt"); }
        catch (DirectoryNotFoundException) { return []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; return []; }
    }

    private static Dictionary<(int Pid, string Class), (long Heap, long Private)> ReadObservations(
        IEnumerable<string> paths, ref int skipped)
    {
        var observations = new Dictionary<(int Pid, string Class), (long Heap, long Private)>();
        foreach (var path in paths)
        {
            string[] lines;
            try { lines = File.ReadAllLines(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; continue; }

            var hasMemoryLine = false;
            foreach (var line in lines)
            {
                if (line != MemoryKind && !line.StartsWith(MemoryKind + " ", StringComparison.Ordinal)) continue;
                hasMemoryLine = true;
                if (TryParse(line, out var key, out var growth))
                    observations[key] = growth;
                else
                    skipped++;
            }
            if (!hasMemoryLine) skipped++;
        }
        return observations;
    }

    private static bool TryParse(string line, out (int Pid, string Class) key, out (long Heap, long Private) growth)
    {
        key = default;
        growth = default;
        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length != 7 || tokens[0] != MemoryKind ||
            !tokens[1].StartsWith("pid=", StringComparison.Ordinal) ||
            !int.TryParse(tokens[1].AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0 ||
            !tokens[2].StartsWith("class=", StringComparison.Ordinal) || tokens[2].Length <= 6 ||
            tokens[2].Any(char.IsWhiteSpace) ||
            !ReadBytes(tokens[3], "heap_start_bytes=", out var heapStart) ||
            !ReadBytes(tokens[4], "heap_end_bytes=", out var heapEnd) ||
            !ReadBytes(tokens[5], "private_start_bytes=", out var privateStart) ||
            !ReadBytes(tokens[6], "private_end_bytes=", out var privateEnd)) return false;

        key = (pid, tokens[2][6..]);
        growth = (heapEnd - heapStart, privateEnd - privateStart);
        return true;
    }

    private static bool ReadBytes(string token, string prefix, out long value)
    {
        value = 0;
        return token.StartsWith(prefix, StringComparison.Ordinal) &&
            long.TryParse(token.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 0;
    }

    private static decimal Median(IEnumerable<decimal> values)
    {
        var sorted = values.Order().ToArray();
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2 : sorted[middle];
    }
}
