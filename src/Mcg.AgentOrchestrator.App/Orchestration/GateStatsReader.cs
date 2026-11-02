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
    private sealed record TrxFile(string Path, string Attempt, DateTimeOffset Time);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex AttemptFile = new(
        @"^(?<attempt>.+-(?<time>\d{17})-[0-9a-f]{32})\.(?<lane>.+)\.trx$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

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

    internal static IReadOnlyList<GateAttempt> ReadGateAttempts(string directory, string? lane, int last, out int skipped)
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
            files.Add(new(path, Path.Combine(goal, match.Groups["attempt"].Value), time));
        }
        var attempts = new List<GateAttempt>();
        foreach (var group in files.GroupBy(file => file.Attempt, StringComparer.Ordinal)
            .OrderByDescending(group => group.First().Time).ThenBy(group => group.Key, StringComparer.Ordinal).Take(last))
        {
            var totals = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var file in group)
            {
                var classes = ReadTrx(file.Path);
                if (classes is null) { skipped++; continue; }
                foreach (var (name, seconds) in classes)
                    totals[name] = totals.GetValueOrDefault(name) + seconds;
            }
            attempts.Add(new(group.Key, group.First().Time, totals));
        }
        return attempts;
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

    private static Dictionary<string, double>? ReadTrx(string path)
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
            var totals = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var result in document.Descendants().Where(element => element.Name.LocalName == "UnitTestResult"))
            {
                if (!TimeSpan.TryParse((string?)result.Attribute("duration"), CultureInfo.InvariantCulture, out var duration) ||
                    duration < TimeSpan.Zero) return null;
                definitions.TryGetValue((string?)result.Attribute("testId") ?? "", out var definition);
                var name = definition?.Descendants().FirstOrDefault(element => element.Name.LocalName == "TestMethod")
                    ?.Attribute("className")?.Value.Trim();
                if (string.IsNullOrWhiteSpace(name))
                {
                    var identity = AcceptanceTrxTestIdentityResolver.Resolve(result, definition);
                    var selector = identity is null ? "" : AcceptanceTrxTestIdentityResolver.NormalizeSelector(identity);
                    var separator = selector.LastIndexOf('.');
                    if (separator <= 0) return null;
                    name = selector[..separator];
                }
                totals[name] = totals.GetValueOrDefault(name) + duration.TotalSeconds;
            }
            return totals;
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException) { return null; }
    }
}
