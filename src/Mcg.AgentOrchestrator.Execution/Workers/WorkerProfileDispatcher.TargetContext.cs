namespace Mcg.AgentOrchestrator.Infrastructure;

public static partial class WorkerProfileDispatcher
{
    internal static TargetContext? TryReadCurrentTargetContext(string workingDirectory,
        Func<string, string, string?>? resolveRevision = null)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return null;
        }

        var branch = GitRevisionReader.TryReadCurrentBranch(workingDirectory, out var currentBranch) &&
            (currentBranch is null || currentBranch.All(char.IsAscii))
            ? new GitCli.GitResult(0, currentBranch ?? "", "")
            : GitCli.Run(workingDirectory, "branch", "--show-current");
        var resolved = resolveRevision is not null ? resolveRevision(workingDirectory, "HEAD") :
            GitRevisionReader.Shared.TryResolve(workingDirectory, "HEAD", out var id) ? id : null;
        var head = resolved is not null ? new GitCli.GitResult(0, resolved + "\n", "") :
            GitCli.Run(workingDirectory, "rev-parse", "HEAD");
        if (branch.ExitCode != 0 || head.ExitCode != 0)
        {
            return null;
        }

        var branchName = branch.Output.Trim();
        var headCommit = head.Output.Trim();
        if (string.IsNullOrWhiteSpace(branchName) && string.IsNullOrWhiteSpace(headCommit))
        {
            return null;
        }

        return new TargetContext(
            string.IsNullOrWhiteSpace(branchName) ? null : branchName,
            string.IsNullOrWhiteSpace(headCommit) ? null : headCommit);
    }

    internal sealed record TargetContext(string? BranchName, string? HeadCommit);
}
