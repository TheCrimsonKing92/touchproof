namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>The tool environment shared by admission probes and final worker sandbox preparation.</summary>
public static class WorkerToolEnvironment
{
    public static ConductorChildEnvironment Prepare(ConductorChildEnvironment inherited, string sandboxRoot,
        bool lowIntegrity, bool disableSharedCompilation)
    {
        var variables = inherited.Variables.ToDictionary(pair => pair.Key, pair => pair.Value,
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        ApplySandbox(variables, sandboxRoot, lowIntegrity);
        if (disableSharedCompilation)
        {
            variables["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
            variables["MSBUILDDISABLENODEREUSE"] = "1";
            variables["UseSharedCompilation"] = "false";
        }
        WorkerMcgEnvironmentAllowlist.RemoveDisallowed(variables);
        variables[WorkerSandboxOptions.DispatchWorkerVariable] = "1";
        return new(variables);
    }

    // Pure preparation: admission must not create or relabel a sandbox before the host owns it.
    internal static void ApplySandbox(IDictionary<string, string?> variables, string sandboxRoot, bool lowIntegrity)
    {
        if (!OperatingSystem.IsWindows()) return;
        variables["PSModuleAnalysisCachePath"] = Path.Combine(sandboxRoot, "powershell", "ModuleAnalysisCache");
        GoToolCacheEnvironment.Apply(variables, sandboxRoot);
        if (!lowIntegrity) return;
        variables["TEMP"] = Path.Combine(sandboxRoot, "temp");
        variables["TMP"] = variables["TEMP"];
        variables.TryGetValue("PATH", out var path);
        variables["PATH"] = DispatchProcessHost.BuildLowIntegrityPath(path, WorkerShell.Executable,
            Path.Combine(sandboxRoot, "bin"));
    }
}
