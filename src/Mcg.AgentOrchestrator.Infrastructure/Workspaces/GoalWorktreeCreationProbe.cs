namespace Mcg.AgentOrchestrator.Infrastructure;

// Facts needed only by Ensure's creation path. Unknown file state still belongs to Git.
internal static class GoalWorktreeCreationProbe
{
    internal readonly record struct Facts(string GitDirectory, bool BranchExists);

    internal static string[] Arguments(string branch) =>
        ["rev-parse", "--is-inside-work-tree", "--absolute-git-dir", "--verify", "--quiet", $"refs/heads/{branch}"];

    internal static Facts? Resolve(string directory, string branch)
    {
        var workTreeConfirmed = GitRevisionReader.TryConfirmWorkTree(directory);
        var directoryConfirmed = GitRevisionReader.TryResolveRootGitDirectory(directory, out var gitDirectory);
        var branchAbsent = GitRevisionReader.TryConfirmBranchAbsent(directory, branch);
        if (workTreeConfirmed && directoryConfirmed && branchAbsent)
            return new Facts(gitDirectory, BranchExists: false);

        var facts = Classify(GitCli.Run(directory, Arguments(branch)));
        if (facts is not { } resolved) return null;
        // A change between the file reads and the probe must not become a guessed answer.
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (directoryConfirmed && !string.Equals(gitDirectory, resolved.GitDirectory, comparison) ||
            branchAbsent && resolved.BranchExists) return null;
        return resolved;
    }

    // Git prints the first two queries before the deferred --verify check. Only a quiet
    // exit 1 with exactly those two lines proves absence; other failures stay undecided.
    internal static Facts? Classify(GitCli.GitResult result)
    {
        if (!result.ProcessStarted || result.DrainTimedOut || !string.IsNullOrEmpty(result.Error) ||
            result.ExitCode is not (0 or 1) || string.IsNullOrEmpty(result.Output)) return null;
        var output = result.Output.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (output.EndsWith('\n')) output = output[..^1];
        var lines = output.Split('\n');
        if (lines.Length != (result.ExitCode == 0 ? 3 : 2) || lines[0] != "true" ||
            !Path.IsPathFullyQualified(lines[1]) || !Directory.Exists(lines[1])) return null;
        if (result.ExitCode == 0 && (lines[2].Length is not (40 or 64) || !lines[2].All(char.IsAsciiHexDigit)))
            return null;
        return new Facts(Path.GetFullPath(lines[1]), BranchExists: result.ExitCode == 0);
    }
}
