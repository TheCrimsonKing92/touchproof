using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record FleetCapacityError(string Code, string Message);
internal sealed record FleetCapacityProjectError(string Code, string Message, string Metric);
internal sealed record FleetCapacityProject(string Project, IReadOnlyList<string> Aliases, string Availability,
    long? PersistedRunningWorkers, int? ConfiguredProjectWorkerCap, IReadOnlyList<FleetCapacityProjectError> Errors);
internal sealed record FleetCapacityResult(DateTimeOffset ObservedAt, bool CatalogComplete,
    IReadOnlyList<FleetCapacityProject> Projects, long KnownPersistedRunningWorkers,
    long TheoreticalConfiguredProjectWorkerCapSum, int UnavailableProjectCount, bool Complete,
    IReadOnlyList<FleetCapacityError> Errors);

internal static class FleetWorkerCapacityReader
{
    internal static async Task<FleetCapacityResult> ReadAsync(AttentionWorkspaceCatalog catalog,
        DateTimeOffset observedAt, CancellationToken cancellationToken = default,
        Func<string, CancellationToken, Task<long>>? readOccupancy = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        readOccupancy ??= (path, token) => SqliteWorkerOccupancyReader.ReadAsync(path, token);
        var projects = new List<FleetCapacityProject>();
        var errors = new List<FleetCapacityError>();
        if (catalog.RegistryError is not null)
            errors.Add(new("registry-unavailable", "Registry inventory is incomplete; totals cover the known subset only."));
        long workers = 0, caps = 0;
        foreach (var group in catalog.CanonicalGroups())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = group[0];
            var failures = new List<FleetCapacityProjectError>();
            long? occupancy = null;
            int? cap = null;
            if (entry.Workspace is not { } workspace || entry.Error is not null)
                failures.Add(new("registry-entry-unavailable", "Project workspace could not be resolved.", "project"));
            else
            {
                try
                {
                    occupancy = await readOccupancy(workspace.SqliteStatePath, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (occupancy < 0) throw new InvalidDataException("Occupancy cannot be negative.");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    occupancy = null;
                    failures.Add(new(ex is WorkerOccupancyReadException typed ? typed.Code : "read-failed",
                        ex is WorkerOccupancyReadException known ? known.Message : "Recorded worker read failed.", "occupancy"));
                }
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    cap = ConductorAutonomyPolicy.LoadFromOrchestratorDirectory(
                        new DirectoryInfo(workspace.OrchestratorDirectory)).MaxConcurrentPaidWorkers;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failures.Add(new("invalid-policy", "Configured project worker policy could not be read or validated.", "policy"));
                }
            }
            if (occupancy is { } knownWorkers) workers = checked(workers + knownWorkers);
            if (cap is { } knownCap) caps = checked(caps + knownCap);
            projects.Add(new(entry.Name, AttentionWorkspaceCatalog.Aliases(group),
                failures.Count == 0 ? "available" : "unavailable", occupancy, cap, failures));
        }
        cancellationToken.ThrowIfCancellationRequested();
        var unavailable = projects.Count(project => project.Availability != "available");
        return new(observedAt.ToUniversalTime(), catalog.RegistryError is null, projects, workers, caps,
            unavailable, catalog.RegistryError is null && unavailable == 0, errors);
    }
}
