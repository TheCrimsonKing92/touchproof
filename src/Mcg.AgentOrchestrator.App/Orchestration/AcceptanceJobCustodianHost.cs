using System.Runtime.InteropServices;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class AcceptanceJobCustodianHost
{
    internal const string Subcommand = "__acceptance-custodian";

    internal static AcceptanceJobCustodianLaunchSpecification Bind(OrchestratorWorkspace workspace) =>
        Bind(Environment.ProcessPath, typeof(AcceptanceJobCustodianHost).Assembly.Location,
            RuntimeEnvironment.GetRuntimeDirectory(), workspace);

    internal static AcceptanceJobCustodianLaunchSpecification Bind(string? processPath, string application,
        string runtimeDirectory, OrchestratorWorkspace workspace)
    {
        var host = ResolveHost(processPath, application, runtimeDirectory);
        IReadOnlyDictionary<string, string>? environment = null;
        if (workspace.IsProjectScoped)
        {
            var parent = Directory.GetParent(Path.TrimEndingDirectorySeparator(workspace.OrchestratorDirectory));
            var projects = workspace.IsTenantScoped ? parent?.Parent?.Parent : parent;
            var dataRootDirectory = projects?.Parent?.FullName;
            if (dataRootDirectory is null)
                throw new InvalidOperationException("Acceptance custody cannot derive the project data root from the workspace state directory.");
            var expected = OrchestratorWorkspace.ForProject(workspace.ProjectName, workspace.RootDirectory,
                workspace.ExecutionDirectory, workspace.TenantName, dataRootDirectory: dataRootDirectory);
            if (!WorkspaceInvocationBinding.SamePath(expected.OrchestratorDirectory, workspace.OrchestratorDirectory))
                throw new InvalidOperationException("Acceptance custody project state directory does not match its workspace layout.");
            var dataRoot = OrchestratorDataRoot.FromDirectory(dataRootDirectory);
            environment = new Dictionary<string, string>
            {
                [OrchestratorDataRoot.EnvironmentVariable] = dataRoot.RootDirectory,
                [OrchestratorProjectRegistry.RegistryHomeEnvironmentVariable] = dataRoot.ProjectsDirectory
            };
        }
        var args = new List<string>
        {
            application, "--repository-root", workspace.RootDirectory,
            "--expected-state-directory", workspace.OrchestratorDirectory,
            "--project", workspace.ProjectName, "--tenant", workspace.TenantName, Subcommand
        };
        return new(host, args, workspace.ExecutionDirectory, environment);
    }

    internal static AcceptanceJobCustodianLaunchSpecification BindShadowStart(string scratch, string state)
    {
        var application = typeof(AcceptanceJobCustodianHost).Assembly.Location;
        var host = ResolveHost(Environment.ProcessPath, application, RuntimeEnvironment.GetRuntimeDirectory());
        return new(host,
            [application, "--repository-root", scratch, "--expected-state-directory", state,
             "--project", OrchestratorWorkspace.DefaultProjectName, "--tenant", OrchestratorWorkspace.DefaultTenantName,
             Subcommand], scratch, new Dictionary<string, string>
            {
                [OrchestratorWorkspace.RepoRootEnvironmentVariable] = scratch,
                [OrchestratorDataRoot.EnvironmentVariable] = Path.Combine(scratch, "data"),
                [OrchestratorProjectRegistry.RegistryHomeEnvironmentVariable] = Path.Combine(scratch, "projects")
            });
    }

    private static string ResolveHost(string? processPath, string application, string runtimeDirectory)
    {
        // Derive the muxer from the loaded shared runtime. A dotnet-hosted caller must agree with that installation.
        if (!Path.IsPathFullyQualified(runtimeDirectory) || !Path.IsPathFullyQualified(application))
            throw new InvalidOperationException("Acceptance custody cannot establish the current managed runtime closure.");
        var root = Directory.GetParent(Path.TrimEndingDirectorySeparator(runtimeDirectory))?.Parent?.Parent?.FullName;
        var host = root is null ? null : Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        if (host is null || !Path.IsPathFullyQualified(host) || !File.Exists(host) ||
            string.IsNullOrWhiteSpace(processPath) || !Path.IsPathFullyQualified(processPath) ||
            (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
                !WorkspaceInvocationBinding.SamePath(host, processPath)) ||
            !WorkspaceInvocationBinding.SamePath(application, typeof(AcceptanceJobCustodianHost).Assembly.Location) ||
            !File.Exists(Path.ChangeExtension(application, ".runtimeconfig.json")) ||
            !File.Exists(Path.ChangeExtension(application, ".deps.json")))
            throw new InvalidOperationException("Acceptance custody cannot establish the current managed runtime closure.");
        return host;
    }

    internal static int Run(IReadOnlyList<string> args)
    {
        if (args.Count != 1 || args[0] != Subcommand)
        {
            Console.Error.WriteLine("Invalid acceptance custody command.");
            return 1;
        }
        return AcceptanceJobCustodianNative.Run(Console.In, Console.Out, Console.Error);
    }
}
