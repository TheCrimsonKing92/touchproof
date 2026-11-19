using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class AttentionShowAgentReadView
{
    internal static string Render(string? goalId, IReadOnlyList<HumanInputRequest> waits,
        IReadOnlyList<CollaborationItem> items, Func<CollaborationItem, string?> clarificationId,
        TimeProvider? time = null, IReadOnlyCollection<Goal>? goals = null, OwnerDecisionRead? decisions = null,
        IReadOnlyList<PlannerBriefAmendmentRevisionPendingHold>? amendmentHolds = null)
    {
        var renderTime = PostLandingFollowUpAttention.CaptureRenderTime(time);
        var selectedGoals = (goals ?? []).Where(goal => goalId is null || goal.Id.Value == goalId).ToArray();
        var payload = PostLandingFollowUpAttention.WithRows(EnvironmentHoldAttentionView.WithRows(AttentionAgentReadView.WithPostLandingDuties(WithDecisions(PlannerBriefAmendmentHoldAttentionView.WithRows(new Dictionary<string, object?>
            {
                ["goalId"] = goalId,
                ["waits"] = waits.Select(wait => new
                {
                    id = wait.Id.Value,
                    goalId = wait.GoalId.Value,
                    taskId = wait.TaskId?.Value,
                    kind = wait.Kind.ToString(),
                    question = wait.Question,
                    plannerIdentity = wait.PlannerIdentity,
                    plannerEvidence = wait.PlannerEvidence,
                    owner = wait.EvidenceOwner ?? "operator",
                    availability = wait.PlannerEvidence?.Availability,
                    nextAction = wait.ResumeCommand,
                    requestedAt = wait.RequestedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                    isAutoDefaultable = wait.IsAutoDefaultable,
                    isDismissible = wait.IsDismissible,
                    isAnswerRequired = wait.IsAnswerRequired,
                    isExternallyBlocked = wait.IsExternallyBlocked,
                    resumeCommand = wait.ResumeCommand
                }).ToArray(),
                ["gateEvidence"] = selectedGoals
                    .SelectMany(goal => goal.GateTestObligations.Where(item => item.BriefVersion == goal.AuthoritativeBrief.Version)
                        .Select(item => new { goalId = goal.Id.Value, identity = item.PlannerIdentity,
                            owner = "acceptance-gate", availability = "post-implementation", item.Selectors,
                            payload = item.PlannerEvidence, nextAction = "Acceptance executes exact-candidate selectors" })).ToArray(),
                ["items"] = items.Select(item => new
                {
                    id = item.Id,
                    clarificationId = clarificationId(item),
                    type = item.Type.ToString(),
                    goalId = item.GoalId,
                    status = item.Status.ToString(),
                    subject = item.Subject,
                    body = item.Body,
                    correlationKey = item.CorrelationKey,
                    raisedAt = item.RaisedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)
                }).ToArray(),
                ["ownerQuestionHolds"] = selectedGoals
                    .Where(goal => goal.Status is not (GoalStatus.Completed or GoalStatus.Cancelled or GoalStatus.Superseded))
                    .Select(OwnerQuestionHoldBlocker.TryRead).OfType<OwnerQuestionHoldBlocker>()
                    .Select(hold => new
                    {
                        goalId = hold.GoalId.Value,
                        state = hold.State,
                        identity = hold.Identity,
                        question = hold.Question,
                        taskId = hold.TaskId,
                        taskNumber = hold.TaskNumber,
                        startedAt = hold.StartedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                        resolvingVerb = hold.ResolvingVerb,
                        nextAction = hold.NextAction,
                        itemId = hold.ItemId,
                        recommendation = hold.Recommendation
                    }).ToArray()
            }, amendmentHolds), decisions), AttentionAgentReadView.DutyRows(null, selectedGoals)), selectedGoals),
                waits, selectedGoals, renderTime.GetUtcNow());
        return AgentReadEnvelope.Serialize(AgentReadEnvelope.AttentionShowSchema, AgentReadEnvelope.AttentionShowSchemaVersion,
            WithRetryFeedbackDelivery(payload, selectedGoals),
            new JsonSerializerOptions(JsonSerializerDefaults.Web), renderTime);
    }

    private static Dictionary<string, object?> WithRetryFeedbackDelivery(Dictionary<string, object?> payload,
        IEnumerable<Goal> goals)
    {
        var rows = new List<object>();
        foreach (var goal in goals)
        {
            foreach (var task in goal.Tasks)
            {
                if (task.Status is WorkTaskStatus.Completed or WorkTaskStatus.Cancelled)
                {
                    continue;
                }

                var delivery = RetryFeedbackDeliveryClassifier.Classify(task);
                if (delivery.AcceptedAt is not { } acceptedAt)
                {
                    continue;
                }

                rows.Add(new
                {
                    goalId = goal.Id.Value,
                    taskId = task.Id.Value,
                    state = delivery.DeliveryState == RetryFeedbackDeliveryClassifier.State.Delivered
                        ? "delivered" : "awaiting-delivery",
                    acceptedAt = acceptedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                    promptHash = delivery.PromptHash,
                    dispatchedAt = delivery.DispatchedAt?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)
                });
            }
        }

        if (rows.Count > 0)
        {
            payload.Add("retryFeedbackDelivery", rows);
        }

        return payload;
    }

    private static Dictionary<string, object?> WithDecisions(Dictionary<string, object?> payload,
        OwnerDecisionRead? decisions)
    {
        if (decisions is null) return payload;
        payload["decisions"] = decisions.Entries.Select(entry => new
        {
            kind = entry.Kind.ToString(),
            goalPrefix = entry.GoalPrefix,
            itemId = entry.ItemId,
            raisedAt = entry.RaisedAt?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            text = entry.Text
        }).ToArray();
        payload["hiddenByReason"] = decisions.HiddenByReason;
        return payload;
    }
}
