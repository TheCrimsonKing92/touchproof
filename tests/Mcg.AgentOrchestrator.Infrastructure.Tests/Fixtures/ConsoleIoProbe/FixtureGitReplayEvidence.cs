using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using static FixtureGitReplay;

[SupportedOSPlatform("windows")]
internal static class FixtureGitReplayEvidence
{
    internal sealed record Selection(string Family, string Filter, int ExecutedCases, int SkippedCases,
        int NativeGit, int SharedProbe, int GitChild, int Unattributed, double UnattributedPercent, int SlottedOther,
        [property: JsonPropertyName("cpu_100ns")] long Cpu100ns, double CpuSeconds);

    private sealed record Case(string Identity, string DisplayName, string[] Parameters, int Repetitions, string Outcome);
    private sealed record Slot(string TestName, int ProcessId, int GitLaunches, Dictionary<string, int> GitSubcommands,
        Dictionary<string, int> GitCallers, int GitFixtureProbeLaunches);
    private sealed record Probe(string TestName, int ProcessId, int ChildProcessId, long ChildStartFiletime,
        string Caller, string Subcommand, string[] Arguments);
    private sealed record MatchedProbe(string TestName, string Caller, string? ParentCaller, string Subcommand, string[] Arguments);
    private sealed record NativeGit(FixtureGitReplayTrace.Start Process, string Origin, MatchedProbe? Probe);

    internal static Selection Reconcile(string directory, string family, string testClass, string filter, string[] methods,
        List<FixtureGitReplayTrace.Start> tree, FixtureGitReplayTrace.Job job)
    {
        var cases = ReadCases(Path.Combine(directory, "tests"), testClass, methods);
        var slots = ReadSlots(Path.Combine(directory, "slot.jsonl"), cases, tree);
        var git = tree.Where(node => Regex.IsMatch(Path.GetFileName(node.Image), @"^git(?:-.+)?\.exe$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).ToDictionary(node => (node.Pid, node.StartFiletime));
        var probes = ReadProbes(Path.Combine(directory, "probes.jsonl"), slots, git);
        var gitSequences = git.Values.Select(node => node.Sequence).ToHashSet(StringComparer.Ordinal);
        var inGitTree = new HashSet<string>(StringComparer.Ordinal);
        var native = new List<NativeGit>();
        foreach (var node in tree)
        {
            var belowGit = inGitTree.Contains(node.ParentSequence);
            if (gitSequences.Contains(node.Sequence))
            {
                var probe = probes.GetValueOrDefault(node.Sequence);
                native.Add(new NativeGit(node, probe is not null ? "shared-probe" : belowGit ? "git-child" : "unattributed", probe));
                inGitTree.Add(node.Sequence);
            }
            else if (belowGit) inGitTree.Add(node.Sequence);
        }

        var callers = probes.Values.GroupBy(probe => (probe.TestName, probe.Caller, probe.ParentCaller, probe.Subcommand))
            .Select(group => new
            {
                family, group.Key.TestName, group.Key.Caller, group.Key.ParentCaller, group.Key.Subcommand, count = group.Count()
            }).ToArray();
        WriteJson(Path.Combine(directory, "process-tree.json"), tree);
        WriteJson(Path.Combine(directory, "native-git.json"), native);
        WriteJson(Path.Combine(directory, "shared-probe-callers.json"), callers);
        WriteJson(Path.Combine(directory, "cases.json"), cases);
        var unattributed = native.Count(row => row.Origin == "unattributed");
        return new Selection(family, filter, cases.Length, 0, git.Count, probes.Count,
            native.Count(row => row.Origin == "git-child"), unattributed, git.Count == 0 ? 0 : 100.0 * unattributed / git.Count,
            slots.Values.Sum(slot => slot.GitCallers.GetValueOrDefault("other")), job.Cpu100ns, TimeSpan.FromTicks(job.Cpu100ns).TotalSeconds);
    }

    private static Case[] ReadCases(string results, string testClass, string[] methods)
    {
        var reports = Directory.GetFiles(results, "*.trx", SearchOption.AllDirectories);
        if (reports.Length == 0) throw new InvalidDataException($"The replay produced no TRX in {results}.");
        var remaining = methods.Select(method => $"{testClass}.{method}").ToHashSet(StringComparer.Ordinal);
        var cases = new List<Case>();
        foreach (var report in reports)
        {
            var root = XElement.Load(report);
            var ns = root.Name.Namespace;
            var definitions = root.Element(ns + "TestDefinitions")!.Elements(ns + "UnitTest")
                .ToDictionary(definition => (string)definition.Attribute("id")!, definition => definition.Element(ns + "TestMethod")!);
            var resultsInReport = root.Element(ns + "Results")!.Elements(ns + "UnitTestResult").ToArray();
            var counters = root.Element(ns + "ResultSummary")?.Element(ns + "Counters");
            if (counters is not null && ((int?)counters.Attribute("total") != resultsInReport.Length ||
                (int?)counters.Attribute("executed") != resultsInReport.Length || (int?)counters.Attribute("notExecuted") > 0))
                throw new InvalidDataException($"TRX result and execution counts disagree in {report}.");
            foreach (var result in resultsInReport)
            {
                var method = definitions[(string)result.Attribute("testId")!];
                var identity = $"{(string?)method.Attribute("className")}.{(string?)method.Attribute("name")}";
                var outcome = (string?)result.Attribute("outcome");
                if (outcome != "Passed" || !remaining.Remove(identity))
                    throw new InvalidDataException($"Unexpected case, repetition or outcome in {report} ({identity}, {outcome}).");
                cases.Add(new Case(identity, (string)result.Attribute("testName")!, [], 1, outcome));
            }
        }
        if (remaining.Count != 0)
            throw new InvalidDataException($"Missing executed TRX cases: {string.Join(", ", remaining.Order(StringComparer.Ordinal))}.");
        return cases.OrderBy(row => row.Identity, StringComparer.Ordinal).ToArray();
    }

    private static Dictionary<string, Slot> ReadSlots(string path, Case[] cases, List<FixtureGitReplayTrace.Start> tree)
    {
        var slots = new Dictionary<string, Slot>(StringComparer.Ordinal);
        var caseNames = cases.Select(row => row.Identity).ToHashSet(StringComparer.Ordinal);
        var pids = tree.Select(node => node.Pid).ToHashSet();
        foreach (var line in File.ReadLines(path))
        {
            var slot = JsonSerializer.Deserialize<Slot>(line, JsonOptions)!;
            if (!caseNames.Contains(slot.TestName) || !pids.Contains(slot.ProcessId) || !slots.TryAdd(slot.TestName, slot))
                throw new InvalidDataException($"Unexpected slot observation for {slot.TestName} in process {slot.ProcessId}.");
            if (slot.GitSubcommands.Values.Sum() != slot.GitLaunches || slot.GitCallers.Values.Sum() != slot.GitLaunches ||
                slot.GitFixtureProbeLaunches < 0 || slot.GitFixtureProbeLaunches > slot.GitLaunches)
                throw new InvalidDataException($"Git census totals disagree for {slot.TestName}.");
        }
        if (slots.Count != cases.Length) throw new InvalidDataException("One or more replay cases have no Git census window.");
        return slots;
    }

    private static Dictionary<string, MatchedProbe> ReadProbes(string path, Dictionary<string, Slot> slots,
        Dictionary<(int Pid, long StartFiletime), FixtureGitReplayTrace.Start> git)
    {
        var probes = new Dictionary<string, MatchedProbe>(StringComparer.Ordinal);
        var commands = new Dictionary<(string TestName, string Subcommand), int>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in File.Exists(path) ? File.ReadLines(path) : [])
        {
            var probe = JsonSerializer.Deserialize<Probe>(line, JsonOptions)!;
            var identity = (probe.ChildProcessId, probe.ChildStartFiletime);
            if (!git.TryGetValue(identity, out var node) || !slots.TryGetValue(probe.TestName, out var slot) ||
                probe.ProcessId != node.ParentPid || probe.ProcessId != slot.ProcessId)
                throw new InvalidDataException($"Fixture probe has no matching native start and test window ({identity}, {probe.TestName}).");
            var caller = probe.Caller.Split("<-", 2);
            if (!probes.TryAdd(node.Sequence, new MatchedProbe(probe.TestName, caller[0], caller.ElementAtOrDefault(1),
                probe.Subcommand, probe.Arguments)))
                throw new InvalidDataException($"Multiple fixture probes matched native start {identity}.");

            var command = (probe.TestName, probe.Subcommand);
            var commandCount = commands.GetValueOrDefault(command) + 1;
            commands[command] = commandCount;
            if (commandCount > slot.GitSubcommands.GetValueOrDefault(probe.Subcommand))
                throw new InvalidDataException($"Fixture probe launches exceed the slot census for {command}.");
            counts[probe.TestName] = counts.GetValueOrDefault(probe.TestName) + 1;
        }
        foreach (var slot in slots.Values)
            if (counts.GetValueOrDefault(slot.TestName) != slot.GitFixtureProbeLaunches)
                throw new InvalidDataException($"Fixture probe starts do not reconcile with the slot census for {slot.TestName}.");
        return probes;
    }
}
