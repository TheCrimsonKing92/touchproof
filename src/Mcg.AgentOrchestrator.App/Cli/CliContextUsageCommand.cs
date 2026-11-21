using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Usage;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliContextUsageCommand
{
    internal static bool IsCommand(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0].Equals("context-usage", StringComparison.OrdinalIgnoreCase);

    internal static int Run(IReadOnlyList<string> args, OrchestratorWorkspace workspace) =>
        Run(args, workspace, Console.Out, Console.Error);

    internal static int Run(IReadOnlyList<string> args, OrchestratorWorkspace workspace, TextWriter output, TextWriter error) =>
        Run(args, () => ReadRepository(workspace).LoadAsync().GetAwaiter().GetResult().Goals, output, error,
            (since, role) => DispatchUsagePopulation.Read(ReadRepository(workspace), since, role));

    internal static int Run(IReadOnlyList<string> args,
        Func<IReadOnlyCollection<Goal>> loadGoals, TextWriter output, TextWriter error,
        Func<DateTimeOffset?, string?, DispatchUsagePopulation>? loadUsage = null)
    {
        try
        {
            var (since, role, json, sections) = Parse(args);
            if (sections)
            {
                WriteSections(ContextSectionOutcomeReport.Build(loadGoals(), since, role), json, output);
                return 0;
            }
            var report = loadUsage is null
                ? ContextUsageReport.Build(loadGoals(), since, role)
                : ContextUsageReport.Build(loadUsage(since, role), since, role);
            if (json)
            {
                output.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                }));
            }
            else
            {
                output.WriteLine($"Context usage: dispatches={report.Rows.Sum(row => row.DispatchCount)} rows={report.Rows.Count}");
                foreach (var row in report.Rows)
                {
                    output.WriteLine($"{row.Role} | {row.Model} | dispatches={row.DispatchCount} reported-input={row.ReportedInputCount} | " +
                        $"input={Format(row.Input)} cached={Format(row.Cached)} uncached={Format(row.Uncached)} " +
                        $"output={Format(row.Output)} turns={Format(row.Turns)} tool-calls={Format(row.ToolCalls)} " +
                        $"harness-overhead={Format(row.HarnessOverhead)} | " +
                        $"missing-usage={row.MissingUsageCount} reasons=" +
                        (row.MissingUsageReasons.Count == 0 ? "none" :
                            string.Join(",", row.MissingUsageReasons.Select(reason => $"{reason.Key}={reason.Value}"))));
                }
                var coverage = report.Coverage;
                output.WriteLine($"Coverage: live={coverage.Live} archived={coverage.Archived} " +
                    $"unknown-usage={coverage.UnknownUsage} conflicting={coverage.Conflicting}");
                foreach (var identity in coverage.ConflictIds)
                    output.WriteLine($"Conflicting dispatch: goal={identity.GoalId} task={identity.TaskId} sequence={identity.Sequence}");
            }
            return 0;
        }
        catch (Exception ex)
        {
            error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static SqliteOrchestratorStateRepository ReadRepository(OrchestratorWorkspace workspace)
    {
        if (!File.Exists(workspace.SqliteStatePath))
            throw new FileNotFoundException("State database is missing.", workspace.SqliteStatePath);
        return SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath);
    }

    private static string Format(ContextUsageStat stat) =>
        $"median={stat.Median?.ToString(CultureInfo.InvariantCulture) ?? "unknown"},p90={stat.P90?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}";

    private static void WriteSections(ContextSectionOutcomeResult report, bool json, TextWriter output)
    {
        if (json)
        {
            output.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));
            return;
        }
        output.WriteLine($"Context sections: dispatches={report.DispatchCount} rows={report.Rows.Count} unresolved={report.UnresolvedDispatchCount}");
        foreach (var row in report.Rows)
        {
            output.WriteLine($"{row.Role} | {row.Section} | dispatches={row.DispatchCount} landed={row.LandedCount} failed={row.FailedCount} retried={row.RetriedCount} | " +
                $"landed-with={Share(row.LandedShareWithSection)} ({row.LandedCount}/{row.DispatchCount}) " +
                $"landed-without={Share(row.LandedShareWithoutSection)} ({row.WithoutSectionLandedCount}/{row.WithoutSectionDispatchCount}) | " +
                $"reported-input={row.ReportedInputCount} input={Format(row.InputUsage)} bytes={Format(row.SectionBytes)} | " +
                $"missing-usage={row.MissingUsageCount} reasons=" +
                (row.MissingUsageReasons.Count == 0 ? "none" :
                    string.Join(",", row.MissingUsageReasons.Select(reason => $"{reason.Key}={reason.Value}"))) +
                $" unresolved={row.UnresolvedCount}");
        }
    }

    private static string Share(double? share) => share?.ToString("P1", CultureInfo.InvariantCulture) ?? "n/a";

    private static (DateTimeOffset? Since, string? Role, bool Json, bool Sections) Parse(IReadOnlyList<string> args)
    {
        DateTimeOffset? since = null;
        string? role = null;
        var json = false;
        var sections = false;
        for (var i = 1; i < args.Count; i++)
        {
            var flag = args[i];
            if (flag.Equals("--sections", StringComparison.OrdinalIgnoreCase))
            {
                sections = true;
                continue;
            }
            if (flag.Equals("--json", StringComparison.OrdinalIgnoreCase))
            {
                json = true;
                continue;
            }
            if (flag.Equals("--role", StringComparison.OrdinalIgnoreCase) &&
                i + 1 < args.Count && !string.IsNullOrWhiteSpace(args[i + 1]) &&
                !args[i + 1].StartsWith("-", StringComparison.Ordinal))
            {
                role = args[++i];
                continue;
            }
            if (flag.Equals("--since", StringComparison.OrdinalIgnoreCase) &&
                i + 1 < args.Count &&
                Regex.IsMatch(args[i + 1], @"^\d{4}-\d{2}-\d{2}T.+(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant) &&
                DateTimeOffset.TryParse(args[i + 1], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                since = date;
                i++;
                continue;
            }
            throw new ArgumentException(CliCommandHelp.ContextUsageUsage);
        }
        return (since, role, json, sections);
    }
}
