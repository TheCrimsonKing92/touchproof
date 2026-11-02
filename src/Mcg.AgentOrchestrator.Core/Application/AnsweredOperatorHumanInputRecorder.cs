namespace Mcg.AgentOrchestrator.Core;

// Publishes completed operator evidence without creating a wait or changing task/goal status.
internal static class AnsweredOperatorHumanInputRecorder
{
    // The caller holds the kernel's human-input lock through completion and publication.
    internal static HumanInputRequest Record(Goal goal, string question, string answer, DateTimeOffset now,
        IDictionary<HumanInputRequestId, HumanInputRequest> requests,
        Action<Goal, TaskId?, ProgressKind, string> append)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        ArgumentException.ThrowIfNullOrWhiteSpace(answer);
        if (AgentOrchestratorKernel.IsReopenProtectedGoalStatus(goal.Status))
            throw new InvalidOperationException("Cannot record operator evidence on a terminal goal.");
        var request = new HumanInputRequest(HumanInputRequestId.New(), goal.Id, taskId: null,
            question, now, HumanWaitKind.Other, isAutoDefaultable: false, isDismissible: false);
        request.Complete(answer, now, origin: HumanInputAnswerOrigin.Operator,
            briefVersion: goal.AuthoritativeBrief.Version);
        append(goal, null, ProgressKind.HumanInputReceived, answer);
        requests.Add(request.Id, request);
        return request;
    }
}
