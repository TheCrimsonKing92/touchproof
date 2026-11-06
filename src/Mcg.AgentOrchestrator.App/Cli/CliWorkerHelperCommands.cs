using System.Globalization;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliWorkerHelperCommands
{
    internal static int RequestBuild(string[] args, string directory, TextWriter output, TextWriter error)
    {
        var projects = new List<string>();
        var configuration = "Debug";
        var requestKey = Guid.NewGuid().ToString("N");
        var busyTimeoutSeconds = 120;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index++)
        {
            var argument = args[index];
            if (!argument.StartsWith('-')) { projects.Add(argument); continue; }
            if (argument is not ("--configuration" or "--request-key" or "--busy-timeout-seconds") ||
                !seen.Add(argument) || ++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
                return Usage(error);
            switch (argument)
            {
                case "--configuration": configuration = args[index]; break;
                case "--request-key": requestKey = args[index]; break;
                case "--busy-timeout-seconds":
                    if (!int.TryParse(args[index], NumberStyles.None, CultureInfo.InvariantCulture, out busyTimeoutSeconds) ||
                        busyTimeoutSeconds is < 1 or > 2147483) return Usage(error);
                    break;
            }
        }
        if (projects.Count == 0) return Usage(error);
        return new WorkerBuildRequestClient(output, error).Run(directory,
            new WorkerBuildRequest(1, requestKey, projects.ToArray(), configuration), busyTimeoutSeconds);
    }

    internal static int SubmitResult(string[] args, TextWriter output, TextWriter error) =>
        WorkerResultSubmission.RunSubmitCommand(args[1..], output, error, () => DateTimeOffset.UtcNow);

    private static int Usage(TextWriter error)
    {
        error.WriteLine("Usage: worker-build-request <project.csproj>... [--configuration <name>] [--request-key <key>] [--busy-timeout-seconds <n>]");
        return 1;
    }
}
