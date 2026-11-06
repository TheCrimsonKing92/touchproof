namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ProjectFleetRollingRestartProject(
    string Name, string State, string? OldBuildCommit, string TargetBuildCommit,
    string? NewBuildCommit = null, int? ProcessId = null, string? Error = null,
    ProjectFleetChild? Child = null)
{
    public string? BuildCommit => State == "restarted" ? NewBuildCommit : OldBuildCommit;
}

internal sealed record ProjectFleetRollingRestartResult(IReadOnlyList<ProjectFleetRollingRestartProject> Projects)
{
    public IReadOnlyList<ProjectFleetStatusRow> BuildRows() => Projects.Select(project =>
        new ProjectFleetStatusRow(project.Name, project.State, project.ProcessId, project.BuildCommit,
            project.Child is { } child
                ? string.Join(", ", child.Resolution.ToolVariables.Select(pair => pair.Key + "=" + pair.Value)) : "",
            project.Child is { } outcome ? string.Join(", ", outcome.Resolution.Missing) : "",
            project.Error)).ToArray();
}

// One invocation owns its stop/wait/start sequence. Cross-process scheduling belongs to the caller.
internal sealed class ProjectFleetRollingRestart
{
    private readonly ProjectFleetSupervisor _supervisor;
    private readonly Func<OrchestratorProject, string?> _readOldBuildCommit;
    private readonly TimeProvider _time;
    private readonly IConductLockPidProbe? _pidProbe;
    private readonly string? _homeRoot;
    private readonly TimeSpan _stopTimeout;
    private readonly TimeSpan _pollInterval;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    internal ProjectFleetRollingRestart(
        ProjectFleetSupervisor supervisor, Func<OrchestratorProject, string?> readOldBuildCommit,
        TimeProvider time, IConductLockPidProbe? pidProbe = null, string? homeRoot = null,
        TimeSpan? stopTimeout = null, TimeSpan? pollInterval = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _supervisor = supervisor;
        _readOldBuildCommit = readOldBuildCommit;
        _time = time;
        _pidProbe = pidProbe;
        _homeRoot = homeRoot is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(homeRoot));
        _stopTimeout = stopTimeout ?? TimeSpan.FromSeconds(30);
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(100);
        if (_stopTimeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(stopTimeout));
        if (_pollInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(pollInterval));
        _delay = delay ?? ((duration, token) => Task.Delay(duration, time, token));
    }

    public async Task<ProjectFleetRollingRestartResult> RunAsync(
        IReadOnlyList<OrchestratorProject> projects, string targetBuildCommit, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetBuildCommit);
        var results = new List<ProjectFleetRollingRestartProject>();
        foreach (var project in projects.Where(project => project.Enabled)
                     .OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(project => project.Name, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            results.Add(await RestartAsync(project, targetBuildCommit, token).ConfigureAwait(false));
        }
        return new(results);
    }

    private async Task<ProjectFleetRollingRestartProject> RestartAsync(
        OrchestratorProject project, string targetBuildCommit, CancellationToken token)
    {
        string? oldCommit = null;
        string? ownedStopFile = null;
        var failureState = "failed-status-read";
        ProjectFleetRollingRestartProject Outcome(string state, string? error = null, ProjectFleetChild? child = null) =>
            new(project.Name, state, oldCommit, targetBuildCommit,
                state == "restarted" ? child?.BuildCommit : null, child?.ProcessId, error, child);

        string WithdrawStop(string error)
        {
            if (ownedStopFile is null) return error;
            try { File.Delete(ownedStopFile); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { return error + " Stop request cleanup failed: " + ex.Message; }
            return error;
        }

        try
        {
            oldCommit = _readOldBuildCommit(project);
            if (_homeRoot is not null && _homeRoot.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(project.RootDirectory)),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                return Outcome("skipped-home");

            failureState = "failed-lock-probe";
            var workspace = project.ResolveWorkspace();
            if (!ConductorLoopLease.IsActive(workspace.OrchestratorDirectory, _pidProbe))
                return Outcome("skipped-not-running");

            token.ThrowIfCancellationRequested();
            failureState = "failed-stop";
            if (RequestStop(workspace.ConductorStopFilePath)) ownedStopFile = workspace.ConductorStopFilePath;

            var startedAt = _time.GetTimestamp();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                failureState = "failed-lock-probe";
                // Re-read the CURRENT owner every time, including a successor that replaced the old pid.
                if (!ConductorLoopLease.IsActive(workspace.OrchestratorDirectory, _pidProbe)) break;
                var remaining = _stopTimeout - _time.GetElapsedTime(startedAt);
                if (remaining <= TimeSpan.Zero)
                    return Outcome("failed-stop-timeout", WithdrawStop("Timed out waiting for the conductor lock owner to exit."));
                await _delay(remaining < _pollInterval ? remaining : _pollInterval, token).ConfigureAwait(false);
            }

            token.ThrowIfCancellationRequested();
            failureState = "failed-stop";
            // An existing operator request still forbids restart after its lock owner exits.
            if (ownedStopFile is null)
                return Outcome("failed-stop", "A pre-existing stop request prevents restart; explicit operator clearance is required.");
            // The conductor observes our request but does not remove it on exit.
            File.Delete(ownedStopFile);
            ownedStopFile = null;

            failureState = "failed-start";
            var pass = await _supervisor.StartAsync([project], token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var child = pass.Children.Single();
            return child.State == "started" && child.ProcessId is not null
                ? Outcome("restarted", child: child)
                : Outcome("failed-start", string.IsNullOrWhiteSpace(child.Error) ? child.State : child.Error, child);
        }
        catch (OperationCanceledException ex) when (token.IsCancellationRequested)
        {
            var cleanup = WithdrawStop("");
            if (cleanup.Length > 0) throw new OperationCanceledException(ex.Message + cleanup, ex, token);
            throw;
        }
        catch (Exception ex)
        {
            return Outcome(failureState, WithdrawStop(string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message));
        }
    }

    private static bool RequestStop(string path)
    {
        try
        {
            // CreateNew distinguishes our request from a pre-existing operator stop request atomically.
            using var created = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
            return true;
        }
        catch (IOException) when (File.Exists(path))
        {
            using var existing = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            return false;
        }
    }
}
