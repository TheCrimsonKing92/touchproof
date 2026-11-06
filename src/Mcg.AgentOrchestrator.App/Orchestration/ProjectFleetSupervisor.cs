namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ProjectFleetChild(
    string Name, string State, int? ProcessId, string? BuildCommit,
    ProjectEnvironmentResolution Resolution, Task<ConductorSupervisorProcessResult>? Completion = null,
    string? Error = null);

internal sealed record ProjectFleetStartPass(IReadOnlyList<ProjectFleetChild> Children);

internal sealed class ProjectFleetSupervisor(
    IConductorSupervisorProcessHost host,
    ProjectEnvironmentResolver resolver,
    IConductLockPidProbe? pidProbe,
    Func<string> readBuildCommit,
    string? homeRoot = null)
{
    public async Task<ProjectFleetStartPass> StartAsync(
        IReadOnlyList<OrchestratorProject> projects, CancellationToken cancellationToken)
    {
        var starts = projects.Where(project => project.Enabled)
            .OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
            .Select(project => StartOneAsync(project, cancellationToken)).ToArray();
        return new(await Task.WhenAll(starts).ConfigureAwait(false));
    }

    private async Task<ProjectFleetChild> StartOneAsync(OrchestratorProject project, CancellationToken token)
    {
        var resolution = resolver.Resolve(project.ToolRequirements);
        ProjectFleetChild Outcome(string state, string? error = null) =>
            new(project.Name, state, null, null, resolution, Error: error);
        if (token.IsCancellationRequested) return Outcome("cancelled");
        if (homeRoot is not null && Path.TrimEndingDirectorySeparator(Path.GetFullPath(homeRoot)).Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(project.RootDirectory)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return Outcome("home-skipped");
        var workspace = project.ResolveWorkspace();
        try
        {
            if (ConductorLoopLease.IsActive(workspace.OrchestratorDirectory, pidProbe))
                return Outcome("already-running");
        }
        catch (InvalidOperationException ex) { return Outcome("lock-invalid", ex.Message); }
        if (resolution.Error is not null) return Outcome("environment-unavailable", resolution.Error);
        if (resolution.Missing.Count > 0) return Outcome("missing-requirements");

        try
        {
            var environment = new Dictionary<string, string>(resolution.Environment, StringComparer.OrdinalIgnoreCase);
            environment.Remove(OrchestratorWorkspace.RepoRootEnvironmentVariable);
            environment.Remove(ConductorContinuitySupervisor.StdoutLogPathEnvironmentVariable);
            environment.Remove(ConductorContinuitySupervisor.StderrLogPathEnvironmentVariable);
            var id = Guid.NewGuid().ToString("N");
            var started = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var commit = readBuildCommit();
            var request = new ConductorSupervisorProcessRequest(
                ["conduct", "--loop", "--project=" + project.Name], project.RootDirectory,
                Path.Combine(workspace.OrchestratorDirectory, "fleet", id + ".exit.json"),
                Path.Combine(workspace.LogDirectory, "fleet-" + id + ".out.log"),
                Path.Combine(workspace.LogDirectory, "fleet-" + id + ".err.log"),
                OnProcessStarted: pid => started.TrySetResult(pid), AdditionalEnvironment: environment);
            var completion = host.RunAsync(request, token);
            // The host owns termination on cancellation. Keep observing it until startup or exit,
            // so cancellation cannot abandon a child while its process tree is being stopped.
            await Task.WhenAny(started.Task, completion).ConfigureAwait(false);
            if (started.Task.IsCompletedSuccessfully)
                return new(project.Name, "started", started.Task.Result, commit, resolution, completion);
            var result = await completion.ConfigureAwait(false);
            return Outcome("start-failed", $"Child exited before reporting its pid (exit {result.ExitCode}).");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { return Outcome("start-failed", ex.Message); }
    }
}
