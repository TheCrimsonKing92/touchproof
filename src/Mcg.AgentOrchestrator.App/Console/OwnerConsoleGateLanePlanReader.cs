using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleGateLanePlanReader
{
    internal static string? ArtifactPath(string heartbeatPath, string attemptId)
    {
        if (string.IsNullOrWhiteSpace(attemptId) || !Path.IsPathRooted(heartbeatPath)) return null;
        try
        {
            var directory = Path.GetDirectoryName(heartbeatPath);
            return string.IsNullOrWhiteSpace(directory) ? null :
                Path.Combine(directory, attemptId + AcceptanceGateLanePlan.ArtifactSuffix);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException) { return null; }
    }

    internal AcceptanceGateLanePlan? Read(string artifactPath, string expectedAttemptId)
    {
        if (string.IsNullOrWhiteSpace(expectedAttemptId)) return null;
        try
        {
            var plan = JsonSerializer.Deserialize<AcceptanceGateLanePlan>(File.ReadAllText(artifactPath), AcceptanceGateLanePlan.JsonOptions);
            if (plan is null || plan.SchemaVersion != AcceptanceGateLanePlan.CurrentSchemaVersion ||
                !string.Equals(plan.AttemptId, expectedAttemptId, StringComparison.Ordinal) ||
                plan.Scheduled is null || plan.Skipped is null ||
                plan.Scheduled.Any(string.IsNullOrWhiteSpace) ||
                plan.Skipped.Any(row => row is null || string.IsNullOrWhiteSpace(row.Name) || string.IsNullOrWhiteSpace(row.Reason))) return null;
            var names = plan.Scheduled.Concat(plan.Skipped.Select(row => row.Name)).ToArray();
            return names.Distinct(StringComparer.Ordinal).Count() == names.Length ? plan : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
            ArgumentException or NotSupportedException or System.Security.SecurityException) { return null; }
    }
}
