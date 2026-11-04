namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleGateBoardStatus
{
    internal static string Failing(OwnerConsoleGateCount count)
    {
        var failures = Math.Max(count.Failed, count.FailedItems.Count);
        var text = $"in gate: FAILING, {failures} failed";
        if (count.FailedItems.Count > 0)
        {
            text += ": " + count.FailedItems[0].Name;
            if (failures > 1) text += $" +{failures - 1}";
        }
        return text + (count.Confirming ? ", confirming" : "");
    }
}
