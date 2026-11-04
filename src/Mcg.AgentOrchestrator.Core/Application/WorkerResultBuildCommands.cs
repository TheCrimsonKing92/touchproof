namespace Mcg.AgentOrchestrator.Core;

public static partial class WorkerResultBlockers
{
    internal static string? ReadCommands(string workerOutput)
    {
        var commands = EnumerateWorkerResultLines(SplitRetainedLines(workerOutput))
            .Where(line => TryFindField(line, "commands", out _)).ToArray();
        return commands.Length == 1 && TryFindField(commands[0], "commands", out var value) ? value : null;
    }
}
