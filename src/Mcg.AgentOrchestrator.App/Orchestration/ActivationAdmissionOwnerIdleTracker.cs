using System.Diagnostics;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// One monitor owns these intervals. Failed censuses leave both identity and start time intact.
internal sealed class ActivationAdmissionOwnerIdleTracker(Func<TimeSpan>? elapsedNow = null)
{
    private readonly Func<TimeSpan> _elapsedNow = elapsedNow ?? (() => Stopwatch.GetElapsedTime(0));
    private readonly Dictionary<string, TimeSpan> _started = new(StringComparer.Ordinal);

    internal ActivationAdmissionHealthVerdict? Observe(ActivationAdmissionSample sample)
    {
        if (sample.CensusFailed) return null;
        if (!ActivationAdmissionPreparingOwnerToken.TryParse(sample.PreparingOwners, out var owners))
            throw new InvalidDataException("Malformed activation preparing owner identities.");
        var now = _elapsedNow();
        var current = owners.Select(owner => owner.Token).ToHashSet(StringComparer.Ordinal);
        foreach (var token in _started.Keys.Where(token => !current.Contains(token)).ToArray())
            _started.Remove(token);
        foreach (var token in current) _started.TryAdd(token, now);
        var overdue = _started.Select(entry => (Token: entry.Key, Elapsed: now - entry.Value))
            .Where(entry => entry.Elapsed > ActivationAdmissionHealthPolicy.OwnerIdleLimit)
            .OrderByDescending(entry => entry.Elapsed).ThenBy(entry => entry.Token, StringComparer.Ordinal)
            .FirstOrDefault();
        if (overdue.Token is null) return null;
        return new(ActivationAdmissionHealthState.Tripped, ConductorActivationRevertReason.AdmissionOwnerIdle,
            FormattableString.Invariant($"owner={overdue.Token} elapsedSeconds={overdue.Elapsed.TotalSeconds:0.###} limitSeconds={ActivationAdmissionHealthPolicy.OwnerIdleLimit.TotalSeconds:0.###} preparing={sample.PreparingAttempts} occupied={sample.Occupied} live={sample.LiveGates}"));
    }
}
