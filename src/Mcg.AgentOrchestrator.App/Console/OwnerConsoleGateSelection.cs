using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// The recorded decision for one execution, independent of its progress or outcome.
internal sealed record OwnerConsoleGateSelection(bool Full, string? Reason = null)
{
    internal string Label => Full ? "full gate" : "selected";
    internal string? ShortReason
    {
        get
        {
            if (!Full || Reason is null) return null;
            var reason = Reason.Split(':', 2)[0];
            return reason.Length > 32 ? reason[..31] + "…" : reason;
        }
    }

    internal static OwnerConsoleGateSelection? From(AcceptanceSelectedExecutionRecord? decision) => decision?.Decision switch
    {
        "full-gate" => new(true, SingleLine(decision.FullGateReason)),
        "plan" => new(false),
        _ => null
    };

    private static string? SingleLine(string? value) => string.IsNullOrWhiteSpace(value) ? null :
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
