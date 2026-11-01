using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record OrphanWorktreeSweepOutcome(
    string Path, GoalId? GoalId, string Reason, int ProcessId, string? ExecutablePath, string? CallerRole)
{
    internal static void Write(OrphanWorktreeSweepOutcome outcome) =>
        Write(outcome, Console.Error);

    internal static void Write(OrphanWorktreeSweepOutcome outcome, TextWriter writer)
    {
        // Kept worktrees are routine; whole-sweep failures already have a cleanup warning.
        if (outcome.Reason is "registered" or "non-terminal-goal" or "recent"
            or "lock-unavailable" or "listing-failed") return;

        writer.WriteLine("orphan-sweep-outcome " + JsonSerializer.Serialize(outcome));
    }
}
