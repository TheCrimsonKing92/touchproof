using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// A differential comparison implicates a candidate, including interactions with its peers.
// It does not establish that the candidate fails by itself.
internal static class MergeTrainSurvivorDifferentialFailure
{
    internal static void OnSurvivorsPassed(
        MergeTrainReceipt red, MergeTrainReceipt survivors, MergeTrainMemberBinding dropped,
        IReadOnlyList<MergeTrainEjection> ejections, Goal goal,
        Func<Goal, GateReadyCandidateProjectionResult> project,
        Action<Goal, IReadOnlyList<string>, string?, string?, IReadOnlyList<AcceptanceCheckAttribution>?, string?> recordFailure,
        Func<GoalId, string, bool> routeFailure, Action recordImplication)
    {
        recordImplication();
        Route(red, survivors, dropped, ejections, goal, project, recordFailure, routeFailure);
    }

    internal static void Route(
        MergeTrainReceipt red, MergeTrainReceipt survivors, MergeTrainMemberBinding dropped,
        IReadOnlyList<MergeTrainEjection> ejections, Goal goal,
        Func<Goal, GateReadyCandidateProjectionResult> project,
        Action<Goal, IReadOnlyList<string>, string?, string?, IReadOnlyList<AcceptanceCheckAttribution>?, string?> recordFailure,
        Func<GoalId, string, bool> routeFailure) =>
        Prepare(red, survivors, dropped, ejections, goal, project, recordFailure, routeFailure)?.Invoke();

    internal static Action? PrepareReconciliation(
        MergeTrainAcceptanceStore store, MergeTrainReceipt red, MergeTrainMemberBinding dropped,
        string detail, Goal goal, Func<Goal, GateReadyCandidateProjectionResult> project,
        Action<Goal, IReadOnlyList<string>, string?, string?, IReadOnlyList<AcceptanceCheckAttribution>?, string?> recordFailure,
        Func<GoalId, string, bool> routeFailure)
    {
        var peer = red.Identity.Members.FirstOrDefault(member => member.GoalId != dropped.GoalId);
        if (peer is null) return null;
        var survivors = store.ReadPassedReceiptsForGoal(peer.GoalId)
            .FirstOrDefault(receipt => MatchesSurvivors(red, receipt, dropped));
        return survivors is null ? null : Prepare(red, survivors, dropped,
            [new(dropped.GoalId, MergeTrainEjectionReason.RedNewestMember, [], detail)],
            goal, project, recordFailure, routeFailure);
    }

    private static Action? Prepare(
        MergeTrainReceipt red, MergeTrainReceipt survivors, MergeTrainMemberBinding dropped,
        IReadOnlyList<MergeTrainEjection> ejections, Goal goal,
        Func<Goal, GateReadyCandidateProjectionResult> project,
        Action<Goal, IReadOnlyList<string>, string?, string?, IReadOnlyList<AcceptanceCheckAttribution>?, string?> recordFailure,
        Func<GoalId, string, bool> routeFailure)
    {
        if (goal.Id != dropped.GoalId || goal.Status != GoalStatus.Verified ||
            MergeTrainAttributedFailureReconciliation.WasApplied(goal, red.ReceiptId) ||
            !ejections.Any(ejection => ejection.GoalId == dropped.GoalId &&
                ejection.Reason == MergeTrainEjectionReason.RedNewestMember &&
                ejection.Detail == $"Dropped after RED receipt {red.ReceiptId}.") ||
            !MergeTrainRedAttribution.IsGenuineTrainRed(red) || !MatchesSurvivors(red, survivors, dropped) ||
            project(goal) is not GateReadyCandidateProjectionResult.Ready ready ||
            ready.Projection.CandidateRevision != dropped.CandidateRevision) return null;

        var failures = MergeTrainRedAttribution.ReadFatalFailures(red.GateTestResultPaths);
        if (failures.Count == 0 || failures.Any(failure => string.IsNullOrWhiteSpace(failure.TestName))) return null;
        var classes = failures.Select(failure => AcceptanceTestSourceResolver.ExtractClassName(failure.TestName!) ?? failure.TestName!)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var tests = failures.Select(failure => failure.TestName!).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        // The existing route header is comma-separated. Keep theory identities intact in
        // the details and use their classes in the header when they cannot be represented.
        var header = tests.All(AttributedCohortFailureDetail.IsRepresentableTestName) ? tests : classes;
        var reason = string.Join(Environment.NewLine,
            new[]
            {
                $"merge train RED attributed (survivors-passed) receipt {red.ReceiptId}: {string.Join(", ", header)}",
                $"survivors_passed receipt={survivors.ReceiptId}",
                $"These tests failed in {red.ReceiptId} with this member present and did not fail in {survivors.ReceiptId}, where only this member was absent. This comparison includes possible interactions with the other members."
            }.Concat(red.GateTestResultPaths.Select(path => $"red_trx={path}"))
            .Concat(failures.Select(failure => $"failed {failure.TestName}: {MergeTrainAttributedFailure.BoundMessage(failure.Message)}")));
        return () => MergeTrainAttributedFailure.RecordAndRoute(red, dropped, goal, classes, reason, recordFailure, routeFailure);
    }

    private static bool MatchesSurvivors(MergeTrainReceipt red, MergeTrainReceipt survivors, MergeTrainMemberBinding dropped)
    {
        if (survivors.Outcome != MergeTrainGateOutcome.Passed || !survivors.ValidForLanding || survivors.Recheck is not null ||
            survivors.Identity.ObservedMainRevision != red.Identity.ObservedMainRevision ||
            survivors.CompletedAt < red.CompletedAt ||
            red.Identity.Members.Count(member => SameCandidate(member, dropped)) != 1) return false;
        var remaining = red.Identity.Members.Where(member => member.GoalId != dropped.GoalId).ToArray();
        return remaining.Length == survivors.Identity.Members.Count &&
            remaining.Zip(survivors.Identity.Members).All(pair => SameCandidate(pair.First, pair.Second));
    }

    private static bool SameCandidate(MergeTrainMemberBinding first, MergeTrainMemberBinding second) =>
        first.GoalId == second.GoalId && first.CandidateRevision == second.CandidateRevision &&
        first.BranchRevision == second.BranchRevision && first.BriefVersion == second.BriefVersion &&
        first.LandingPaths.SequenceEqual(second.LandingPaths, StringComparer.Ordinal);
}
