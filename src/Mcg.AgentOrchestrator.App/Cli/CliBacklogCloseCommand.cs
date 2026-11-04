using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliBacklogCloseCommand
{
    internal static void Execute(BacklogStore store, string itemId, string? reason)
    {
        var closed = store.CloseAsync(itemId, reason).GetAwaiter().GetResult();
        Console.WriteLine($"Closed: [{closed.Id}] {closed.Title}");
        Console.Write(BacklogTextView.RenderBacklogCloseFollowUp(closed.Id,
            id => store.GetByExactIdAsync(id).GetAwaiter().GetResult()));
    }
}
