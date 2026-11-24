using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.App.Orchestration.PreTesterReverseDependencyObservation;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// The supervisor reserves a unique exit-artifact path for each launch. The direct child
// claims its associated grant once, while the parent still holds the supervisor lease.
internal sealed class ConductorResolverObservationLaunch(string path) : IDisposable
{
    private sealed record Grant(ConductorSupervisorProcessIdentity Supervisor, string AppPath, string Checkpoint);

    internal static ConductorResolverObservationLaunch Authorize(
        string exitArtifactPath, IConductorSupervisorHandoffSeam authority, string appPath, string checkpoint)
    {
        if (!authority.IsOwner(authority.Self))
            throw new InvalidOperationException("The continuity supervisor no longer owns the launch lease.");
        var path = exitArtifactPath + ".resolver-launch";
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(file, new Grant(authority.Self, Path.GetFullPath(appPath), checkpoint));
        return new(path);
    }

    internal static string? Claim(string stateDirectory, string? exitArtifactPath, string checkpoint)
    {
        var continuity = Path.GetFullPath(Path.Combine(stateDirectory, "continuity"));
        try
        {
            if (exitArtifactPath is null || !Path.IsPathFullyQualified(exitArtifactPath) ||
                !string.Equals(Path.GetDirectoryName(exitArtifactPath), continuity, StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileNameWithoutExtension(exitArtifactPath) is not { Length: 40 } name ||
                !name.StartsWith("conduct-", StringComparison.Ordinal) || !Guid.TryParseExact(name[8..], "N", out _) ||
                !Path.GetExtension(exitArtifactPath).Equals(".json", StringComparison.Ordinal))
                return null;
            var owner = SupervisedSelfRelaunchGuard.Probe(continuity, new ConductLockPidProbe()).Owner;
            if (owner is null || ProcessParentIdResolver.TryGetParentProcessId(Environment.ProcessId) != owner.ProcessId)
                return null;
            var path = exitArtifactPath + ".resolver-launch";
            var claimed = path + ".claimed";
            File.Move(path, claimed, overwrite: false);
            using var claim = new ConductorResolverObservationLaunch(claimed);
            using var input = File.OpenRead(claimed);
            var grant = JsonSerializer.Deserialize<Grant>(input);
            return grant is not null && grant.Supervisor == owner && grant.Checkpoint == checkpoint &&
                string.Equals(grant.AppPath, typeof(ConductorResolverObservationLaunch).Assembly.Location,
                    StringComparison.OrdinalIgnoreCase)
                ? checkpoint : null;
        }
        catch (Exception error) when (IsObservationFailure(error))
        {
            return null;
        }
    }

    public void Dispose()
    {
        try { File.Delete(path); }
        catch (Exception error) when (IsObservationFailure(error)) { }
    }
}
