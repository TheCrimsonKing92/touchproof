using Terminal.Gui.Text;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleBoardOrderKey
{
    private static readonly string Ranks = string.Join(" > ", Enum.GetValues<OwnerConsoleAttention>().Select(Label));

    internal static string Full { get; } = "Display order: " + Ranks +
        "; shared gate waiters oldest first, recently ended newest first, then case-insensitive goal id; not scheduler order.";

    internal static string Compact { get; } = "Display order: " + Ranks + "; not scheduler order.";

    internal static string Fit(int width) => width <= 0 ? "" : OwnerConsoleLineFitter.Fit(
        Full.GetColumns() <= width ? Full : Compact, OwnerConsoleLineSpans.None, width);

    private static string Label(OwnerConsoleAttention rank) => rank switch
    {
        OwnerConsoleAttention.NeedsYou => "needs you",
        OwnerConsoleAttention.Held => "held",
        OwnerConsoleAttention.GateRunning => "running in shared gate",
        OwnerConsoleAttention.WaitingForGate => "waiting for shared gate",
        OwnerConsoleAttention.WorkerRunning => "worker running",
        OwnerConsoleAttention.Other => "other",
        OwnerConsoleAttention.RecentTerminal => "recently ended",
        _ => throw new ArgumentOutOfRangeException(nameof(rank), rank, "Unknown board attention rank.")
    };
}
