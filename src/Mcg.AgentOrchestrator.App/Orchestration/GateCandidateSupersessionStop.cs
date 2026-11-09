using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owns observation diagnostics only; coordinators retain ownership of all attempt transitions.
internal sealed class GateCandidateSupersessionStop(
    ConductorParallelAcceptanceAttemptCoordinator solo,
    ConductorGroupedGateAttemptCoordinator grouped,
    ConductEventLogWriter events,
    Func<DateTimeOffset>? utcNow = null)
{
    private readonly HashSet<string> _unreadableReported = new(StringComparer.Ordinal);

    internal sealed record Result(IReadOnlyList<string> Progress, IReadOnlySet<GoalId> ChangedGoalIds);

    internal Result Apply(AgentOrchestratorKernel kernel, GateCandidateHeadObservations heads)
    {
        List<string> progress = [];
        HashSet<GoalId> changed = [];
        var attempts = solo.GetAllCapacityReservingAttempts().Where(a => a.Kind == "gate" &&
                a.Outcome == ConductorParallelAcceptanceAttemptOutcome.Running && a.ReconciledAt is null)
            .Select(a => (a.AttemptId, Solo: (ConductorParallelAcceptanceAttempt?)a, Grouped: (ConductorGroupedGateAttempt?)null))
            .Concat(grouped.ReadAll().Where(a => a.Kind is "cohort" or "train" &&
                    a.Outcome == "Running" && a.ReconciledAt is null && grouped.ReservesCapacity(a))
                .Select(a => (a.AttemptId, Solo: (ConductorParallelAcceptanceAttempt?)null, Grouped: (ConductorGroupedGateAttempt?)a)))
            .OrderBy(a => a.AttemptId, StringComparer.Ordinal).ToArray();
        _unreadableReported.IntersectWith(attempts.Select(a => a.AttemptId));

        foreach (var attempt in attempts)
        {
            (string GoalId, string? Revision)[] members = attempt.Grouped is { } group
                ? group.Members.Select(m => (m.GoalId, Revision: (string?)m.BranchRevision)).ToArray()
                : [(attempt.Solo!.GoalId, Revision: attempt.Solo.BranchHeadSha)];
            if (members.Length == 0 || members.Any(m => !ConductorGitRevisionReader.IsValid(m.Revision))) continue;
            var kind = attempt.Grouped?.Kind ?? "solo";
            var observed = members.Select(m => (m.GoalId, Gated: m.Revision!.Trim().ToLowerInvariant(),
                Head: heads.Observe(new GoalId(m.GoalId)))).ToArray();
            var unreadable = observed.FirstOrDefault(m => m.Head is null);
            if (unreadable.GoalId is not null)
            {
                if (_unreadableReported.Add(attempt.AttemptId))
                    progress.Add($"GATE_SUPERSESSION_HEAD_UNREADABLE attempt={attempt.AttemptId} kind={kind} member={unreadable.GoalId}");
                continue;
            }
            var moved = observed.FirstOrDefault(m => m.Gated != m.Head);
            if (moved.GoalId is null) continue;

            try
            {
                var stopped = attempt.Solo is { } single
                    ? solo.CancelForOperator(single, "candidate-superseded") == OperatorGateStopResult.Stopped
                    : grouped.CancelForSupersededCandidate(attempt.Grouped!, moved.GoalId, moved.Gated, moved.Head!);
                if (!stopped) continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                progress.Add($"CANDIDATE_SUPERSESSION_CHECK_FAILED attempt={attempt.AttemptId} error={ConductorDiagnosticText.BoundSingleLine(ex.Message)}");
                continue;
            }

            var detail = $"GATE_CANCELLED attempt={attempt.AttemptId} kind={kind} members={string.Join('+', members.Select(m => m.GoalId))} " +
                $"reason=candidate-superseded member={moved.GoalId} gated={moved.Gated} head={moved.Head}";
            try
            {
                if (!events.AppendRequired("gate-cancel", moved.GoalId, detail, utcNow?.Invoke() ?? DateTimeOffset.UtcNow))
                    progress.Add($"CANDIDATE_SUPERSEDED_EVENT_APPEND_FAILED attempt={attempt.AttemptId}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                progress.Add($"CANDIDATE_SUPERSEDED_EVENT_APPEND_FAILED attempt={attempt.AttemptId} error={ConductorDiagnosticText.BoundSingleLine(ex.Message)}");
            }
            progress.Add(detail);
            if (attempt.Solo is not null && kernel.Goals.FirstOrDefault(g => g.Id.Value == moved.GoalId) is { } goal &&
                OperatorGateCancellation.TryReturnSoloGoalToVerified(kernel, goal,
                    $"Candidate superseded acceptance attempt {attempt.AttemptId}; goal returned to Verified without a verdict."))
                changed.Add(goal.Id);
        }
        return new(progress, changed);
    }
}
