using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static OwnerConsoleGoalChangeSummaryFixtures;

internal static class OwnerConsoleGoalChangeSummaryAcceptanceJournal
{
    internal static Task<RunEventRecord> AppendAcceptanceAsync(SqliteRunEventStore store, GoalId id,
        string outcome, string candidate, string main) => store.AppendAsync(new(RunEventTypes.GoalOperation,
        id.Value, "acceptance", outcome == "failed" ? "Failed" : "Completed", "full gate",
        AcceptancePayload(id, outcome, candidate, main), At.AddMinutes(3)), Token);

    internal static string AcceptancePayload(GoalId id, string outcome, string candidate, string main, string? detail = null)
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        return JsonSerializer.Serialize(new GoalOperationJournalEntry(Guid.NewGuid().ToString("N"), id, "acceptance",
            outcome == "failed" ? GoalOperationStatus.Failed : GoalOperationStatus.Completed, At.AddMinutes(3),
            detail ?? "full gate", candidate, main, outcome), options);
    }
}
