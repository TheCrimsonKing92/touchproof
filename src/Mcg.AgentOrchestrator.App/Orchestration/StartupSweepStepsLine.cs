using System.Globalization;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class StartupSweepStepsLine
{
    public const string Prefix = "STARTUP_SWEEP_STEPS total_ms=";

    public static string Format(TerminalGoalSweepResult result, long totalMs)
    {
        var retired = result.Goals.Sum(goal => goal.Repairs.Count(repair =>
            repair.Kind == LandedGoalBranchRetirement.RepairKind));
        return string.Create(CultureInfo.InvariantCulture,
            $"{Prefix}{totalMs} owned_root_ms={result.OwnedRootDurationMs} git_index_ms={result.GitIndexDurationMs} evidence_ms={result.EvidenceDurationMs} ephemeral_ms={result.EphemeralDurationMs} attention_ms={result.AttentionDurationMs} merge_evidence_ms={result.MergeEvidenceDurationMs} goals_ms={result.GoalsDurationMs} git_spawns={result.GitSpawnCount} goals_swept={result.GoalsSweptCount} terminal_goals_swept={result.TerminalGoalsSweptCount} landed_branches_retired={retired} slowest={FormatSlowest(result.GoalDurations)}");
    }

    internal static string FormatSlowest(IReadOnlyList<(GoalId GoalId, long ElapsedMs, int GitSpawns)> durations)
    {
        var slowest = durations
            .Select(item => (Prefix: item.GoalId.Value[..Math.Min(8, item.GoalId.Value.Length)], item.ElapsedMs, item.GitSpawns))
            .OrderByDescending(item => item.ElapsedMs)
            .ThenBy(item => item.Prefix, StringComparer.Ordinal)
            .Take(5)
            .Select(item => string.Create(CultureInfo.InvariantCulture, $"{item.Prefix}:{item.ElapsedMs}:{item.GitSpawns}"));
        var slowestText = string.Join(",", slowest);
        return slowestText.Length == 0 ? "none" : slowestText;
    }
}
