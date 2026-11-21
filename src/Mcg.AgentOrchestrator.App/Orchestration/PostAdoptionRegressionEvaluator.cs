using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class PostAdoptionRegressionEvaluator
{
    internal sealed record Window(int MaxTicks, TimeSpan MaxElapsed);

    internal sealed record GoalDecision(string GoalId, bool EligibleBeforeAdoption, PolicyDecisionRecord? Decision);

    internal sealed record Observation(
        string AdoptedLandingCommit,
        IReadOnlyList<GoalDecision> Baseline,
        IReadOnlyList<GoalDecision> CurrentTick,
        int TicksSinceAdoption,
        TimeSpan ElapsedSinceAdoption,
        bool GateRunning,
        bool GateSlotFree,
        int VerifiedGoalCount);

    internal sealed record AffectedGoal(string GoalId, PolicyDecisionRecord? Before, PolicyDecisionRecord After);

    internal sealed record Verdict(
        bool IsRegression,
        string Trigger,
        string AdoptedLandingCommit,
        string? SharedReason,
        IReadOnlyList<AffectedGoal> AffectedGoals,
        bool GateStarved,
        bool GateSlotFree,
        int VerifiedGoalCount,
        string? NegativeReason);

    private readonly Window _window;

    internal PostAdoptionRegressionEvaluator(Window? window = null)
    {
        _window = window ?? new Window(10, TimeSpan.FromMinutes(30));
    }

    internal Verdict Evaluate(Observation observation)
    {
        if (observation.TicksSinceAdoption > _window.MaxTicks ||
            observation.ElapsedSinceAdoption > _window.MaxElapsed)
        {
            return NoRegression(observation, "window-elapsed");
        }

        if (observation.GateRunning)
        {
            return NoRegression(observation, "gate-running");
        }

        var baseline = observation.Baseline
            .GroupBy(goal => goal.GoalId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        var previousBlockers = baseline.Values
            .Select(goal => HeldReason(goal.Decision))
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

        var newlyHeld = new List<AffectedGoal>();
        foreach (var goal in observation.CurrentTick.GroupBy(goal => goal.GoalId, StringComparer.Ordinal))
        {
            var after = goal.Last().Decision;
            if (after is not null && HeldReason(after) is { } reason && !previousBlockers.Contains(reason) &&
                baseline.TryGetValue(goal.Key, out var before) && before.EligibleBeforeAdoption)
            {
                newlyHeld.Add(new(goal.Key, before.Decision, after));
            }
        }

        var sharedBlocker = newlyHeld
            .GroupBy(goal => goal.After.Reason, StringComparer.Ordinal)
            .Where(group => group.Count() >= 2)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .FirstOrDefault();
        var gateStarved = observation.GateSlotFree && observation.VerifiedGoalCount > 0;
        var trigger = (sharedBlocker is not null, gateStarved) switch
        {
            (true, true) => "shared-blocker+gate-starvation",
            (true, false) => "shared-blocker",
            (false, true) => "gate-starvation",
            _ => null
        };

        return trigger is null
            ? NoRegression(observation, "no-regression-evidence")
            : new(true, trigger, observation.AdoptedLandingCommit, sharedBlocker?.Key,
                sharedBlocker?.ToArray() ?? [], gateStarved, observation.GateSlotFree,
                observation.VerifiedGoalCount, null);
    }

    private static Verdict NoRegression(Observation observation, string reason) =>
        new(false, "none", observation.AdoptedLandingCommit, null, [], false,
            observation.GateSlotFree, observation.VerifiedGoalCount, reason);

    private static string? HeldReason(PolicyDecisionRecord? decision) =>
        decision is not null && string.Equals(decision.Action, "Hold", StringComparison.Ordinal) &&
        !string.IsNullOrEmpty(decision.Reason)
            ? decision.Reason
            : null;
}
