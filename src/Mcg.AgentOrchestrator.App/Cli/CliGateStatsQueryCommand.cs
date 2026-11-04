using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliGateStatsQueryCommand
{
    internal static bool IsGateStatsQueryCommand(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0].Equals("gate-stats", StringComparison.OrdinalIgnoreCase) &&
        !CliCommandHelp.IsCommandSpecificHelp(args);

    internal static void Execute(IReadOnlyList<string> args, OrchestratorWorkspace workspace)
    {
        if (args.Count < 2) throw Usage();
        var form = args[1].ToLowerInvariant();
        if (form is not ("lanes" or "classes" or "compare" or "flakes" or "waits")) throw Usage();
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        var json = false;
        for (var i = 2; i < args.Count; i++)
        {
            var flag = args[i];
            if (flag == "--json") { if (json) throw Usage(); json = true; continue; }
            var allowed = flag == "--last" || (form == "classes" && flag is "--lane" or "--top") ||
                (form is "flakes" or "waits" && flag == "--top") ||
                (form == "compare" && flag is "--before" or "--after");
            if (!allowed || ++i >= args.Count || args[i].StartsWith("--", StringComparison.Ordinal) ||
                !flags.TryAdd(flag, args[i]) || string.IsNullOrWhiteSpace(args[i])) throw Usage();
        }
        var last = Positive(flags, "--last", form == "flakes" ? 20 : 10);
        object report;
        if (form == "lanes") report = GateStatsReport.Lanes(workspace.OrchestratorDirectory, last);
        else if (form == "waits") report = GateStatsReport.Waits(workspace.OrchestratorDirectory, last,
            Positive(flags, "--top", 20));
        else if (form == "classes") report = GateStatsReport.Classes(workspace.OrchestratorDirectory, last,
            flags.GetValueOrDefault("--lane"), Positive(flags, "--top", 20));
        else if (form == "flakes") report = GateStatsReport.Flakes(workspace.OrchestratorDirectory, last,
            Positive(flags, "--top", 20));
        else
        {
            var before = IsoTime(flags.GetValueOrDefault("--before"));
            var after = IsoTime(flags.GetValueOrDefault("--after"));
            if (before > after) throw Usage();
            report = GateStatsReport.Compare(workspace.OrchestratorDirectory, last, before, after);
        }
        // The same serialized values back both presentations, preserving numeric precision.
        var serialized = JsonSerializer.Serialize(report, report.GetType(), new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { PropertyNamingPolicy = form == "flakes" ? JsonNamingPolicy.CamelCase : JsonNamingPolicy.SnakeCaseLower });
        if (json) { Console.WriteLine(serialized); return; }
        using var document = JsonDocument.Parse(serialized);
        if (form == "flakes")
        {
            Console.WriteLine(string.Join('\t', document.RootElement.EnumerateObject()
                .Where(property => property.Name != "tests").Select(property => $"{property.Name}={Cell(property.Value.ToString())}")));
            foreach (var row in document.RootElement.GetProperty("tests").EnumerateArray())
                Console.WriteLine(string.Join('\t', row.EnumerateObject().Select(property => $"{property.Name}={Cell(property.Value.ToString())}")));
            return;
        }
        foreach (var row in document.RootElement.GetProperty("rows").EnumerateArray())
            Console.WriteLine(string.Join('\t', row.EnumerateObject().Select(property =>
                $"{property.Name}={Cell(property.Value.ValueKind == JsonValueKind.Null ? "-" : property.Value.ToString())}")));
        if (form == "waits")
            Console.WriteLine(string.Join('\t', document.RootElement.EnumerateObject()
                .Where(property => property.Name is not ("rows" or "skipped"))
                .Select(property => $"{property.Name}={Cell(property.Value.ValueKind == JsonValueKind.Null ? "-" : property.Value.ToString())}")));
        Console.WriteLine($"skipped={document.RootElement.GetProperty("skipped").GetInt32()}");
    }

    private static int Positive(Dictionary<string, string> flags, string name, int fallback)
    {
        if (!flags.TryGetValue(name, out var value)) return fallback;
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0
            ? number : throw Usage();
    }

    private static DateTimeOffset IsoTime(string? value) => value is not null &&
        Regex.IsMatch(value, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant) &&
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
        ? time : throw Usage();

    private static string Cell(string value) => value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
    private static ArgumentException Usage() => new(CliCommandHelp.GateStatsUsage);
}
