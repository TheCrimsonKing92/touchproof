using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class BackgroundDispatchRunner
{
    private static string? TryGetDirtyStateHash(string workingDirectory)
    {
        try
        {
            if (!Directory.Exists(workingDirectory))
                return null;

            var result = GitCli.Run(workingDirectory, "status", "--porcelain=v1", "--untracked-files=all");
            if (!result.Succeeded)
                return null;

            return DispatchWorktreeCommitter.ComputeDirtyStateHash(result.Output);
        }
        catch
        {
            return null;
        }
    }
}
