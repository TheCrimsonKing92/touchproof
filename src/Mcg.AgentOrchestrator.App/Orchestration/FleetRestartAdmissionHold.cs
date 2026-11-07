using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// A project-local admission restriction owned by exactly one restart pass.
internal sealed record FleetRestartAdmissionHold(
    string Nonce, int OwnerPid, DateTimeOffset OwnerStartedAt, DateTimeOffset Deadline, string Commit)
{
    internal const string FileName = "fleet-restart-drain.json";
    internal static string MarkerPath(string directory) => Path.Combine(directory, FileName);
    private static string AckPath(string directory) => Path.Combine(directory, "fleet-restart-drain.ack.json");

    internal static FleetRestartAdmissionHold Acquire(string directory, string commit, DateTimeOffset deadline)
    {
        using var process = Process.GetCurrentProcess();
        var hold = new FleetRestartAdmissionHold(Guid.NewGuid().ToString("N"), process.Id,
            new DateTimeOffset(process.StartTime.ToUniversalTime()), deadline, commit);
        return Locked(directory, () =>
        {
            if (File.Exists(MarkerPath(directory)))
                throw new InvalidOperationException("A fleet restart admission hold already exists.");
            WriteAtomic(MarkerPath(directory), hold);
            return hold;
        });
    }

    internal void Release(string directory) => Locked(directory, () =>
    {
        if (Read(directory)?.Nonce == Nonce)
        {
            File.Delete(MarkerPath(directory));
            File.Delete(AckPath(directory));
        }
        return true;
    });

    // Invalid/unreadable markers fail closed. The caller reports the diagnostic and holds admission.
    internal static FleetRestartAdmissionHold? ReadForAdmission(
        string directory, DateTimeOffset now, Action<string> append, IConductLockPidProbe? probe = null) =>
        Locked(directory, () =>
        {
            var hold = Read(directory);
            if (hold is null) return null;
            var reason = now >= hold.Deadline ? "deadline-expired"
                : !(probe ?? new ConductLockPidProbe()).IsSameProcess(hold.OwnerPid, hold.OwnerStartedAt)
                    ? "owner-exited" : null;
            if (reason is null) return hold;
            File.Delete(MarkerPath(directory));
            File.Delete(AckPath(directory));
            append($"FLEET_RESTART_HOLD_ABANDONED hold={MarkerPath(directory)} reason={reason}");
            return null;
        });

    internal void Acknowledge(string directory, int activeAttempts) => Locked(directory, () =>
    {
        if (Read(directory)?.Nonce == Nonce)
            WriteAtomic(AckPath(directory), new Acknowledgement(Nonce, activeAttempts));
        return true;
    });

    internal bool IsDrainedAcknowledgement(string directory) => Locked(directory, () =>
        Read(directory)?.Nonce == Nonce && File.Exists(AckPath(directory)) &&
        JsonSerializer.Deserialize<Acknowledgement>(File.ReadAllText(AckPath(directory))) is
            { ActiveAttempts: 0 } ack && ack.Nonce == Nonce);

    private static FleetRestartAdmissionHold? Read(string directory)
    {
        if (!File.Exists(MarkerPath(directory))) return null;
        var hold = JsonSerializer.Deserialize<FleetRestartAdmissionHold>(File.ReadAllText(MarkerPath(directory)));
        if (hold is null || string.IsNullOrWhiteSpace(hold.Nonce) || hold.OwnerPid <= 0 ||
            hold.OwnerStartedAt == default || hold.Deadline == default)
            throw new InvalidDataException("Invalid fleet restart admission hold.");
        return hold;
    }

    // All marker mutations, including nonce checks and abandoned cleanup, share this short OS lock.
    // No process can remove a successor's marker between an ownership check and deletion.
    private static T Locked<T>(string directory, Func<T> action)
    {
        Directory.CreateDirectory(directory);
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)).ToUpperInvariant();
        using var mutex = new Mutex(false, "mcg-fleet-drain-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalized))));
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("Fleet restart admission hold lock unavailable.");
            return action();
        }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }

    private static void WriteAtomic<T>(string path, T value)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value));
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    internal static bool HasLiveGate(OrchestratorProject project) => HasLiveGate(
        project.ResolveWorkspace().OrchestratorDirectory, GateHeartbeatArtifacts.ReadStableSlots(), DateTimeOffset.UtcNow);

    internal static bool HasLiveGate(string directory, IReadOnlyList<GateHeartbeatStatus> statuses, DateTimeOffset now)
    {
        var owner = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        foreach (var status in statuses)
        {
            if (status.UnavailableReason == "invalid") return true;
            if (status.Snapshot is not { } snapshot) continue;
            if (!string.IsNullOrWhiteSpace(snapshot.OwnerStateDirectory) && !string.Equals(owner,
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(snapshot.OwnerStateDirectory)),
                    StringComparison.OrdinalIgnoreCase)) continue;
            if (GateLivenessVerdict.Classify(status, now).Liveness is GateLiveness.Running or GateLiveness.Stalled)
                return true;
        }
        return false;
    }

    private sealed record Acknowledgement(string Nonce, int ActiveAttempts);
}
