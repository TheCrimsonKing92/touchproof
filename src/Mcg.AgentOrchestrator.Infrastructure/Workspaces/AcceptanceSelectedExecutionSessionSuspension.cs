using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace Mcg.AgentOrchestrator.Infrastructure;

// A cross-process safety projection. Its home follows the same project identity as the gate.
internal sealed record AcceptanceSelectedExecutionSessionSuspension(string MissId, string? StateDirectory = null)
{
    internal static string Home(string worktree, string? projectHome) => projectHome ??
        Path.Combine(AcceptanceOwnerResultsRoot.ResolveOwnerResultsRootForCandidate(worktree), ".orchestrator");

    internal static string MarkerPath(string home, string? stateDirectory = null)
    {
        var suffix = stateDirectory is null ? "" : "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.GetFullPath(stateDirectory).ToUpperInvariant())));
        return Path.Combine(home, "selected-execution-backstop-miss" + suffix + ".json");
    }

    internal static async Task<AcceptanceSelectedExecutionSessionSuspension?> ReadAsync(string worktree, string? projectHome)
    {
        var home = Home(worktree, projectHome);
        try
        {
            // Each state identity owns its projection. One tenant cannot clear another's miss.
            foreach (var path in Directory.GetFiles(home, "selected-execution-backstop-miss*.json").Order(StringComparer.Ordinal))
            {
                AcceptanceSelectedExecutionSessionSuspension? value;
                try { value = JsonSerializer.Deserialize<AcceptanceSelectedExecutionSessionSuspension>(await File.ReadAllTextAsync(path).ConfigureAwait(false)); }
                catch (FileNotFoundException) { continue; } // Resolved concurrently after enumeration.
                if (value is not { MissId.Length: > 0 } ||
                    value.StateDirectory is not null && !Path.IsPathFullyQualified(value.StateDirectory) ||
                    !string.Equals(path, MarkerPath(home, value.StateDirectory), StringComparison.OrdinalIgnoreCase))
                    return new("unreadable-backstop-miss");
                // Consult the canonical attention store at gate entry, including child processes
                // launched before the conductor's next reconciliation tick.
                var item = (await CollaborationItemStore.ForDirectory(value.StateDirectory ?? home).ListAsync().ConfigureAwait(false))
                    .SingleOrDefault(item => item.CorrelationKey == "selected-execution-backstop-miss:" + value.MissId);
                if (item is null || !Mcg.AgentOrchestrator.Core.CollaborationItemLifecycle.IsTerminal(item.Status)) return value;
            }
            return null;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or Microsoft.Data.Sqlite.SqliteException or ArgumentException or NotSupportedException or InvalidOperationException)
        { return new("unreadable-backstop-miss"); }
    }

    internal static void Write(string home, AcceptanceSelectedExecutionSessionSuspension? value, string? stateDirectory = null)
    {
        var path = MarkerPath(home, stateDirectory);
        if (value is null) { File.Delete(path); return; }
        Directory.CreateDirectory(home);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value));
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}
