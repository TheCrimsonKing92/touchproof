using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class OperatorRouteNoteAdmission
{
    internal static bool Admits(TaskSpec task, OperatorIntentRecord intent) =>
        intent.AuthenticationAssurance != OperatorIntentAdjudication.StewardAssurance &&
        (OperatorRoutingNoteLedger.IsHeldRoundPending(task) ||
         task.Status == WorkTaskStatus.Running && OperatorRoutingNoteLedger.IsCurrentRoundAutomaticAcceptanceRetry(task));

    internal static bool TryApply(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task,
        OperatorIntentRecord intent, AdjudicateOperatorIntentPayload payload, RetryCause cause, DateTimeOffset now)
    {
        if (!Admits(task, intent)) return false;
        OperatorRoutingNoteLedger.AcceptNote(kernel, goal, task, intent.Id, payload.Text,
            payload.EvidenceReferences!, cause, now, intent.CreatedAt);
        return true;
    }

    internal static string Outcome(Goal goal, string? taskId, string intentId)
    {
        var note = goal.Tasks.FirstOrDefault(task => task.Id.Value == taskId)?.OperatorRoutingNotes
            .FirstOrDefault(note => note.IntentId == intentId);
        return note?.QueuedWhileRunning == true ? " " + OperatorRoutingNote.QueuedOutcome : string.Empty;
    }

    internal static void RetainFollowingRoute(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task,
        OperatorIntentRecord intent, AdjudicateOperatorIntentPayload payload, RetryCause cause, DateTimeOffset now)
    {
        // A separately authorized route can open the next round after the automatic round ends.
        // Keep its newer instruction after any queued ones, instead of demoting it to failure facts.
        if (intent.AuthenticationAssurance == OperatorIntentAdjudication.StewardAssurance ||
            OperatorRoutingNoteLedger.PendingNotes(task).Count == 0) return;
        OperatorRoutingNoteLedger.AcceptNote(kernel, goal, task, intent.Id, payload.Text,
            payload.EvidenceReferences, cause, now, intent.CreatedAt, replacesAutomaticFeedback: true);
    }
}
