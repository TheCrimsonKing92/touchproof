using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal enum ExecutorScreeningRefusalCause
{
    AdmissionDisabled,
    NoLanes,
    SlotBusy
}

internal sealed record ExecutorScreeningRefusal(ExecutorScreeningRefusalCause Cause)
{
    internal int? Slot { get; init; }
    internal IReadOnlyList<(string Attempt, string? Class)> Holders { get; init; } = [];

    internal string FormatIdleLine(double? waitedMinutes = null)
    {
        var reason = Cause switch
        {
            ExecutorScreeningRefusalCause.AdmissionDisabled => "admission-disabled",
            ExecutorScreeningRefusalCause.NoLanes => "no-lanes",
            ExecutorScreeningRefusalCause.SlotBusy => "slot-busy",
            _ => throw new InvalidOperationException($"Unknown executor-screen refusal cause {Cause}.")
        };
        var line = new StringBuilder("SCREEN idle reason=").Append(reason);
        if (Slot is { } slot) line.Append(" slot=").Append(slot.ToString(CultureInfo.InvariantCulture));
        foreach (var (attempt, claimClass) in Holders)
        {
            line.Append(" attempt=").Append(FormatField(attempt));
            if (claimClass is not null) line.Append(" class=").Append(FormatField(claimClass));
        }
        if (waitedMinutes is { } minutes)
            line.Append(" waited=").Append(minutes.ToString("G", CultureInfo.InvariantCulture));
        return line.ToString();
    }

    private static string FormatField(string value) =>
        value.Length > 0 && !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '"' or '\\' or '=')
            ? value
            : JsonSerializer.Serialize(value);
}

internal sealed class ExecutorScreeningOutcome<T> where T : class
{
    private readonly T? _receipt;

    internal ExecutorScreeningOutcome(T receipt) => _receipt = receipt;
    internal ExecutorScreeningOutcome(ExecutorScreeningRefusal refusal) => Refusal = refusal;

    internal ExecutorScreeningRefusal? Refusal { get; }
    internal T Receipt => _receipt ?? throw new InvalidOperationException("A refused executor screen has no receipt.");
}
