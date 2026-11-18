using System.Globalization;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class OwnerQuestionHoldAttentionView
{
    internal static bool Print(IEnumerable<Goal> goals, DateTimeOffset now, TextWriter? output = null)
    {
        var holds = goals
            .Where(goal => goal.Status is not (GoalStatus.Completed or GoalStatus.Cancelled or GoalStatus.Superseded))
            .Select(OwnerQuestionHoldBlocker.TryRead).OfType<OwnerQuestionHoldBlocker>().ToArray();
        var writer = output ?? Console.Out;
        foreach (var hold in holds)
        {
            var item = hold.ItemId is null ? "" : $" item={hold.ItemId}";
            var started = hold.StartedAt.ToString("u", CultureInfo.InvariantCulture);
            var held = Math.Max(0, (now - hold.StartedAt).TotalSeconds).ToString("F0", CultureInfo.InvariantCulture);
            writer.WriteLine($"  [{hold.State}] goal={hold.GoalId.Value[..8]}{item} started={started} held={held}s");
            writer.WriteLine($"    question: {hold.Question}");
            if (hold.Recommendation is not null)
                writer.WriteLine($"    recommendation: {hold.Recommendation}");
            writer.WriteLine($"    answer: {hold.NextAction}");
        }
        return holds.Length > 0;
    }
}
