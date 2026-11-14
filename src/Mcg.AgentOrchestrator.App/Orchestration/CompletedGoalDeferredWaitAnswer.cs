using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owns the terminal-goal exception for wait answers, without admitting goal work.
internal static class CompletedGoalDeferredWaitAnswer
{
    internal const string AmbiguousTargetReasonCode = "completed-goal-deferred-wait-ambiguous";
    internal const string AppliedOutcomePrefix = "applied; completed deferred wait ";

    internal abstract record Decision
    {
        internal sealed record NotApplicable : Decision;
        internal sealed record Confirm : Decision;
        internal sealed record Apply(HumanInputRequest Wait, AnswerOperatorIntentPayload Payload) : Decision;
        internal sealed record Ambiguous(IReadOnlyList<string> WaitIds) : Decision;
    }

    internal sealed record ServiceResult(
        IReadOnlySet<string> HandledIntentIds, bool Mutated, IReadOnlyList<string> Lines);

    internal static Decision Decide(AgentOrchestratorKernel kernel, Goal goal, OperatorIntentRecord intent)
    {
        if (goal.Status != GoalStatus.Completed || intent.Verb != OperatorIntentVerbs.Answer)
            return new Decision.NotApplicable();

        AnswerOperatorIntentPayload? payload;
        try { payload = JsonSerializer.Deserialize<AnswerOperatorIntentPayload>(intent.PayloadJson, OperatorIntentJson.Options); }
        catch (JsonException) { return new Decision.NotApplicable(); }
        if (payload is null || payload.GoalId != intent.GoalId || payload.GoalId != goal.Id.Value ||
            payload.ActorKind != intent.ActorKind || payload.RouteRole is not null || payload.AmendmentDisposition is not null)
            return new Decision.NotApplicable();

        if (goal.Timeline.Any(entry => entry.OperatorIntentApplied is { } applied &&
                applied.IntentId == intent.Id && applied.Outcome?.StartsWith(AppliedOutcomePrefix, StringComparison.Ordinal) == true))
            return new Decision.Confirm();

        if (string.IsNullOrWhiteSpace(payload.Text) || payload.TargetKind != OperatorAnswerTargetKind.HumanInput)
            return new Decision.NotApplicable();

        var waits = kernel.HumanInputRequests.Where(request => request.GoalId == goal.Id &&
                !request.IsCompleted && PostLandingOperatorCheck.IsPostLandingCheck(request))
            .OrderBy(request => request.RequestedAt).ThenBy(request => request.Id.Value, StringComparer.Ordinal).ToArray();
        HumanInputRequest? wait;
        if (!string.IsNullOrWhiteSpace(payload.TargetId))
            wait = waits.SingleOrDefault(request => request.Id.Value == payload.TargetId);
        else
        {
            if (waits.Length > 1) return new Decision.Ambiguous(waits.Select(request => request.Id.Value).ToArray());
            wait = waits.SingleOrDefault();
        }
        return wait is not null && (wait.TaskId is null || goal.Tasks.Any(task => task.Id == wait.TaskId))
            ? new Decision.Apply(wait, payload)
            : new Decision.NotApplicable();
    }

    // Only a freshly reloaded marker may finish an intent. A new answer waits for the
    // tick owner's checkpoint and the next reload before the coordinator confirms it.
    internal static ServiceResult Service(
        AgentOrchestratorKernel kernel, Goal goal, OperatorIntentCoordinator intents,
        ConductorParallelAcceptanceAttemptCoordinator attempts, IReadOnlyList<OperatorIntentRecord> batch)
    {
        var handled = new HashSet<string>(StringComparer.Ordinal);
        var lines = new List<string>();
        var mutated = false;
        var confirmations = batch.Where(intent => Decide(kernel, goal, intent) is Decision.Confirm)
            .Select(intent => intent.Id).ToHashSet(StringComparer.Ordinal);
        if (confirmations.Count > 0)
        {
            var application = OperatorIntentGoalApplication.ApplyPending(kernel, goal, intents, attempts, confirmations);
            lines.AddRange(application.Lines);
            handled.UnionWith(confirmations);
        }

        foreach (var intent in batch)
        {
            if (handled.Contains(intent.Id)) continue;
            switch (Decide(kernel, goal, intent))
            {
                case Decision.Apply apply:
                    kernel.SubmitHumanInput(apply.Wait.Id, apply.Payload.Text, origin: HumanInputAnswerOrigin.Operator);
                    kernel.RecordOperatorIntentApplied(goal.Id, intent.Id, intent.Verb, null, intent.Actor, intent.Channel,
                        intent.AuthenticationAssurance,
                        $"{OperatorIntentCoordinator.BuildApplicationMarker(intent)} verb=answer target=HumanInput:{apply.Wait.Id.Value}",
                        intent.ActorKind, outcome: AppliedOutcomePrefix + apply.Wait.Id.Value);
                    mutated = true;
                    handled.Add(intent.Id);
                    lines.Add($"OPERATOR_INTENT id={intent.Id} verb=answer goal={goal.Id.Value[..Math.Min(8, goal.Id.Value.Length)]} result=applied-pending-commit");
                    break;
                case Decision.Ambiguous ambiguous:
                    lines.AddRange(intents.RejectPending(goal.Id.Value,
                        "Answer target is ambiguous; specify one of the open deferred wait ids: " + string.Join(", ", ambiguous.WaitIds),
                        AmbiguousTargetReasonCode, new HashSet<string>(StringComparer.Ordinal) { intent.Id }));
                    handled.Add(intent.Id);
                    break;
            }
        }
        return new ServiceResult(handled, mutated, lines);
    }
}
