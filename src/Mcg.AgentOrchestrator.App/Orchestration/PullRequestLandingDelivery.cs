using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class PullRequestLandingDelivery
{
    private const string PendingRecordPrefix = "pull request delivery pending recording: ";
    public static bool Applies(OrchestratorWorkspace? workspace) => workspace is { IsProjectScoped: true, Delivery: ProjectDelivery.PullRequest };
    public static bool IsDeliveredForReview(string reason) => reason.StartsWith("delivered for review: ", StringComparison.Ordinal);

    public static LandingResult Deliver(AgentOrchestratorKernel kernel, Goal goal, OrchestratorWorkspace workspace,
        string candidate, IPullRequestPublisher publisher, IOperatorChannel? channel,
        IGoalLifecycleEventWriter? eventWriter, Func<string?>? mutationBlocker, IClock clock)
    {
        LandingResult Result(string reason, bool stateChanged = false) => new(goal.Id.Value, goal.Id.Value[..8],
            new LandingDecision.Escalate(reason), LandingExecutor.IntegrationBranchName, false, reason,
            StateChanged: stateChanged);
        LandingResult Fail(string reason)
        {
            OperatorInbox.RecordLandingEscalation(workspace, goal, reason, LandingExecutor.IntegrationBranchName, channel);
            eventWriter?.AppendGoalEscalated(goal.Id, GoalLifecycleState.Verified, goal.Status, reason, LandingExecutor.IntegrationBranchName);
            return Result(reason, stateChanged: true);
        }

        if (goal.DeliveredForReview is { } delivered && string.Equals(delivered.PushedCommit, candidate, StringComparison.OrdinalIgnoreCase))
            return Result($"delivered for review: {delivered.Url}");
        if (mutationBlocker?.Invoke() is { Length: > 0 } blocked)
            return Result($"landing mutation blocked: {blocked}");

        PullRequestPublishRequest request;
        string attemptId;
        try
        {
            var evidence = PullRequestGateEvidenceResolver.Resolve(workspace, goal, candidate);
            attemptId = evidence.AttemptId;
            var content = PullRequestBodyRenderer.Render(goal, attemptId, evidence.CheckNames,
                evidence.RedactionSource, workspace.RootDirectory, workspace.ExecutionDirectory, workspace.OrchestratorDirectory);
            var branch = GoalWorktreeLayout.BranchName(goal.Id);
            var previousCommit = goal.DeliveredForReview?.PushedCommit;
            // Incomplete publications retain push provenance in the goal timeline, never in scratch state.
            foreach (var progress in goal.Timeline.Reverse())
            {
                if (progress.Kind == ProgressKind.GoalPolicyDecision && IsDeliveredForReview(progress.Message)) break;
                if (progress.Kind != ProgressKind.GoalPolicyDecision ||
                    !(progress.Message.StartsWith("pull request delivery failed: ", StringComparison.Ordinal) ||
                      progress.Message.StartsWith(PendingRecordPrefix, StringComparison.Ordinal))) continue;
                var pushed = Regex.Match(progress.Message, $@"; pushed branch {Regex.Escape(branch)} commit ([0-9a-f]{{40}}|[0-9a-f]{{64}})\z");
                if (pushed.Success) { previousCommit = pushed.Groups[1].Value; break; }
            }
            request = new(workspace.ExecutionDirectory, branch, candidate, previousCommit, workspace.IntegrationBranch, content.Title, content.Body);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
        {
            return Fail($"pull request delivery failed: gh-failed: {ForgeDiagnosticRedactor.Redact(exception.Message)}");
        }

        var publication = publisher.Publish(request);
        if (!publication.Succeeded)
        {
            var reason = $"pull request delivery failed: {publication.FailureToken ?? "gh-failed"}: {ForgeDiagnosticRedactor.Redact(publication.Message ?? "publisher failed")}";
            if (publication.PushedCommit is { } pushed && publication.PushedBranch == request.GoalBranch)
                reason += $"; pushed branch {request.GoalBranch} commit {pushed}";
            kernel.RecordGoalPolicyDecision(goal.Id, reason);
            return Fail(reason);
        }
        try
        {
            kernel.RecordGoalDeliveredForReview(goal.Id, new GoalDeliveredForReview(publication.PullRequestNumber!.Value,
                publication.Url!, publication.PushedCommit!, attemptId, clock.UtcNow));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The remote publication can be reconciled on the next tick; do not escalate or roll it back.
            // Retain ownership if a new candidate is gated before the delivery record can be written.
            kernel.RecordGoalPolicyDecision(goal.Id,
                $"{PendingRecordPrefix}{exception.GetType().Name}; pushed branch {request.GoalBranch} commit {publication.PushedCommit}");
            return Result($"landing mutation blocked: delivered-for-review record write failed ({exception.GetType().Name})",
                stateChanged: true);
        }
        return Result($"delivered for review: {publication.Url}", stateChanged: true);
    }
}
