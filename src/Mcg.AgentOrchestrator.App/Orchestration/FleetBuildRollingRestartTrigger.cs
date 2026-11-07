using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Home-supervisor lifetime: at most one queued pass per commit, and one executing pass per fleet.
internal sealed class FleetBuildRollingRestartTrigger(
    Func<IReadOnlyList<OrchestratorProject>> listProjects,
    Func<IReadOnlyList<OrchestratorProject>, string, IReadOnlyList<string>?, CancellationToken,
        Task<ProjectFleetRollingRestartResult>> runPass,
    Action<string> append, string statusPath,
    Func<Func<Task>, Task>? schedule = null,
    Func<IReadOnlyList<string>?, IDisposable?>? acquireBuildLease = null)
{
    internal const string StatusFileName = "fleet-restart-status.txt";
    private readonly object _gate = new();
    private readonly HashSet<string> _reserved = new(StringComparer.OrdinalIgnoreCase);
    private Task _tail = Task.CompletedTask;

    internal void OnHomeAdopted(string commit, IReadOnlyList<string>? launchPrefix = null)
    {
        if (!HomeBuildAdoptionDetector.IsCommit(commit)) return;
        lock (_gate)
        {
            if (!_reserved.Add(commit)) return;
            var prefix = launchPrefix?.ToArray();
            var lease = acquireBuildLease?.Invoke(prefix);
            var previous = _tail;
            try
            {
                _tail = (schedule ?? (body => Task.Run(body)))(async () =>
                {
                    try { await previous.ConfigureAwait(false); }
                    catch (Exception) { /* A failed earlier pass never prevents the next commit. */ }
                    await RunAsync(commit, prefix, lease).ConfigureAwait(false);
                });
            }
            catch { lease?.Dispose(); throw; }
        }
    }

    internal Task WhenIdle() { lock (_gate) return _tail; }

    private async Task RunAsync(string commit, IReadOnlyList<string>? prefix, IDisposable? lease)
    {
        ProjectFleetRollingRestartResult? result = null;
        string? failure = null;
        try
        {
            result = await runPass(listProjects(), commit, prefix, CancellationToken.None).ConfigureAwait(false);
            foreach (var project in result.Projects)
                Emit($"FLEET_ROLLING_RESTART_PROJECT commit={commit} project={Clean(project.Name)} " +
                    $"state={Clean(project.State)} build={Clean(project.BuildCommit ?? "unknown")} error={Clean(project.Error ?? "none")}");
            try { ProjectFleetStatusReport.WriteFile(statusPath, result.BuildRows()); }
            catch (Exception ex)
            { Emit($"FLEET_ROLLING_RESTART_STATUS_WRITE_FAILED commit={commit} reason={Clean(ex.Message)}"); }
        }
        catch (Exception ex)
        {
            failure = Clean(ex.Message);
            Emit($"FLEET_ROLLING_RESTART_FAILED commit={commit} reason={failure}");
        }
        finally
        {
            var projects = result?.Projects ?? [];
            Emit($"FLEET_ROLLING_RESTART commit={commit} restarted={projects.Count(p => p.State == "restarted")} " +
                $"failed={projects.Count(p => p.State.StartsWith("failed", StringComparison.Ordinal))} " +
                $"skipped={projects.Count(p => p.State.StartsWith("skipped", StringComparison.Ordinal))} " +
                $"total={projects.Count} error={failure ?? "none"}");
            // Keep the staged build alive while queued and while children launched from it run.
            // Observe child failures without waiting for their lifetime on the serial queue.
            var children = projects.Where(p => p.Child?.Completion is not null).Select(p => p.Child!.Completion!).ToArray();
            _ = Task.WhenAll(children).ContinueWith(completed =>
            {
                _ = completed.Exception;
                try { lease?.Dispose(); }
                catch (Exception ex) { Emit($"FLEET_ROLLING_RESTART_LEASE_RELEASE_FAILED commit={commit} reason={Clean(ex.Message)}"); }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private void Emit(string line)
    {
        // The event sink is best-effort, just like TryAppendConductEvent in activation.
        try { append(line); }
        catch (Exception) { }
    }

    private static string Clean(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();

    internal static FleetBuildRollingRestartTrigger CreateDefault(Action<string> append)
    {
        var registry = OrchestratorProjectRegistry.CreateDefault();
        var homeRoot = OrchestratorHome.ResolveForProcess().SourceRootDirectory
            ?? throw new InvalidOperationException("Fleet restart requires a home source root.");
        var homeWorkspace = OrchestratorWorkspace.ForDirectory(homeRoot, homeRoot, OrchestratorWorkspace.DefaultTenantName, registry);
        return new(registry.ListProjects, (projects, commit, prefix, token) =>
        {
            if (prefix is not { Count: >= 2 })
                throw new InvalidOperationException("Adopted build launch prefix is unavailable.");
            var supervisor = new ProjectFleetSupervisor(new AdoptedBuildHost(
                    new SystemConductorSupervisorProcessHost(), prefix), ProjectEnvironmentResolver.CreateSystem(),
                null, () => commit, homeRoot);
            return new ProjectFleetRollingRestart(supervisor, ReadOldCommit, TimeProvider.System,
                    homeRoot: homeRoot, liveGateProbe: FleetRestartAdmissionHold.HasLiveGate)
                .RunAsync(projects, commit, token);
        }, append, Path.Combine(homeWorkspace.OrchestratorDirectory, StatusFileName),
            acquireBuildLease: prefix => prefix is { Count: >= 2 }
                ? File.Open(prefix[1], FileMode.Open, FileAccess.Read, FileShare.Read)
                : throw new InvalidOperationException("Adopted build launch prefix is unavailable."));
    }

    private static string? ReadOldCommit(OrchestratorProject project)
    {
        var path = project.ResolveWorkspace().ConductEventsLogPath;
        var directory = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(directory)) return null;
        string? commit = null;
        foreach (var log in Directory.GetFiles(directory, "conduct-events-*.log")
                     .OrderBy(File.GetLastWriteTimeUtc).ThenBy(p => p, StringComparer.Ordinal).Append(path))
        {
            if (!File.Exists(log)) continue;
            using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                try
                {
                    using var json = JsonDocument.Parse(line);
                    if (!json.RootElement.TryGetProperty("detail", out var detail) || detail.ValueKind != JsonValueKind.String)
                        continue;
                    var fields = detail.GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (fields.FirstOrDefault() != "ACTIVATION_ADOPTED") continue;
                    var field = fields.FirstOrDefault(f => f.StartsWith("adoptedCommit=", StringComparison.Ordinal));
                    if (field is not null) commit = field["adoptedCommit=".Length..];
                }
                catch (JsonException) { } // Ignore a partial log record, never fabricate a build.
            }
        }
        return commit;
    }

    internal sealed class AdoptedBuildHost(IConductorSupervisorProcessHost inner, IReadOnlyList<string> prefix)
        : IConductorSupervisorProcessHost
    {
        public Task<ConductorSupervisorProcessResult> RunAsync(ConductorSupervisorProcessRequest request, CancellationToken token) =>
            inner.RunAsync(request with { CommandPrefix = prefix }, token);
    }
}
