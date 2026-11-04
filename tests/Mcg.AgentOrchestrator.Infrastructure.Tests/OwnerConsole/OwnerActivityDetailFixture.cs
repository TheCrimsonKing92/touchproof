using System.Text.Json;
using Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerActivityDetailFixture
{
    internal static JsonDocument Read() => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
        "TestData", "Fixtures", "activity-detail-dispatch-rejected.json")));

    internal static OwnerConductEvent Event(JsonElement record)
    {
        // Replay persisted values into the real lifecycle transport contract. Role metadata
        // was not recorded, so it is not synthesized from the diagnostic message.
        var line = JsonSerializer.Serialize(new {
            occurredAt = record.GetProperty("OccurredAt").GetString(),
            eventType = record.GetProperty("Kind").GetString(),
            taskId = record.GetProperty("TaskId").GetString(),
            message = record.GetProperty("Message").GetString()
        });
        Assert.True(OwnerGoalLifecycleEvent.TryParse(line, record.GetProperty("GoalId").GetString()!, out var source));
        return source!;
    }

    internal static OwnerConsoleActivityItem Failure()
    {
        using var fixture = Read();
        return Assert.Single(OwnerActivityNarrator.Narrate([Event(fixture.RootElement.GetProperty("task_failure"))],
            _ => "Retained dispatch rejection (synthetic title)"));
    }
}
