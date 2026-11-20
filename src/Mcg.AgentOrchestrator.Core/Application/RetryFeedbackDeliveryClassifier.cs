using System.Text;

namespace Mcg.AgentOrchestrator.Core;

public static class RetryFeedbackDeliveryClassifier
{
    public enum State
    {
        NoAcceptedFeedback,
        AwaitingDelivery,
        Delivered
    }

    public sealed record Result(
        State DeliveryState,
        DateTimeOffset? AcceptedAt = null,
        string? PromptHash = null,
        DateTimeOffset? DispatchedAt = null);

    public static Result Classify(TaskSpec task)
    {
        if (task.AcceptedRetryFeedback is not { } feedback)
        {
            return new(State.NoAcceptedFeedback);
        }

        var dispatch = task.LastDispatch;
        if (dispatch is null || dispatch.DispatchedAt < feedback.AcceptedAt ||
            dispatch.ContextPackageReceipt?.RetryFeedbackPromptReceipt is not { } receipt ||
            !string.Equals(receipt.AcceptedFeedbackSha256, HashFeedback(feedback.Message), StringComparison.Ordinal))
        {
            return new(State.AwaitingDelivery, feedback.AcceptedAt);
        }

        return new(State.Delivered, feedback.AcceptedAt, receipt.GeneratedPromptSha256, dispatch.DispatchedAt);
    }

    public static string HashFeedback(string value) => WorkerContextArtifact.Hash(
        Encoding.UTF8.GetBytes(value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim()));
}
