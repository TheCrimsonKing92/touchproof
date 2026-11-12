using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Describes an already-persisted transition; it owns no lifecycle decisions.
internal static class AcceptanceAttemptTerminalEvent
{
    internal const string Kind = "acceptance-attempt";

    internal static string Format(ConductorParallelAcceptanceAttempt attempt,
        Func<string, (string Settled, string Phase)>? readHolder = null)
    {
        try
        {
            (string Settled, string Phase) holder;
            try
            {
                holder = (readHolder ?? ConductorFinishedAttemptDiagnosis.ReadHolder)(attempt.MetadataPath);
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
            {
                holder = ("unreadable", "unreadable");
            }

            return $"ACCEPTANCE_ATTEMPT_TERMINAL goal={Quote(attempt.GoalId)} " +
                $"attempt-id={Quote(attempt.AttemptId)} slot={Quote($"slot-{attempt.SlotIndex}")} " +
                $"outcome={Quote(attempt.Outcome.ToString())} detail={Quote(attempt.Detail)} " +
                $"holder-settled={Quote(holder.Settled)} holder-phase={Quote(holder.Phase)}";
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            return "ACCEPTANCE_ATTEMPT_TERMINAL goal=\"unreadable\" attempt-id=\"unreadable\" " +
                "slot=\"unreadable\" outcome=\"unreadable\" detail=\"unreadable\" " +
                "holder-settled=\"unreadable\" holder-phase=\"unreadable\"";
        }
    }

    private static string Quote(string? value) => JsonSerializer.Serialize(value ?? "");
}
