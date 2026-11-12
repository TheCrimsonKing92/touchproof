using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

/// <summary>Persists positive start-failure evidence bound to one dispatch, independently of cleanup.</summary>
internal sealed record DispatchStartFailureEvidence(DateTimeOffset DispatchedAt, string FailureText)
{
    private const string NotePrefix = "DISPATCH_START_FAILURE ";

    internal static void Record(AgentOrchestratorKernel kernel, GoalId goalId, TaskId taskId,
        DateTimeOffset dispatchedAt, string failureText)
    {
        var task = kernel.GetTask(goalId, taskId);
        if (!NeverStartedDispatchReconciliation.IsUnclaimed(task) || task.LastDispatch!.DispatchedAt != dispatchedAt)
            throw new InvalidOperationException("Start-failure evidence requires the current unclaimed dispatch.");
        kernel.RecordTaskNote(goalId, taskId,
            NotePrefix + JsonSerializer.Serialize(new DispatchStartFailureEvidence(dispatchedAt, failureText)));
    }

    internal static bool IsRecorded(Goal goal, TaskSpec task) => task.LastDispatch is { } dispatch &&
        goal.Timeline.Any(entry => entry.TaskId == task.Id && entry.Kind == ProgressKind.TaskNote &&
            Read(entry.Message) is { } failure && failure.DispatchedAt == dispatch.DispatchedAt &&
            failure.FailureText is not null);

    private static DispatchStartFailureEvidence? Read(string message)
    {
        if (!message.StartsWith(NotePrefix, StringComparison.Ordinal)) return null;
        try { return JsonSerializer.Deserialize<DispatchStartFailureEvidence>(message[NotePrefix.Length..]); }
        catch (JsonException) { return null; }
    }
}
