using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class GroupedGateChildResultDocument
{
    internal static string Serialize(string identityValue, ConductorGroupedGateOutcome? outcome) =>
        outcome is null
            ? JsonSerializer.Serialize(new { IdentityValue = identityValue, Status = "completed" })
            : JsonSerializer.Serialize(new CompletedResult(identityValue, "completed", outcome.Verdict,
                outcome.ReceiptId, outcome.Detail));

    private sealed record CompletedResult(string IdentityValue, string Status, string Verdict,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReceiptId,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Detail);
}
