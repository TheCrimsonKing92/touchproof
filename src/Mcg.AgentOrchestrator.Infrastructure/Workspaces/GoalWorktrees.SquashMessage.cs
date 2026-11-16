using System.Text;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static partial class GoalWorktrees
{
    internal static string ComposeSquashCommitMessage(
        string executionDirectory, string baseBranch, string mainHead, string oldHead, string tree, GoalId goalId)
    {
        var range = $"{mainHead}..{oldHead}";
        var prefix = $"Developer({Prefix(goalId)}):";
        var log = GitCli.Run(executionDirectory, "log", "--format=%h%x09%P%x09%s", range);
        var commits = log.Succeeded && !log.DrainTimedOut
            ? log.Output.Split('\n').Select(line => line.TrimEnd('\r').Split('\t', 3))
                .Where(fields => fields.Length == 3).ToArray()
            : [];
        var newestDeveloperSubject = commits.Select(fields => fields[2])
            .FirstOrDefault(subject => subject.StartsWith(prefix, StringComparison.Ordinal));
        var subject = newestDeveloperSubject is null
            ? "squashed goal branch"
            : newestDeveloperSubject[prefix.Length..];
        if (subject.StartsWith(' ')) subject = subject[1..];

        var message = new StringBuilder($"{prefix} {subject}\n\nSquashed onto {baseBranch} from {oldHead} (tree {tree}).");
        foreach (var fields in commits.Reverse())
        {
            // %P is empty for roots and contains a space only for merge commits.
            if (!fields[1].Contains(' '))
                message.Append('\n').Append("- ").Append(fields[0]).Append(' ').Append(fields[2]);
        }

        return message.Append("\n\nGoal: ").Append(goalId.Value).ToString();
    }
}
