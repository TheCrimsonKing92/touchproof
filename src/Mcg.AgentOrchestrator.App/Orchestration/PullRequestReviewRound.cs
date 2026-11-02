using System.Globalization;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Round ownership is durable timeline order, scoped to the delivered PR, never process-local state.
internal static class PullRequestReviewRound
{
    internal const string BatchPrefix = "pull request changes requested: ";
    internal const string StartedPrefix = "pull request review round started: ";
    internal const string DeliveredPrefix = "pull request review round delivered: ";
    internal const string StoppedPrefix = "pull request review round stopped: ";
    internal const string CapPrefix = "pull request review round cap reached: ";
    internal const string Instruction = "address every request with a code change or a reasoned reply in your WORKER_RESULT `summary`; do not resolve review threads; do not change acceptance criteria";

    private static IEnumerable<ProgressEvent> Decisions(Goal goal) => goal.Timeline
        .Where(item => item.Kind == ProgressKind.GoalPolicyDecision);
    private static IEnumerable<ProgressEvent> SinceDelivery(Goal goal) => Decisions(goal).Reverse()
        .TakeWhile(item => !PullRequestLandingDelivery.IsDeliveredForReview(item.Message));
    private static bool Matches(ProgressEvent item, string prefix, string url) =>
        item.Message.StartsWith(prefix + url + " ", StringComparison.Ordinal);

    internal static bool HasStarted(Goal goal) => goal.DeliveredForReview is { } delivery &&
        SinceDelivery(goal).Any(item => Matches(item, StartedPrefix, delivery.Url));
    internal static bool Stopped(Goal goal) => goal.DeliveredForReview is { } delivery &&
        SinceDelivery(goal).Any(item => Matches(item, StoppedPrefix, delivery.Url));
    internal static bool InFlight(Goal goal) => HasStarted(goal) && !Stopped(goal);
    internal static int Started(Goal goal, string url) => Decisions(goal).Count(item => Matches(item, StartedPrefix, url));
    internal static int Delivered(Goal goal, string url) => Decisions(goal).Count(item => Matches(item, DeliveredPrefix, url));

    internal static bool Pending(Goal goal, string url) => Decisions(goal).Reverse()
        .TakeWhile(item => !Matches(item, StartedPrefix, url))
        .Any(item => Matches(item, BatchPrefix, url));

    internal static bool CapRecorded(Goal goal, string url) => Decisions(goal).Reverse()
        .TakeWhile(item => !Matches(item, BatchPrefix, url))
        .Any(item => Matches(item, CapPrefix, url));

    internal static DateTimeOffset Boundary(Goal goal, string url, DateTimeOffset initialBoundary)
    {
        // Consume forge timestamps rather than the local start/push time: a batch observed during
        // an in-flight round must survive that round's later push and belong to the next round.
        var consumed = Decisions(goal).Reverse().SkipWhile(item => !Matches(item, StartedPrefix, url))
            .Skip(1).FirstOrDefault(item => Matches(item, BatchPrefix, url));
        if (consumed is null) return initialBoundary;
        var marker = consumed.Message.LastIndexOf(" latest=", StringComparison.Ordinal);
        if (marker < 0 || !DateTimeOffset.TryParse(consumed.Message[(marker + 8)..],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var boundary))
            throw new InvalidOperationException("Recorded pull request review batch has no usable timestamp.");
        return boundary;
    }

    internal static string Guidance(string url, int round, IReadOnlyList<PullRequestChangesRequest> batch) =>
        $"Pull request review round {round}: {url}\n" + string.Join("\n\n", batch.Select(review =>
            $"Reviewer: {review.Login}\nSubmittedAt: {review.SubmittedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)}\n{review.Body}")) +
        "\n\n" + Instruction;
}
