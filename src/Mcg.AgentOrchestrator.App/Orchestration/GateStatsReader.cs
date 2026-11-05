using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Reads existing evidence only. Each TRX contributes atomically, including failed tests.
internal static class GateStatsReader
{
    internal sealed record LaneObservation(int Version, string LaneName, double DurationSeconds, DateTimeOffset RecordedAt);
    internal sealed record GateAttempt(string Id, DateTimeOffset RecordedAt, IReadOnlyDictionary<string, double> Classes);
    internal sealed record TestResult(string Class, string? Test, string? Outcome, double Seconds);
    internal sealed record LaneRun(string Lane, int Run, IReadOnlyList<TestResult> Results);
    internal sealed record GateRunAttempt(string Id, DateTimeOffset RecordedAt, IReadOnlyList<LaneRun> Runs);
    private sealed record TrxFile(string Path, string Attempt, DateTimeOffset Time, string Lane);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex AttemptFile = new(
        @"^(?<attempt>.+-(?<time>\d{17})-[0-9a-f]{32})\.(?<lane>.+)\.trx$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex RunSuffix = new(@"^(?<lane>.+)-run-(?<run>\d+)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    internal static IReadOnlyList<(string Attempt, DateTimeOffset Time, string Lane, int Retry)> DiscoverWaitRuns(
        string directory, out int skipped)
    {
        var groups = DiscoverAttempts(directory, null, int.MaxValue, out skipped);
        var runs = new List<(string, DateTimeOffset, string, int)>();
        foreach (var group in groups)
        foreach (var file in group)
        {
            var suffix = RunSuffix.Match(file.Lane);
            var retry = 0;
            if (suffix.Success && !int.TryParse(suffix.Groups["run"].Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out retry)) { skipped++; continue; }
            runs.Add((group.Key, file.Time, suffix.Success ? suffix.Groups["lane"].Value : file.Lane, retry));
        }
        return runs;
    }

    internal static IReadOnlyList<LaneObservation> ReadLaneObservations(string directory, out int skipped)
    {
        skipped = 0;
        var observations = new List<LaneObservation>();
        try
        {
            foreach (var line in SharedJsonlFile.ReadLines(Path.Combine(directory, AcceptanceLaneDurationStore.FileName)))
            {
                try
                {
                    var row = JsonSerializer.Deserialize<LaneObservation>(line, JsonOptions);
                    if (row is null || row.Version != 1 || string.IsNullOrWhiteSpace(row.LaneName) ||
                        !double.IsFinite(row.DurationSeconds) || row.DurationSeconds <= 0 || row.RecordedAt == default)
                        skipped++;
                    else observations.Add(row);
                }
                catch (JsonException) { skipped++; }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
        return observations;
    }

    // Reuse the same identity and run-ordinal parsing without discarding unreadable evidence.
    internal static IReadOnlyList<LaneRun> ReadAttemptRuns(string prefix, ICollection<string> problems)
    {
        var runs = new List<LaneRun>();
        try
        {
            foreach (var path in Directory.GetFiles(Path.GetDirectoryName(prefix)!, Path.GetFileName(prefix) + ".*.trx"))
            {
                var match = AttemptFile.Match(Path.GetFileName(path));
                if (!match.Success || match.Groups["attempt"].Value != Path.GetFileName(prefix)) continue;
                var lane = match.Groups["lane"].Value;
                var suffix = RunSuffix.Match(lane);
                var run = 0;
                if (suffix.Success && !int.TryParse(suffix.Groups["run"].Value, NumberStyles.None,
                    CultureInfo.InvariantCulture, out run))
                {
                    problems.Add($"{Path.GetFileName(path)}: invalid run ordinal");
                    continue;
                }
                var results = ReadTrx(path);
                if (results is null) problems.Add($"{Path.GetFileName(path)}: malformed or unreadable TRX");
                else runs.Add(new(suffix.Success ? suffix.Groups["lane"].Value : lane, run, results));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"{Path.GetFileName(prefix)} TRX discovery: {ex.GetType().Name}");
        }
        return runs.OrderBy(run => run.Lane, StringComparer.Ordinal).ThenBy(run => run.Run).ToArray();
    }

    internal static IReadOnlyList<GateAttempt> ReadGateAttempts(string directory, string? lane, int last, out int skipped)
    {
        var groups = DiscoverAttempts(directory, lane, last, out skipped);
        var attempts = new List<GateAttempt>();
        foreach (var group in groups)
        {
            var totals = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var file in group)
            {
                var results = ReadTrx(file.Path);
                if (results is null) { skipped++; continue; }
                foreach (var result in results)
                    totals[result.Class] = totals.GetValueOrDefault(result.Class) + result.Seconds;
            }
            attempts.Add(new(group.Key, group.First().Time, totals));
        }
        return attempts;
    }

    internal static IReadOnlyList<GateRunAttempt> ReadGateRuns(string directory, int last, out int skipped)
    {
        var groups = DiscoverAttempts(directory, null, last, out skipped);
        var attempts = new List<GateRunAttempt>();
        foreach (var group in groups)
        {
            var runs = new List<LaneRun>();
            foreach (var file in group)
            {
                var suffix = RunSuffix.Match(file.Lane);
                var run = 0;
                if (suffix.Success && !int.TryParse(suffix.Groups["run"].Value, NumberStyles.None,
                    CultureInfo.InvariantCulture, out run)) { skipped++; continue; }
                var results = ReadTrx(file.Path);
                if (results is null) { skipped++; continue; }
                runs.Add(new(suffix.Success ? suffix.Groups["lane"].Value : file.Lane, run, results));
            }
            attempts.Add(new(group.Key, group.First().Time, runs));
        }
        return attempts;
    }

    private static IGrouping<string, TrxFile>[] DiscoverAttempts(string directory, string? lane, int last, out int skipped)
    {
        skipped = 0;
        var files = new List<TrxFile>();
        var root = Path.Combine(directory, "acceptance-gate-attempts");
        foreach (var goal in Entries(root, true, ref skipped))
        foreach (var path in Entries(goal, false, ref skipped))
        {
            var match = AttemptFile.Match(Path.GetFileName(path));
            if (!match.Success) { skipped++; continue; }
            if (lane is not null && !lane.Equals(match.Groups["lane"].Value, StringComparison.OrdinalIgnoreCase)) continue;
            if (!DateTimeOffset.TryParseExact(match.Groups["time"].Value, "yyyyMMddHHmmssfff",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time))
            { skipped++; continue; }
            files.Add(new(path, Path.Combine(goal, match.Groups["attempt"].Value), time, match.Groups["lane"].Value));
        }
        return files.GroupBy(file => file.Attempt, StringComparer.Ordinal)
            .OrderByDescending(group => group.First().Time).ThenBy(group => group.Key, StringComparer.Ordinal).Take(last).ToArray();
    }

    private static string[] Entries(string directory, bool directories, ref int skipped)
    {
        try
        {
            return directories ? Directory.GetDirectories(directory) : Directory.GetFiles(directory, "*.trx");
        }
        catch (DirectoryNotFoundException) { return []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; return []; }
    }

    private static IReadOnlyList<TestResult>? ReadTrx(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var document = XDocument.Load(stream);
            if (document.Root?.Name.LocalName != "TestRun" ||
                !document.Root.Elements().Any(element => element.Name.LocalName == "Results")) return null;
            var definitions = document.Descendants().Where(element => element.Name.LocalName == "UnitTest")
                .Where(element => element.Attribute("id") is not null)
                .GroupBy(element => (string)element.Attribute("id")!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var results = new List<TestResult>();
            foreach (var result in document.Descendants().Where(element => element.Name.LocalName == "UnitTestResult"))
            {
                if (!TimeSpan.TryParse((string?)result.Attribute("duration"), CultureInfo.InvariantCulture, out var duration) ||
                    duration < TimeSpan.Zero) return null;
                definitions.TryGetValue((string?)result.Attribute("testId") ?? "", out var definition);
                var identity = AcceptanceTrxTestIdentityResolver.Resolve(result, definition);
                var name = definition?.Descendants().FirstOrDefault(element => element.Name.LocalName == "TestMethod")
                    ?.Attribute("className")?.Value.Trim();
                if (string.IsNullOrWhiteSpace(name))
                {
                    var selector = identity is null ? "" : AcceptanceTrxTestIdentityResolver.NormalizeSelector(identity);
                    var separator = selector.LastIndexOf('.');
                    if (separator <= 0) return null;
                    name = selector[..separator];
                }
                results.Add(new(name, identity, (string?)result.Attribute("outcome"), duration.TotalSeconds));
            }
            return results;
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException) { return null; }
    }
}
