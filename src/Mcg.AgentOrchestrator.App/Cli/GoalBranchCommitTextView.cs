using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class GoalBranchCommitTextView
{
    internal static void Print(string newHead, GoalBranchCommitRebindPlan? plan,
        IReadOnlyCollection<string> submitted, IEnumerable<HumanInputRequest>? waits)
    {
        Console.WriteLine($"New head: {newHead}");
        Console.WriteLine($"Rebinds submitted: {Join(submitted)}");
        Console.WriteLine($"Rebinds not submitted: {Join(plan?.Eligible.Select(duty => duty.Id).Except(submitted) ?? [])}");
        Console.WriteLine($"Left unchanged: {Join(plan?.Unchanged.Select(item => $"{item.Key} ({item.Value})") ?? [])}");
        if (waits is null)
        {
            Console.WriteLine("Open ProspectiveAcceptanceEvidence waits: unavailable after failure");
            return;
        }
        var open = waits.Where(wait => wait.Kind == HumanWaitKind.ProspectiveAcceptanceEvidence)
            .OrderBy(wait => wait.RequestedAt).ThenBy(wait => wait.Id.Value, StringComparer.Ordinal).ToArray();
        Console.WriteLine($"Open ProspectiveAcceptanceEvidence waits: {(open.Length == 0 ? "none" : open.Length.ToString())}");
        foreach (var wait in open)
            Console.WriteLine($"{wait.Id.Value}: {wait.Question}");
    }

    private static string Join(IEnumerable<string> values)
    {
        var items = values.ToArray();
        return items.Length == 0 ? "none" : string.Join(", ", items);
    }
}
