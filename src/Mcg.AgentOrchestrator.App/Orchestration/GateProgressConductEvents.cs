using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owns gate progress wire formatting and required event writes, independently of dispatch policy.
internal static class GateProgressConductEvents
{
    internal static void Append(
        ConductEventLogWriter writer,
        GoalId goalId,
        AcceptanceGateProgress progress)
    {
        if (!writer.AppendRequired(
                "gate-progress",
                goalId.Value[..8],
                Format(progress)))
        {
            throw new IOException(
                $"Required gate progress event could not be appended for goal {goalId.Value[..8]}.");
        }
    }

    internal static void AppendCohort(
        ConductEventLogWriter writer,
        AcceptanceCohortIdentity identity,
        IReadOnlyList<AcceptanceCohortMemberBinding> bindings,
        AcceptanceGateProgress progress,
        string? outerAttemptId = null)
    {
        var members = string.Join(',', bindings.Select(member => member.GoalId.Value[..8]));
        foreach (var member in bindings)
        {
            var goalId = member.GoalId.Value[..8];
            var detail =
                $"PHASE_PROGRESS goal={goalId} cohort={identity.Value[..Math.Min(18, identity.Value.Length)]} " +
                $"member={goalId} members={members} phase={progress.Phase} " +
                $"elapsed_ms={(long)progress.Elapsed.TotalMilliseconds} target={FormatConductToken(progress.CurrentTarget)} " +
                $"child_pid={progress.ChildProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} " +
                $"output_bytes={progress.OutputBytes} heartbeat={FormatConductToken(progress.HeartbeatPath)}" + progress.ShardOutcomeTokens +
                (progress.ExecutionId is null ? "" : $" execution={FormatConductToken(progress.ExecutionId)}") +
                (outerAttemptId is null ? "" : $" outer_attempt={FormatConductToken(outerAttemptId)}");
            if (!writer.AppendRequired("gate-progress", goalId, detail))
            {
                throw new IOException(
                    $"Required cohort gate progress event could not be appended for goal {goalId}.");
            }
        }
    }

    internal static string Format(AcceptanceGateProgress progress) =>
        $"PHASE_PROGRESS goal={progress.GoalId?[..Math.Min(8, progress.GoalId.Length)] ?? "unknown"} phase={progress.Phase} " +
        $"elapsed_ms={(long)progress.Elapsed.TotalMilliseconds} target={FormatConductToken(progress.CurrentTarget)} " +
        $"child_pid={progress.ChildProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} " +
        $"output_bytes={progress.OutputBytes} heartbeat={FormatConductToken(progress.HeartbeatPath)}" + progress.ShardOutcomeTokens +
        (progress.ExecutionId is null ? "" : $" execution={FormatConductToken(progress.ExecutionId)}");

    private static string FormatConductToken(string value) =>
        value.IndexOfAny([' ', '\t', '\r', '\n', '"']) < 0
            ? value
            : $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
}
