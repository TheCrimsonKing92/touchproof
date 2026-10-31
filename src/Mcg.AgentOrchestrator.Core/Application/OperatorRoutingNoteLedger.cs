using System.Text;

namespace Mcg.AgentOrchestrator.Core;

// Task-owned routing state is snapshotted with the retry/dispatch transition, never in a side store.
public static class OperatorRoutingNoteLedger
{
    public static void OpenHold(TaskSpec task, DateTimeOffset failureRecordedAt, int holdSeconds,
        string? attemptId, IReadOnlyList<string> automaticFeedback)
    {
        if (holdSeconds < 0) throw new ArgumentOutOfRangeException(nameof(holdSeconds));
        var round = task.LatestRetryAt ?? throw new InvalidOperationException("An acceptance retry must have a round identity.");
        if (task.AcceptanceRetryRoutingHold is { RoundRetryAt: var existing } && existing == round) return;
        task.AcceptanceRetryRoutingHold = new(round, failureRecordedAt,
            failureRecordedAt.AddSeconds(holdSeconds), holdSeconds, attemptId, automaticFeedback.ToArray());
    }

    public static bool IsCurrentRoundAutomaticAcceptanceRetry(TaskSpec task) =>
        task.AcceptanceRetryRoutingHold is { } hold && hold.RoundRetryAt == task.LatestRetryAt;

    public static bool IsHeldRoundPending(TaskSpec task) =>
        task.Status == WorkTaskStatus.Assigned && IsCurrentRoundAutomaticAcceptanceRetry(task);

    public static void PrepareDispatch(TaskSpec task, DateTimeOffset now)
    {
        if (IsHeldRoundPending(task) && task.AcceptanceRetryRoutingHold is { } hold && now < hold.Deadline)
            throw new InvalidOperationException($"waiting-for-operator-routing deadline={hold.Deadline:O} " +
                $"remaining_seconds={Math.Max(0, (long)Math.Ceiling((hold.Deadline - now).TotalSeconds))}");
        DeliverPending(task);
    }

    public static void AcceptNote(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task,
        string intentId, string text, IReadOnlyList<string> evidence, RetryCause cause, DateTimeOffset now,
        DateTimeOffset? submittedAt = null, bool replacesAutomaticFeedback = false)
    {
        if (task.OperatorRoutingNotes.Any(note => note.IntentId == intentId)) return;
        var queued = task.Status == WorkTaskStatus.Running;
        task.OperatorRoutingNotes = [.. task.OperatorRoutingNotes,
            new(intentId, text.Trim(), evidence.ToArray(), cause, now, queued, SubmittedAt: submittedAt ?? now)];
        if (replacesAutomaticFeedback) task.OperatorRoutingAutomaticFeedback = [];
        kernel.RecordTaskNote(goal.Id, task.Id,
            $"OPERATOR_ROUTING_NOTE intent={intentId} accepted_at={now:O} evidence={string.Join(",", evidence)} " +
            (queued ? OperatorRoutingNote.QueuedOutcome : "Operator note retained for the pending acceptance retry."));
    }

    public static IReadOnlyList<OperatorRoutingNote> PendingNotes(TaskSpec task) =>
        task.OperatorRoutingNotes.Where(note => note.DeliveredAt is null).ToArray();

    public static string ComposeBlock(IReadOnlyList<OperatorRoutingNote> notes,
        IReadOnlyList<string> automaticFeedback, string? attemptId)
    {
        var block = new StringBuilder("Later operator notes override earlier ones where they conflict.");
        foreach (var note in notes)
        {
            block.AppendLine().AppendLine($"Authoritative operator note submitted_at={note.SubmittedAt ?? note.AcceptedAt:O} " +
                $"intent={note.IntentId} cause={note.Cause} evidence={string.Join(",", note.EvidenceReferences)}:");
            block.AppendLine(note.Text);
        }
        block.AppendLine().AppendLine("Automatic failure facts (not instructions):");
        foreach (var fact in automaticFeedback) block.AppendLine(fact);
        if (attemptId is not null) block.AppendLine($"Acceptance attempt id: {attemptId}");
        return block.ToString().Trim();
    }

    public static void DeliverPending(TaskSpec task)
    {
        var notes = PendingNotes(task);
        if (notes.Count == 0 || task.Status != WorkTaskStatus.Assigned) return;
        var round = task.LatestRetryAt ?? throw new InvalidOperationException("Routing notes require an independently authorized retry round.");
        var hold = IsCurrentRoundAutomaticAcceptanceRetry(task) ? task.AcceptanceRetryRoutingHold : null;
        // Repeated preparations use original facts, not an already composed instruction block.
        var facts = hold?.AutomaticFeedback ?? task.OperatorRoutingAutomaticFeedback ?? task.CriterionRetryFeedback;
        task.OperatorRoutingAutomaticFeedback = facts.ToArray();
        var block = ComposeBlock(notes, facts, hold?.AttemptId);
        task.RecordCriterionRetryFeedback([block], task.CriterionRetryFeedbackRoundAt);
        task.RecordAcceptedRetryFeedback(block, notes[^1].AcceptedAt);
        task.OperatorRoutingNotes = task.OperatorRoutingNotes.Select(note => note.DeliveredAt is null
            ? note with { TargetRoundRetryAt = round } : note).ToArray();
    }

    internal static void CommitDelivery(TaskSpec task, DateTimeOffset dispatchedAt)
    {
        task.OperatorRoutingNotes = task.OperatorRoutingNotes.Select(note =>
            note.DeliveredAt is null && note.TargetRoundRetryAt is { } round && round == task.LatestRetryAt
                ? note with { DeliveredAt = dispatchedAt } : note).ToArray();
    }

    public static IReadOnlyList<OperatorRoutingNote> MergeNotes(
        IReadOnlyList<OperatorRoutingNote>? stored, IReadOnlyList<OperatorRoutingNote>? current)
    {
        // A stale writer cannot remove an accepted note or undo a committed delivery.
        var notes = (stored ?? []).ToDictionary(note => note.IntentId, StringComparer.Ordinal);
        foreach (var incoming in current ?? [])
        {
            if (!notes.TryGetValue(incoming.IntentId, out var previous) ||
                previous.DeliveredAt is null && incoming.DeliveredAt is not null)
                notes[incoming.IntentId] = incoming;
        }
        return notes.Values.OrderBy(note => note.AcceptedAt).ToArray();
    }
}
