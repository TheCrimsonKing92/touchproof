using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class ProjectFleetCommand
{
    private const string Usage = "Usage: project fleet start|enable <name> [--off]|require <name> <tool> [--env <VAR>]|require <name> --clear";

    public static int Execute(IReadOnlyList<string> parts, OrchestratorProjectRegistry registry,
        string homeRoot, TextWriter output)
    {
        if (parts.Count < 3) throw new ArgumentException(Usage);
        switch (parts[2].ToLowerInvariant())
        {
            case "enable" when parts.Count == 4 || parts.Count == 5 && parts[4] == "--off":
                registry.SetFleetEnabled(parts[3], parts.Count == 4);
                output.WriteLine($"Fleet project {parts[3]}: {(parts.Count == 4 ? "enabled" : "disabled")}");
                return 0;
            case "require" when parts.Count == 5 && parts[4] == "--clear":
                registry.SetToolRequirements(parts[3], []);
                output.WriteLine($"Fleet requirements cleared: {parts[3]}");
                return 0;
            case "require" when parts.Count == 5 || parts.Count == 7 && parts[5] == "--env":
                if (parts[4].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException(Usage);
                var project = registry.GetRequiredProject(parts[3]);
                var requirements = project.ToolRequirements
                    .Where(item => !item.Tool.Equals(parts[4], StringComparison.OrdinalIgnoreCase))
                    .Append(new ProjectToolRequirement(parts[4], parts.Count == 7 ? parts[6] : null)).ToArray();
                registry.SetToolRequirements(parts[3], requirements);
                output.WriteLine($"Fleet requirement recorded: {parts[3]} {parts[4]}");
                return 0;
            case "start" when parts.Count == 3:
                return Start(registry, homeRoot, output);
            default: throw new ArgumentException(Usage);
        }
    }

    private static int Start(OrchestratorProjectRegistry registry, string homeRoot, TextWriter output)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, args) => { args.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            var supervisor = new ProjectFleetSupervisor(new SystemConductorSupervisorProcessHost(),
                ProjectEnvironmentResolver.CreateSystem(), null,
                () => SystemConductorSupervisorHandoffSeam.ReadOwnBuild().CommitSha, homeRoot);
            return RunAsync(registry.ListProjects(), supervisor, output, cancellation.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return 130; }
        finally { Console.CancelKeyPress -= cancel; }
    }

    internal static async Task<int> RunAsync(IReadOnlyList<OrchestratorProject> projects,
        ProjectFleetSupervisor supervisor, TextWriter output, CancellationToken token)
    {
        var pass = await supervisor.StartAsync(projects, token).ConfigureAwait(false);
        ProjectFleetStatusReport.Write(output, ProjectFleetStatusReport.BuildRows(pass));
        var results = await Task.WhenAll(pass.Children.Where(child => child.Completion is not null)
            .Select(child => child.Completion!)).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return pass.Children.Any(child => child.State is "missing-requirements" or "environment-unavailable" or
            "lock-invalid" or "start-failed") || results.Any(result => result.ExitCode != 0) ? 1 : 0;
    }
}
