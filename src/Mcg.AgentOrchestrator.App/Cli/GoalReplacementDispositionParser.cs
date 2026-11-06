using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class GoalReplacementDispositionParser
{
    internal static GoalReplacementDisposition Parse(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "zero-work-correction" => GoalReplacementDisposition.ZeroWorkCorrection,
            "abandon-failed-attempt" => GoalReplacementDisposition.AbandonFailedAttempt,
            "supersede-unlanded-attempt" => GoalReplacementDisposition.SupersedeUnlandedAttempt,
            _ => throw new ArgumentException(
                "--disposition requires one of: zero-work-correction, abandon-failed-attempt, supersede-unlanded-attempt.")
        };
}
