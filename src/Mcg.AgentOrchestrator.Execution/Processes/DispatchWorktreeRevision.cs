namespace Mcg.AgentOrchestrator.Infrastructure;

// Optional spawn/recovery metadata: an unreadable worktree supplies no revision.
internal static class DispatchWorktreeRevision
{
    internal static string? TryReadHead(string workingDirectory)
    {
        try
        {
            if (!Directory.Exists(workingDirectory))
                return null;
            var result = GitCli.Run(workingDirectory, "rev-parse", "HEAD");
            return result.Succeeded ? result.Output.Trim() : null;
        }
        catch
        {
            return null;
        }
    }
}
