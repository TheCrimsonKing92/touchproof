using System.Globalization;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.App.Cli;

// Reads the bounded class attribution emitted at host teardown; retries count as real launches.
internal static class CliGateStatsLaunchesCommand
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
            // Read atomically: an unreadable file contributes no partial totals.
            string[] lines;
            try { lines = File.ReadAllLines(file.Path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; continue; }
            foreach (var line in lines)
            {
                if (!line.StartsWith("LAUNCH_LOCK_BY_CLASS", StringComparison.Ordinal)) continue;
                if (!TryParse(line, out var name, out var launches, out var git, out var createMs))
                { skipped++; continue; }
                if (!totals.TryGetValue(name, out var total)) totals.Add(name, total = new());
                total.Attempts.Add(attempt.Key);
                total.Launches += launches;
                total.GitCli += git;
                total.CreateMs += createMs;
            }
        }

        foreach (var pair in totals.OrderByDescending(pair => pair.Value.Launches)
                     .ThenBy(pair => pair.Key, StringComparer.Ordinal).Take(flags.GetValueOrDefault("--top", 20)))
            output.WriteLine(FormattableString.Invariant(
                $"class={pair.Key}\tgates={pair.Value.Attempts.Count}\tlaunches={pair.Value.Launches}\tgit_cli_launches={pair.Value.GitCli}\tcreate_ms_total={pair.Value.CreateMs:F3}"));
        output.WriteLine(FormattableString.Invariant($"attempts={attempts.Length}\tskipped={skipped}"));
    }

    private static string[] Entries(string directory, bool directories, ref int skipped)
    {
        try { return directories ? Directory.GetDirectories(directory) : Directory.GetFiles(directory, "*.launch-lock-summary.txt"); }
        catch (DirectoryNotFoundException) { return []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; return []; }
    }

    private static bool TryParse(string line, out string name, out long launches, out long git, out decimal createMs)
    {
        name = "";
        launches = git = 0;
        createMs = 0;
        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length != 6 || tokens[0] != "LAUNCH_LOCK_BY_CLASS") return false;
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var token in tokens.Skip(1))
        {
            var separator = token.IndexOf('=');
            if (separator <= 0 || separator == token.Length - 1 ||
                !fields.TryAdd(token[..separator], token[(separator + 1)..])) return false;
        }
        if (!fields.TryGetValue("pid", out var pid) ||
            !int.TryParse(pid, NumberStyles.None, CultureInfo.InvariantCulture, out _) ||
            !fields.TryGetValue("class", out var className) || className.Any(char.IsWhiteSpace) ||
            !fields.TryGetValue("launches", out var count) || !Count(count, out launches) ||
            !fields.TryGetValue("create_ms_total", out var milliseconds) ||
            !decimal.TryParse(milliseconds, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out createMs) ||
            !fields.TryGetValue("by_entry", out var byEntry)) return false;
        var entries = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var item in byEntry.Split(','))
        {
            var separator = item.IndexOf(':');
            if (separator <= 0 || !Count(item[(separator + 1)..], out var entryCount) ||
                !entries.TryAdd(item[..separator], entryCount)) return false;
        }
        if (!entries.TryGetValue("GitCli", out git)) return false;
        name = className;
        return true;
    }

    private static bool Count(string text, out long value) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private sealed class ClassTotal
    {
        internal HashSet<string> Attempts { get; } = new(StringComparer.Ordinal);
        internal long Launches;
        internal long GitCli;
        internal decimal CreateMs;
    }
}
