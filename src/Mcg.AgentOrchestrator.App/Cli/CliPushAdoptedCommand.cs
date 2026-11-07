using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliPushAdoptedCommand
{
    internal const string Usage = "Usage: push-adopted <commit> [--remote <name>] [--branch <name>]";

    internal static int Run(IReadOnlyList<string> args, OrchestratorWorkspace workspace)
    {
        var log = new ConductEventLogWriter(workspace.ConductEventsLogPath);
        return Run(args, AdoptedBuildPushService.CreateDefault(workspace.RootDirectory,
            line => log.Append("adopted-build-push", null, line)), Console.Out, Console.Error,
            workspace.IntegrationBranch);
    }

    internal static int Run(IReadOnlyList<string> args, AdoptedBuildPushService service,
        TextWriter output, TextWriter error, string defaultBranch = TrunkBranchName.Default)
    {
        try
        {
            CliCommandHelp.ThrowIfInvalidFlags(args);
            if (args.Count < 2 || !args[0].Equals("push-adopted", StringComparison.OrdinalIgnoreCase) ||
                args[1].StartsWith('-')) throw new ArgumentException(Usage);
            var remote = "origin";
            var branch = defaultBranch;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 2; i < args.Count; i += 2)
            {
                if (i + 1 >= args.Count || !seen.Add(args[i]) || args[i + 1].StartsWith('-'))
                    throw new ArgumentException(Usage);
                if (args[i].Equals("--remote", StringComparison.OrdinalIgnoreCase)) remote = args[i + 1];
                else if (args[i].Equals("--branch", StringComparison.OrdinalIgnoreCase)) branch = args[i + 1];
                else throw new ArgumentException(Usage);
            }
            var pushed = service.Push(args[1], remote, branch);
            output.WriteLine(pushed ? "Adopted build pushed." : "Adopted build push held; see attention and conduct events.");
            return pushed ? 0 : 1;
        }
        catch (ArgumentException) { error.WriteLine(Usage); return 1; }
    }
}
