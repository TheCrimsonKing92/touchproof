using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliGateStatsGitCommand
{
    private const int RecentTicks = 100;
    private sealed record Lane(string Attempt, string Name, int Ordinal, DateTimeOffset At,
        double WallMs, long Launches, double GitMs, IReadOnlyDictionary<string, long> Sites);

    internal static void Execute(IReadOnlyList<string> args, string receiptPath, string logDirectory, TextWriter output)
    {
        var flags = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 2; i < args.Count; i++)
        {
            var key = args[i];
            if (key is not ("--last" or "--top") || ++i >= args.Count ||
                !int.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0 ||
                !flags.TryAdd(key, number)) throw new ArgumentException(CliCommandHelp.GateStatsUsage);
        }
        var last = flags.GetValueOrDefault("--last", 10);
        var top = flags.GetValueOrDefault("--top", 5);
        var lanes = new Dictionary<(string, string, int), Lane>();
        var skipped = 0;
        foreach (var line in SharedJsonlFile.ReadLines(receiptPath))
        {
            if (!TryReadLane(line, out var lane)) { skipped++; continue; }
            var key = (lane!.Attempt, lane.Name, lane.Ordinal);
            if (!lanes.TryGetValue(key, out var previous) || lane.At >= previous.At) lanes[key] = lane;
        }
        var gates = lanes.Values.GroupBy(lane => lane.Attempt)
            .OrderByDescending(group => group.Max(lane => lane.At)).ThenBy(group => group.Key, StringComparer.Ordinal)
            .Take(last).ToArray();
        var log = OwnerDigestMeasurementLogs.Read(logDirectory, "conduct-events*.log");
        var ticks = new List<GitLaunchWindowCensus.Tick>();
        foreach (var record in log.Records)
        {
            if (OwnerDigestMeasurementLogs.String(record.Data, "eventKind") != "tick-git-census") continue;
            var detail = OwnerDigestMeasurementLogs.String(record.Data, "detail");
            if (detail is null || !GitLaunchWindowCensus.TryParseTick(detail, out var tick)) { skipped++; continue; }
            ticks.Add(tick!);
        }
        var recent = ticks.TakeLast(RecentTicks).ToArray();
        output.WriteLine("Summed git wall time over parallel launches can exceed elapsed time; share_pct is uncapped and share_capped_pct is capped at 100.");
        output.WriteLine("Gate wall_ms sums measured lane elapsed time; gate coverage is slot-held test-host launches (including fixtures). Ticks count in-flight launches but time only completions within the tick.");
        foreach (var gate in gates)
            Print("gate", gate.Key, gate.Sum(lane => lane.Launches), gate.Sum(lane => lane.GitMs),
                gate.Sum(lane => lane.WallMs), MergeSites(gate.Select(lane => lane.Sites)), top, output);
        foreach (var tick in recent)
            Print("tick", tick.Number.ToString(CultureInfo.InvariantCulture), tick.Launches, tick.WallMs,
                tick.ElapsedMs, tick.CallSites, top, output, tick.Timed, tick.Subcommands);
        var gateWall = gates.Sum(gate => gate.Sum(lane => lane.WallMs));
        var gateGit = gates.Sum(gate => gate.Sum(lane => lane.GitMs));
        var tickWall = recent.Sum(tick => tick.ElapsedMs);
        var tickGit = recent.Sum(tick => tick.WallMs);
        var gateShare = Share(gateGit, gateWall);
        var tickShare = Share(tickGit, tickWall);
        output.WriteLine(FormattableString.Invariant(
            $"gates={gates.Length} requested={last} ticks={recent.Length} tick_limit={RecentTicks} skipped={skipped + log.Skipped} unavailable={log.Unavailable} gate_share_pct={gateShare:0.###} tick_share_pct={tickShare:0.###}"));
        var verdict = gates.Length < last || gateWall <= 0 ? "insufficient-data"
            : gateShare > 5 || tickShare > 20 ? "step-2"
            : recent.Length == 0 || tickWall <= 0 ? "insufficient-data" : "close";
        output.WriteLine($"verdict={verdict} gates={gates.Length} requested={last} gate_threshold_pct=5 tick_threshold_pct=20");
    }

    private static double Share(double git, double wall) => wall > 0 ? git / wall * 100 : 0;

    private static void Print(string kind, string id, long launches, double git, double wall,
        IReadOnlyDictionary<string, long> sites, int top, TextWriter output, long? timed = null,
        IReadOnlyDictionary<string, long>? subcommands = null)
    {
        var share = Share(git, wall);
        var timing = timed is null ? "" : FormattableString.Invariant($" git_timed={timed} subcommands={GitLaunchWindowCensus.FormatCounts(subcommands!, top)}");
        output.WriteLine(FormattableString.Invariant(
            $"{kind}={GitLaunchWindowCensus.MethodName(id)} launches={launches} git_wall_ms={git:0.###} wall_ms={wall:0.###} share_pct={share:0.###} share_capped_pct={Math.Min(100, share):0.###} call_sites={GitLaunchWindowCensus.FormatCounts(sites, top)}{timing}"));
    }

    private static Dictionary<string, long> MergeSites(IEnumerable<IReadOnlyDictionary<string, long>> sources)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var pair in sources.SelectMany(source => source))
            result[pair.Key] = result.GetValueOrDefault(pair.Key) + pair.Value;
        return result;
    }

    private static bool TryReadLane(string line, out Lane? lane)
    {
        lane = null;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var attempt = OwnerDigestMeasurementLogs.String(root, "attempt_id");
            var name = OwnerDigestMeasurementLogs.String(root, "lane");
            if (string.IsNullOrWhiteSpace(attempt) || string.IsNullOrWhiteSpace(name) ||
                !root.TryGetProperty("run_ordinal", out var ordinal) || ordinal.ValueKind != JsonValueKind.Number ||
                !ordinal.TryGetInt32(out var run) || run < 0 ||
                !root.TryGetProperty("recorded_at", out var at) || at.ValueKind != JsonValueKind.String ||
                !at.TryGetDateTimeOffset(out var timestamp) ||
                !Number(root, "wall_ms", out var wall) || wall <= 0 ||
                !Number(root, "slot_git_wall_ms", out var git) ||
                !root.TryGetProperty("slot_git_launches", out var launches) || launches.ValueKind != JsonValueKind.Number ||
                !launches.TryGetInt64(out var count) || count < 0) return false;
            var sites = new Dictionary<string, long>(StringComparer.Ordinal);
            if (root.TryGetProperty("slot_git_call_sites", out var callers))
            {
                if (callers.ValueKind != JsonValueKind.Object) return false;
                foreach (var caller in callers.EnumerateObject())
                {
                    if (caller.Value.ValueKind != JsonValueKind.Number || !caller.Value.TryGetInt64(out var value) || value < 0)
                        return false;
                    var site = GitLaunchWindowCensus.MethodName(caller.Name);
                    sites[site] = sites.GetValueOrDefault(site) + value;
                }
            }
            lane = new(attempt, name, run, timestamp, wall, count, git, sites);
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool Number(JsonElement row, string name, out double value)
    {
        value = 0;
        return row.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Number &&
            field.TryGetDouble(out value) && double.IsFinite(value) && value >= 0;
    }
}
