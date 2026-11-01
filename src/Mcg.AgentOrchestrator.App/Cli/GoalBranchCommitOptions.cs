using System.Globalization;

namespace Mcg.AgentOrchestrator.App.Cli;

internal sealed record GoalBranchCommitOptions(string GoalPrefix, string? Message, bool Resume, string? Wait)
{
    internal static GoalBranchCommitOptions Parse(IReadOnlyList<string> args)
    {
        CliCommandHelp.ThrowIfInvalidFlags(args);
        if (args.Count < 2 || args[1].StartsWith("--")) throw new ArgumentException(CliGoalBranchCommitCommand.Usage);
        string? message = null;
        string? wait = null;
        var resume = false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 2; i < args.Count; i++)
        {
            var token = args[i].Split('=', 2);
            var flag = token[0].ToLowerInvariant();
            if (!seen.Add(flag)) throw new ArgumentException($"Duplicate flag: {flag}");
            if (flag == "--resume" && token.Length == 1) { resume = true; continue; }
            if (flag == "--wait")
            {
                var seconds = token.Length == 2 ? token[1]
                    : i + 1 < args.Count && !args[i + 1].StartsWith("--") ? args[++i] : "300";
                if (!int.TryParse(seconds, NumberStyles.None, CultureInfo.InvariantCulture, out var duration) || duration <= 0)
                    throw new ArgumentException("--wait requires a positive number of seconds.");
                wait = $"--wait={duration}";
                continue;
            }
            if (flag is not ("--message" or "--operator-actor" or "--actor-kind"))
                throw new ArgumentException(CliGoalBranchCommitCommand.Usage);
            var value = token.Length == 2 ? token[1]
                : i + 1 < args.Count && !args[i + 1].StartsWith("--") ? args[++i]
                : throw new ArgumentException($"{flag} requires a value.");
            if (flag == "--message") message = value;
        }
        if (resume ? message is not null : string.IsNullOrWhiteSpace(message))
            throw new ArgumentException(CliGoalBranchCommitCommand.Usage);
        return new(args[1], message, resume, wait);
    }
}
