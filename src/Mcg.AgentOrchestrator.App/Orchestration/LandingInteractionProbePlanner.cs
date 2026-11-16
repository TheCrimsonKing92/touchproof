using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record LandingInteractionProbeLanding(
    string LandingId,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<string> TestClasses);

internal sealed record LandingInteractionProbeCandidate(
    string GoalId,
    GoalStatus Status,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<string> DeferredTestClasses);

internal enum LandingInteractionProbeOutcomeKind
{
    Planned,
    Deferred
}

internal sealed record LandingInteractionProbeOutcome(
    string CandidateGoalId,
    LandingInteractionProbeOutcomeKind Kind,
    IReadOnlyList<string> TestClasses);

// Owns only the pairs planned during this instance's lifetime; callers supply canonical inputs.
internal sealed class LandingInteractionProbePlanner
{
    private readonly object _gate = new();
    private readonly HashSet<(string LandingId, string CandidateGoalId)> _planned = [];

    public IReadOnlyList<LandingInteractionProbeOutcome> Plan(
        LandingInteractionProbeLanding landing,
        IReadOnlyList<LandingInteractionProbeCandidate> candidates,
        bool gateWidthOccupied)
    {
        ArgumentNullException.ThrowIfNull(landing);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentException.ThrowIfNullOrWhiteSpace(landing.LandingId);
        ArgumentNullException.ThrowIfNull(landing.ChangedFiles);
        ArgumentNullException.ThrowIfNull(landing.TestClasses);

        // Validate the whole call before recording any pair.
        foreach (var candidate in candidates)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            ArgumentException.ThrowIfNullOrWhiteSpace(candidate.GoalId);
            ArgumentNullException.ThrowIfNull(candidate.ChangedFiles);
            ArgumentNullException.ThrowIfNull(candidate.DeferredTestClasses);
        }

        var landedFiles = new HashSet<string>(landing.ChangedFiles, StringComparer.Ordinal);
        var outcomes = new List<LandingInteractionProbeOutcome>();
        lock (_gate)
        {
            foreach (var candidate in candidates)
            {
                if (candidate.Status is not (GoalStatus.Verified or GoalStatus.Verifying)
                    || !candidate.ChangedFiles.Any(landedFiles.Contains))
                {
                    continue;
                }

                // Occupancy takes precedence even for a pair that was planned earlier.
                if (gateWidthOccupied)
                {
                    outcomes.Add(new(candidate.GoalId, LandingInteractionProbeOutcomeKind.Deferred, []));
                    continue;
                }

                var pair = (landing.LandingId, candidate.GoalId);
                if (_planned.Contains(pair))
                {
                    continue;
                }

                var classes = landing.TestClasses.Concat(candidate.DeferredTestClasses)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                outcomes.Add(new(candidate.GoalId, LandingInteractionProbeOutcomeKind.Planned, classes));
                _planned.Add(pair);
            }
        }

        return outcomes;
    }
}
