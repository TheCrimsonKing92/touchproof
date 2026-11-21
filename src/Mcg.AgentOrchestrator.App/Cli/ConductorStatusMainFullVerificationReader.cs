using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class ConductorStatusMainFullVerificationReader
{
    internal static ConductorStatusSnapshot.MainFullVerificationObservation? Read(string path)
    {
        try
        {
            var latest = new MainFullVerificationStore(path).Latest("completed");
            return latest is { CompletedAt: { } completed, DurationSeconds: { } duration, Result: { } result }
                ? new(latest.Commit, result.Outcome, latest.StartedAt, completed, duration)
                : null;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
