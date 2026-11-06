using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class GoalClarificationItemsReader
{
    internal const string StoreName = "collaboration-items";
    internal const string Operation = "status-read";

    internal sealed record Result(IReadOnlyList<CollaborationItem> Items, string? DegradedRead);

    internal static Result Read(string collaborationPath, string goalId, StartupSqliteRetryOptions? retry = null)
    {
        if (!File.Exists(collaborationPath))
            return new Result([], null);

        try
        {
            var items = StartupSqliteRetry.Run(collaborationPath, Operation,
                () => CollaborationItemStore.OpenReadOnly(collaborationPath)
                    .ListAsync(goalId).GetAwaiter().GetResult(), retry);
            return new Result(items, null);
        }
        catch (Exception ex) when (TryFormatExhausted(ex, out var line))
        {
            return new Result([], line);
        }
    }

    private static bool TryFormatExhausted(Exception error, out string? line)
    {
        line = null;
        var sqlite = error as SqliteException ??
            (error is InvalidOperationException ? error.InnerException as SqliteException : null);
        // Only the retry helper's exhausted, stamped failures may degrade a read.
        if (sqlite?.SqliteErrorCode is not (5 or 6) ||
            error.Data["Mcg.DatabasePath"] is not string path ||
            error.Data["Mcg.Operation"] is not string operation ||
            error.Data["Mcg.AttemptCount"] is not int attempts ||
            error.Data["Mcg.ElapsedMilliseconds"] is not double elapsed)
            return false;

        line = FormattableString.Invariant(
            $"{StoreName} unavailable store={path} operation={operation} attempts={attempts} elapsed_ms={elapsed:0}");
        return true;
    }
}
