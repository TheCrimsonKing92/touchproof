using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class GoalBranchArchive
{
    internal sealed record Result(string? Evidence = null, string? Failure = null);

    internal static Result PreserveUnmergedTip(
        string executionDirectory,
        GoalId goalId,
        string branch,
        Func<int> remainingMilliseconds)
    {
        var refName = $"refs/archive/goal/{goalId.Value}";
        var tipResult = GitCli.Run(executionDirectory, remainingMilliseconds(),
            "rev-parse", "--verify", $"refs/heads/{branch}^{{commit}}");
        var tip = tipResult.Output.Trim();
        if (!Succeeded(tipResult) || tip.Length != 40 || !tip.All(Uri.IsHexDigit))
        {
            return new Result(Failure:
                $"resolving the branch tip for {refName} failed: {Describe(tipResult)}");
        }

        var ancestry = GitCli.Run(executionDirectory, remainingMilliseconds(),
            "merge-base", "--is-ancestor", tip, "HEAD");
        if (Succeeded(ancestry))
        {
            return new Result();
        }

        if (ancestry.ExitCode != 1 || !ancestry.ProcessStarted || ancestry.DrainTimedOut ||
            !string.IsNullOrWhiteSpace(ancestry.Error))
        {
            return new Result(Failure:
                $"the ancestry check for branch tip {tip} against HEAD failed: {Describe(ancestry)}");
        }

        // Create only: an existing archive must never be moved, including by a competing writer.
        var write = GitCli.Run(executionDirectory, remainingMilliseconds(),
            "update-ref", "--no-deref", refName, tip, new string('0', 40));
        var confirmation = GitCli.Run(executionDirectory, remainingMilliseconds(),
            "rev-parse", "--verify", refName);
        if (Succeeded(confirmation))
        {
            var archivedTip = confirmation.Output.Trim();
            if (string.Equals(archivedTip, tip, StringComparison.Ordinal))
            {
                return new Result(Evidence: $"Archived tip {tip} at {refName}.");
            }

            return new Result(Failure:
                $"archive ref {refName} already exists at a different tip {archivedTip}; expected {tip}");
        }

        return new Result(Failure:
            $"archive ref {refName} could not be written and confirmed at {tip}: " +
            $"update-ref: {Describe(write)}; resolving the archive ref: {Describe(confirmation)}");
    }

    private static bool Succeeded(GitCli.GitResult result) =>
        result.Succeeded && result.ProcessStarted && !result.DrainTimedOut;

    private static string Describe(GitCli.GitResult result) =>
        $"git exit {result.ExitCode}, process started: {result.ProcessStarted}, " +
        $"drain timed out: {result.DrainTimedOut}" +
        (string.IsNullOrWhiteSpace(result.Error) ? string.Empty : $"; {result.Error.Trim()}");
}
