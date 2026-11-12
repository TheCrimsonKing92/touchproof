using System.Text.Json;

namespace Mcg.AgentOrchestrator.Core;

// Task-private authoritative answers are retry context, regardless of who submitted them.
internal static class AnsweredHumanInputRetryContext
{
    internal static IEnumerable<string> Select(
        Goal goal, TaskSpec task, IEnumerable<HumanInputRequest> requests) =>
        requests
            .Where(request =>
                request.GoalId == goal.Id &&
                request.TaskId == task.Id &&
                request.IsCompleted &&
                !request.WasDismissed &&
                !request.IsSyntheticParkedHumanWaitCompletion &&
                request.SupersededByRequestId is null &&
                !string.IsNullOrWhiteSpace(request.AuthoritativeAnswer?.Text))
            // Encode the pair unambiguously even when an answer contains separators.
            .Select(request => "human-input-answer\u001f" +
                JsonSerializer.Serialize(new[] { request.Id.Value, request.AuthoritativeAnswer!.Text }));
}
