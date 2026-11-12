using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Applies only the recommendation belonging to the goal's current, unanswered Author hold.
internal sealed class AuthorRecommendationApplication(
    ICollaborationItemStore collaboration, IOperatorIntentStore intents,
    ConductEventLogWriter conduct, Func<DateTimeOffset> utcNow, Func<TimeSpan?> waitProvider)
{
    internal static TimeSpan? LoadWait(string orchestratorDirectory)
    {
        try
        {
            return TimeSpan.FromSeconds(ConductorAutonomyPolicy.LoadFromOrchestratorDirectory(
                new DirectoryInfo(orchestratorDirectory)).AuthorRecommendationWaitSeconds);
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static bool IsEligible(AuthorOwnerQuestionHold question, DateTimeOffset startedAt,
        DateTimeOffset now, TimeSpan wait) => wait > TimeSpan.Zero &&
        question.Reversibility == OwnerQuestionReversibility.Reversible &&
        !string.IsNullOrWhiteSpace(question.Recommendation) && now - startedAt >= wait;

    internal static string FormatAnswer(AuthorOwnerQuestionHold question, TimeSpan wait) =>
        $"AUTHOR_RECOMMENDATION_APPLIED\nquestion={question.Question}\nreason={question.Reason}\n" +
        $"class={question.Reversibility.ToString().ToLowerInvariant()}\nwait-seconds={Seconds(wait)}\n" +
        question.Recommendation;

    internal void ServiceTick(AgentOrchestratorKernel kernel, string? onlyGoalId)
    {
        if (waitProvider() is not { } wait || wait <= TimeSpan.Zero) return;
        foreach (var goal in kernel.Goals.ToArray())
        {
            if (goal.IsTerminal || goal.Status == GoalStatus.Parked ||
                (onlyGoalId is not null && goal.Id.Value != onlyGoalId)) continue;
            var hold = goal.CurrentHold;
            if (AuthorOwnerQuestionHold.TryParse(hold) is not { } question ||
                !IsEligible(question, hold!.StartedAt, utcNow(), wait) ||
                !Enum.TryParse<OperatorAnswerTargetKind>(question.TargetKind, out var kind) ||
                kind is not (OperatorAnswerTargetKind.Clarification or OperatorAnswerTargetKind.HumanInput)) continue;

            var item = new ConductorAuthorItem(kind, question.TargetId, goal.Id.Value, question.Question, null);
            var references = new[] { $"author-item={item.Identity}" };
            var payload = new AnswerOperatorIntentPayload(kind, item.TargetId, item.GoalId,
                FormatAnswer(question, wait), OperatorActorKind.Agent, references, null);
            var candidate = new OperatorIntentRecord(Guid.NewGuid().ToString("N"), $"author-{item.Identity}",
                OperatorIntentVerbs.Answer, item.GoalId, null,
                JsonSerializer.Serialize(payload, OperatorIntentJson.Options), [], "author", "conductor-author",
                "author", utcNow(), ActorKind: OperatorActorKind.Agent);

            // Re-read target and queued answers immediately before submission. Passing the deadline
            // does not reserve this question against an answer already recorded by a person.
            if (!IsUnanswered(kernel, goal, kind, question.TargetId)) continue;
            var recorded = intents.ListForGoalAsync(goal.Id.Value, int.MaxValue).GetAwaiter().GetResult();
            if (recorded.Any(intent => intent.IdempotencyKey == candidate.IdempotencyKey ||
                    intent.Status != OperatorIntentStatus.Rejected && Targets(intent, kind, question.TargetId))) continue;
            if (goal.IsTerminal || goal.Status == GoalStatus.Parked || goal.CurrentHold != hold) continue;

            var submitted = intents.EnqueueAsync(candidate).GetAwaiter().GetResult();
            if (submitted.Id != candidate.Id) continue;
            conduct.Append("author", goal.Id.Value,
                $"item={item.Identity} kind=recommendation-applied reason={question.Reason} " +
                $"class={question.Reversibility.ToString().ToLowerInvariant()} wait-seconds={Seconds(wait)} intent={submitted.Id}");
        }
    }

    private bool IsUnanswered(AgentOrchestratorKernel kernel, Goal goal,
        OperatorAnswerTargetKind kind, string targetId) => kind switch
    {
        OperatorAnswerTargetKind.Clarification => collaboration.ListAsync(goal.Id.Value).GetAwaiter().GetResult()
            .Any(item => item.Id == targetId && item.GoalId == goal.Id.Value &&
                item.Type == CollaborationItemType.Clarification &&
                !CollaborationItemLifecycle.IsTerminal(item.Status) && item.AuthoritativeAnswer is null),
        OperatorAnswerTargetKind.HumanInput => kernel.HumanInputRequests.Any(request =>
            request.GoalId == goal.Id && request.Id.Value == targetId &&
            !request.IsCompleted && request.AuthoritativeAnswer is null),
        _ => false
    };

    private static bool Targets(OperatorIntentRecord intent, OperatorAnswerTargetKind kind, string targetId)
    {
        if (intent.Verb != OperatorIntentVerbs.Answer) return false;
        try
        {
            var answer = JsonSerializer.Deserialize<AnswerOperatorIntentPayload>(intent.PayloadJson, OperatorIntentJson.Options);
            return answer?.TargetKind == kind && answer.TargetId == targetId;
        }
        catch (JsonException) { return false; }
    }

    private static string Seconds(TimeSpan wait) => wait.TotalSeconds.ToString("0.#######", CultureInfo.InvariantCulture);
}
