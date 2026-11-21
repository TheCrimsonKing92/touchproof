using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Observational only: neither attempt coordinators nor writable receipt stores belong in a census.
internal static class ActivationAdmissionSlotOwnerSource
{
    internal static IReadOnlyList<ActivationAdmissionSlotOwner> Read() =>
        ActivationAdmissionAccumulator.Current.Value is null || GateHeartbeatOwner.Current is not { } directory
            ? [] : Read(directory);

    internal static IReadOnlyList<ActivationAdmissionSlotOwner> Read(string stateDirectory,
        Func<IEnumerable<ConductorGroupedGateAttempt>>? readAttempts = null,
        Func<ConductorGroupedGateAttempt, SpawnTrackedProcessStatus>? classifyOwner = null,
        Func<string, ConductorGroupedGateAttempt, ActivationAdmissionOwnerPendingStep>? readPendingStep = null)
    {
        var owners = new List<ActivationAdmissionSlotOwner>();
        try
        {
            foreach (var attempt in (readAttempts ?? (() => ReadAttempts(stateDirectory)))())
            {
                try
                {
                    if (attempt.Kind is not ("train" or "cohort") || attempt.Outcome != "Running" ||
                        attempt.ReconciledAt is not null || attempt.Members is null ||
                        attempt.Members.Any(member => member is null) || attempt.OwnerProcessId <= 0 ||
                        ConductorGroupedGateAttemptCoordinator.HasFinishedResult(attempt) ||
                        (classifyOwner ?? Classify)(attempt) != SpawnTrackedProcessStatus.LiveMatch) continue;
                    var identity = new ActivationAdmissionPreparingOwner(attempt.Kind, attempt.AttemptId);
                    if (!ActivationAdmissionPreparingOwnerToken.TryParse(identity.Token, out _)) continue;
                    var step = (readPendingStep ?? ReadPendingStep)(stateDirectory, attempt);
                    if (step == ActivationAdmissionOwnerPendingStep.None) continue;
                    owners.Add(new(identity, attempt.Members.Select(member => member.GoalId).ToArray(),
                        attempt.OwnerProcessId, [attempt.AttemptId, attempt.IdentityValue], step));
                }
                catch (Exception ex) when (Unreadable(ex)) { }
            }
        }
        catch (Exception ex) when (Unreadable(ex)) { }
        return owners.DistinctBy(owner => owner.Identity).ToArray();
    }

    private static SpawnTrackedProcessStatus Classify(ConductorGroupedGateAttempt attempt) =>
        AcceptanceHolderIdentity.Classify(attempt.OwnerProcessId,
            attempt.OwnerProcessStartedAt, attempt.OwnerExecutablePath);

    private static IEnumerable<ConductorGroupedGateAttempt> ReadAttempts(string stateDirectory)
    {
        var root = Path.Combine(stateDirectory, "grouped-gate-attempts");
        if (!Directory.Exists(root)) yield break;
        foreach (var path in Directory.EnumerateFiles(root, "*.attempt.json", SearchOption.AllDirectories))
        {
            ConductorGroupedGateAttempt? attempt = null;
            try { attempt = ConductorGroupedGateAttemptCoordinator.Read(path); }
            catch (Exception ex) when (Unreadable(ex)) { }
            if (attempt is not null) yield return attempt;
        }
    }

    internal static ActivationAdmissionOwnerPendingStep ReadPendingStep(string directory,
        ConductorGroupedGateAttempt attempt) => ActivationAdmissionGroupedPendingStep.Read(directory, attempt);

    private static bool Unreadable(Exception ex) => ex is IOException or UnauthorizedAccessException or
        JsonException or SqliteException or InvalidOperationException or ArgumentException or NotSupportedException;
}
