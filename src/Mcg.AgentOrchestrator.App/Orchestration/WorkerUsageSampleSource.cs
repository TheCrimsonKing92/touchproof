using System.Globalization;
using Mcg.AgentOrchestrator.App.Usage;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class WorkerUsageSampleSource
{
    private readonly (AgentRole Role, TaskDispatchRecord Dispatch)[] newestFirst;

    internal WorkerUsageSampleSource(IEnumerable<Goal> goals) : this(DispatchUsagePopulation.Build(goals))
    {
    }

    private WorkerUsageSampleSource(DispatchUsagePopulation population) =>
        newestFirst = population.Dispatches.OrderByDescending(row => row.Dispatch.DispatchedAt).ToArray();

    internal static WorkerUsageSampleSource Read(string statePath) =>
        new(DispatchUsagePopulation.Read(SqliteOrchestratorStateRepository.OpenReadOnly(statePath)));

    internal static DateTimeOffset? ReadLandedAt(string homeDirectory, string landedHomeCommit)
    {
        var result = GitCli.RunForRepository(homeDirectory, "show", "-s", "--format=%cI", landedHomeCommit, "--");
        return result.ExitCode == 0 && DateTimeOffset.TryParse(result.Output.Trim(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp) ? timestamp : null;
    }

    internal (OutcomeMetricSample Before, OutcomeMetricSample After) Measure(
        OutcomeMetricLine line, DateTimeOffset landedAt, DateTimeOffset evaluatedAt)
    {
        var before = new List<double>();
        var after = new List<double>();
        foreach (var (role, dispatch) in newestFirst)
        {
            if (dispatch.DispatchedAt > evaluatedAt || (line.Role is { } requiredRole && requiredRole != role)) continue;
            var window = dispatch.DispatchedAt < landedAt ? before : after;
            if (window.Count == line.Window || ReadValue(line.Name, dispatch) is not { } value) continue;
            window.Add(value);
            if (before.Count == line.Window && after.Count == line.Window) break;
        }

        return (Summarize(before), Summarize(after));

        OutcomeMetricSample Summarize(List<double> values) =>
            new(values.Count == line.Window ? CostGoalOutcomeMetric.Aggregate(line.Name, values) : null, values.Count);
    }

    private static long? ReadValue(string metric, TaskDispatchRecord dispatch) => metric switch
    {
        CostGoalOutcomeMetric.WorkerTurns => DispatchUsageValues.RecordedTurns(dispatch),
        CostGoalOutcomeMetric.WorkerInput =>
            DispatchUsageValues.ReportedCount(DispatchUsageValues.RecordedUsage(dispatch)?.InputTokens),
        _ => throw new ArgumentOutOfRangeException(nameof(metric), metric, "Unknown worker usage metric.")
    };
}
