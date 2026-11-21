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
        Run(args, () => ReadGoals(workspace), output, error,
            (since, role) => DispatchUsagePopulation.Read(OpenState(workspace), since, role),
            since => ReadReworkGoals(workspace, since));

    internal static int Run(IReadOnlyList<string> args,
        Func<IReadOnlyCollection<Goal>> loadGoals, TextWriter output, TextWriter error,
        Func<DateTimeOffset?, (IReadOnlyCollection<Goal> Goals, ContextReworkCoverage Coverage)>? readReworkGoals) =>
        Run(args, loadGoals, output, error, loadReworkGoals: readReworkGoals);

    internal static int Run(IReadOnlyList<string> args,
        Func<IReadOnlyCollection<Goal>> loadGoals, TextWriter output, TextWriter error,
        Func<DateTimeOffset?, string?, DispatchUsagePopulation>? loadUsage = null,
        Func<DateTimeOffset?, (IReadOnlyCollection<Goal> Goals, ContextReworkCoverage Coverage)>? loadReworkGoals = null)
    {
        try
        {
            var (since, until, role, json, sections, rework, top) = Parse(args);
            if (rework)
            {
                var (goals, coverage) = loadReworkGoals is null ? WithLiveCoverage(loadGoals()) : loadReworkGoals(since);
                WriteRework(ContextReworkUsageReport.Build(goals, since, until, role, coverage, top), json, output);
                return 0;
            }
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
                            string.Join(",", row.MissingUsageReasons.Select(reason => $"{reason.Key}={reason.Value}"))) +
                        FormatWtCalls(row.WtCalls));
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

    private static IReadOnlyCollection<Goal> ReadGoals(OrchestratorWorkspace workspace) =>
        OpenState(workspace).LoadAsync().GetAwaiter().GetResult().Goals;

    private static SqliteOrchestratorStateRepository OpenState(OrchestratorWorkspace workspace)
    {
        if (!File.Exists(workspace.SqliteStatePath))
            throw new FileNotFoundException("State database is missing.", workspace.SqliteStatePath);
        return SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath);
    }

    private static (IReadOnlyCollection<Goal> Goals, ContextReworkCoverage Coverage) ReadReworkGoals(
        OrchestratorWorkspace workspace, DateTimeOffset? since)
    {
        var repository = OpenState(workspace);
        var goals = repository.LoadAsync().GetAwaiter().GetResult().Goals;
        var live = goals.Where(goal => !goal.IsMetadataOnly).ToArray();
        var archiveIds = goals.Where(goal => goal.IsMetadataOnly &&
                (since is null || goal.MetadataTerminatedAt is null || goal.MetadataTerminatedAt >= since))
            .Select(goal => goal.Id).ToArray();
        var hydrated = archiveIds.Chunk(500)
            .SelectMany(batch => repository.LoadGoalsAsync(batch).GetAwaiter().GetResult().Goals)
            .Where(goal => !goal.IsMetadataOnly).ToArray();
        return ([.. live, .. hydrated], new(live.Length, hydrated.Length, archiveIds.Length - hydrated.Length));
    }

    private static (IReadOnlyCollection<Goal> Goals, ContextReworkCoverage Coverage) WithLiveCoverage(
        IReadOnlyCollection<Goal> goals)
    {
        var live = goals.Count(goal => !goal.IsMetadataOnly);
        return (goals, new(live, 0, goals.Count - live));
    }

    private static void WriteRework(ContextReworkUsageResult report, bool json, TextWriter output)
    {
        if (json)
        {
            output.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));
            return;
        }

        var since = report.Since?.ToString("O", CultureInfo.InvariantCulture) ?? "unbounded";
        var until = report.Until?.ToString("O", CultureInfo.InvariantCulture) ?? "unbounded";
        var overall = report.Overall;
        var coverage = report.Coverage;
        output.WriteLine($"Context rework usage [{since}, {until}): rows={report.Rows.Count} top={report.Top}");
        output.WriteLine($"Overall: attempts={overall.Attempts} input={Count(overall.Input)} cached={Count(overall.Cached)} " +
            $"output={Count(overall.Output)} missing-usage={overall.MissingUsage}");
        output.WriteLine($"Coverage: live={coverage.Live} archived-hydrated={coverage.ArchivedHydrated} " +
            $"archived-unavailable={coverage.ArchivedUnavailable} (unavailable archives excluded)");
        foreach (var note in report.Notes)
            output.WriteLine(note);
        foreach (var row in report.Rows.Take(report.Top))
        {
            var median = row.MedianInput?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
            output.WriteLine($"{row.Role} | {row.RetryCause} | {row.FindingClass} | attempts={row.Attempts} " +
                $"input={Count(row.Input)} cached={Count(row.Cached)} output={Count(row.Output)} missing-usage={row.MissingUsage} | " +
                $"goals={row.GoalsAffected} median-input={median} median-goals={row.MedianGoalCount} | " +
                $"examples={string.Join(", ", row.Examples.Select(pair => $"{pair.GoalId}/{pair.TaskId}"))}");
        }
    }

    private static string Count(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "unknown";

    private static string Format(ContextUsageStat stat) =>
        $"median={stat.Median?.ToString(CultureInfo.InvariantCulture) ?? "unknown"},p90={stat.P90?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}";

    private static string FormatWtCalls(ContextUsageWtCalls? calls)
    {
        if (calls is null)
            return string.Empty;
        var counts = calls.ReportedDispatchCount == 0
            ? "unknown"
            : string.Join(",", calls.BySubcommand.Select(pair => $"{pair.Key}={pair.Value}"));
        return $" | wt-calls={Format(calls.Total)} reported={calls.ReportedDispatchCount} subcommands={counts}";
    }

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

    private static (DateTimeOffset? Since, DateTimeOffset? Until, string? Role, bool Json, bool Sections, bool Rework, int Top)
        Parse(IReadOnlyList<string> args)
    {
        DateTimeOffset? since = null;
        DateTimeOffset? until = null;
        string? role = null;
        var json = false;
        var sections = false;
        var rework = false;
        int? top = null;
        for (var i = 1; i < args.Count; i++)
        {
            var flag = args[i];
            if (flag.Equals("--sections", StringComparison.OrdinalIgnoreCase))
            {
                sections = true;
                continue;
            }
            if (flag.Equals("--rework", StringComparison.OrdinalIgnoreCase))
            {
                rework = true;
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
            var isSince = flag.Equals("--since", StringComparison.OrdinalIgnoreCase);
            if ((isSince || flag.Equals("--until", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < args.Count &&
                Regex.IsMatch(args[i + 1], @"^\d{4}-\d{2}-\d{2}T.+(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant) &&
                DateTimeOffset.TryParse(args[i + 1], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                if (isSince)
                    since = date;
                else
                    until = date;
                i++;
                continue;
            }
            if (flag.Equals("--top", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count &&
                int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var limit) && limit > 0)
            {
                top = limit;
                i++;
                continue;
            }
            throw new ArgumentException(CliCommandHelp.ContextUsageUsage);
        }
        if (rework ? sections : until is not null || top is not null)
            throw new ArgumentException(CliCommandHelp.ContextUsageUsage);
        if (since.HasValue && until.HasValue && since >= until)
            throw new ArgumentException("--since must precede --until.");
        return (since, until, role, json, sections, rework, top ?? 10);
    }
}
