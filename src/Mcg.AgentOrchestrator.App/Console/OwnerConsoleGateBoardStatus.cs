namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleGateBoardStatus
{
    internal static string Failing(OwnerConsoleGateCount count)
    {
        var text = $"in gate: FAILING, {count.Failed} failed";
        if (count.FailedItems.Count > 0)
        {
            text += ": " + count.FailedItems[0].Name;
            if (count.Failed > 1) text += $" +{count.Failed - 1}";
        }
        return text + (count.Confirming ? ", confirming" : "");
    }
}
