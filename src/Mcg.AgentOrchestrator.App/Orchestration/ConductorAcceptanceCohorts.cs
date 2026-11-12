using System.Collections.Concurrent;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum ConductorAcceptanceCohortPairExclusionReason
{
    UpstreamExcluded,
    DuplicateGoal,
    IncompleteLandingScope,
    DocsTreeOnlyCandidate,
    IncompleteResourceKeys,
    MainRevisionMismatch,
    LandingPathOverlap,
    SerializedResourceOverlap,
    SuppressedInteraction,
    AttributedMember,
    TrainImplicatedMember,
    OwnerProtectedMember
}

internal sealed record ConductorAcceptanceCohortPairExclusion(
    GoalId FirstGoalId,
    GoalId SecondGoalId,
    ConductorAcceptanceCohortPairExclusionReason Reason,
    string? Evidence = null);

internal sealed record ConductorAcceptanceCohortSelection(
    IReadOnlyList<GateReadyCandidateProjection> Members,
    IReadOnlyList<ConductorAcceptanceCohortPairExclusion> Exclusions)
{
    internal IReadOnlyList<AcceptanceCohortMemberBinding> BindMembers(Func<GoalId, int?>? briefVersion = null) =>
        Members.Select(member => new AcceptanceCohortMemberBinding(
            member.GoalId,
            member.BranchRevision,
            member.CandidateRevision,
            member.LandingPaths,
            member.ResourceKeys,
            member.ChangeRiskTier,
            member.AutoPromotionDisposition,
            member.MergeEvidence.Status.ToString(),
            member.MergeEvidence.Reason.ToString(), briefVersion?.Invoke(member.GoalId))).ToArray();
}

internal enum ConductorAcceptanceHeldConflictKind
{
    LandingPath,
    SerializedResource,
    UnknownResourceIdentity
}

internal enum ConductorAcceptanceCohortFairnessOutcome
{
    YieldedToRunnableYounger,
    NoRunnableYounger
}

internal sealed record ConductorAcceptanceHeldResource(
    string HolderGoalId,
    IReadOnlyList<string> LandingPaths,
    IReadOnlyList<string> ResourceKeys);

internal sealed record ConductorAcceptanceHeldConflict(
    GoalId CandidateGoalId,
    string HolderGoalId,
    ConductorAcceptanceHeldConflictKind Kind,
    string ConflictKey);

internal sealed record ConductorAcceptanceCohortFairnessContext(
    GoalId BlockedHeadGoalId,
    int OvertakeCount,
    IReadOnlyList<ConductorAcceptanceHeldResource> HeldResources);

internal sealed record ConductorAcceptanceCohortFairnessPriority(
    GoalId GoalId,
    int OvertakeCount);

internal sealed record ConductorAcceptanceCohortFairnessDecision(
    ConductorAcceptanceCohortFairnessOutcome Outcome,
    GoalId BlockedHeadGoalId,
    GoalId? SelectedGoalId,
    ConductorAcceptanceHeldConflict HeadConflict,
    int OvertakeCount,
    IReadOnlyList<ConductorAcceptanceHeldConflict> CandidateConflicts);

internal sealed record ConductorAcceptanceCohortSelectionResult(
    ConductorAcceptanceCohortSelection? Selection,
    IReadOnlyList<ConductorAcceptanceCohortPairExclusion> Exclusions,
    ConductorAcceptanceCohortFairnessDecision? FairnessDecision = null);

internal sealed record ConductorAcceptanceCohortRunResult(
    AcceptanceCohortReceipt? Receipt,
    IReadOnlyDictionary<string, ConductorAdvanceResult> MemberResults,
    string Detail);

// A background acceptance cohort gate that ended in an exception, carried as data so the conduct tick
// can classify it and hold or escalate the members instead of receiving a throw across the tick boundary.
internal sealed record ConductorAcceptanceCohortGateFault(
    string MemberPairKey,
    IReadOnlySet<string> MemberGoalIds,
    string PairFingerprint,
    Exception Fault)
{
    internal string FaultType => Fault.GetType().Name;

    internal string Message => BoundFaultMessage(Fault.Message);

    private static string BoundFaultMessage(string value)
    {
        var singleLine = value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        return singleLine.Length <= 256 ? singleLine : singleLine[..256];
    }
}

internal static class ConductorAcceptanceCohortSelector
{
    internal const int CohortSize = 2;

    internal static ConductorAcceptanceCohortSelectionResult Select(
        IReadOnlyList<ConductorSpeculativeAcceptanceCandidate> orderedCandidates,
        GoalId? forcedCandidate = null,
        IReadOnlySet<string>? suppressedPairFingerprints = null,
        ConductorAcceptanceCohortFairnessContext? fairnessContext = null,
        IReadOnlySet<string>? attributedMemberKeys = null,
        IReadOnlySet<string>? trainImplicatedMemberKeys = null)
    {
        ArgumentNullException.ThrowIfNull(orderedCandidates);
        var ready = new List<GateReadyCandidateProjection>(orderedCandidates.Count);
        var exclusions = new List<ConductorAcceptanceCohortPairExclusion>();
        foreach (var candidate in orderedCandidates)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            if (candidate.ProjectionResult is not GateReadyCandidateProjectionResult.Ready projected)
            {
                var evidence = candidate.ProjectionResult is GateReadyCandidateProjectionResult.Excluded excluded
                    ? excluded.Reason.ToString()
                    : candidate.ProjectionResult.GetType().Name;
                exclusions.Add(new ConductorAcceptanceCohortPairExclusion(
                    candidate.GoalId,
                    candidate.GoalId,
                    ConductorAcceptanceCohortPairExclusionReason.UpstreamExcluded,
                    evidence));
                continue;
            }
            if (projected.Projection.GoalId != candidate.GoalId)
            {
                throw new InvalidOperationException(
                    $"Cohort candidate {candidate.GoalId.Value} does not match its Ready projection {projected.Projection.GoalId.Value}.");
            }
            if (attributedMemberKeys?.Contains(ConductorAcceptanceCohortAttributedMembers.Key(
                    candidate.GoalId, projected.Projection.CandidateRevision)) == true)
            {
                exclusions.Add(new ConductorAcceptanceCohortPairExclusion(
                    candidate.GoalId, candidate.GoalId,
                    ConductorAcceptanceCohortPairExclusionReason.AttributedMember,
                    $"candidate={projected.Projection.CandidateRevision}"));
                continue;
            }
            if (trainImplicatedMemberKeys?.Contains(ConductorAcceptanceCohortAttributedMembers.Key(
                    candidate.GoalId, projected.Projection.CandidateRevision)) == true)
            {
                exclusions.Add(new ConductorAcceptanceCohortPairExclusion(
                    candidate.GoalId, candidate.GoalId,
                    ConductorAcceptanceCohortPairExclusionReason.TrainImplicatedMember,
                    $"candidate={projected.Projection.CandidateRevision}"));
                continue;
            }
            if (OwnerProtectedLandingPaths.Contains(projected.Projection.LandingPaths))
            {
                exclusions.Add(new ConductorAcceptanceCohortPairExclusion(
                    candidate.GoalId, candidate.GoalId,
                    ConductorAcceptanceCohortPairExclusionReason.OwnerProtectedMember,
                    $"goal={candidate.GoalId.Value}"));
                continue;
            }
            ready.Add(projected.Projection);
        }

        if (fairnessContext is not null)
        {
            if (forcedCandidate is null || forcedCandidate != fairnessContext.BlockedHeadGoalId)
            {
                throw new InvalidOperationException(
                    "A cohort fairness context must describe the exact forced candidate.");
            }

            var blockedHeadIndex = ready.FindIndex(candidate => candidate.GoalId == forcedCandidate);
            if (blockedHeadIndex >= 0 &&
                FindHeldConflict(ready[blockedHeadIndex], fairnessContext.HeldResources) is { } headConflict)
            {
                var candidateConflicts = new List<ConductorAcceptanceHeldConflict>();
                for (var candidateIndex = blockedHeadIndex + 1; candidateIndex < ready.Count; candidateIndex++)
                {
                    var candidate = ready[candidateIndex];
                    if (FindHeldConflict(candidate, fairnessContext.HeldResources) is { } candidateConflict)
                    {
                        candidateConflicts.Add(candidateConflict);
                        continue;
                    }

                    for (var peerIndex = candidateIndex + 1; peerIndex < ready.Count; peerIndex++)
                    {
                        var peer = ready[peerIndex];
                        if (FindHeldConflict(peer, fairnessContext.HeldResources) is { } peerConflict)
                        {
                            candidateConflicts.Add(peerConflict);
                            continue;
                        }

                        var pairExclusion = FindExclusion(candidate, peer, suppressedPairFingerprints);
                        if (pairExclusion is not null)
                        {
                            exclusions.Add(pairExclusion);
                            continue;
                        }

                        var frozenExclusions = Array.AsReadOnly(exclusions.ToArray());
                        return new ConductorAcceptanceCohortSelectionResult(
                            new ConductorAcceptanceCohortSelection(
                                Array.AsReadOnly([candidate, peer]),
                                frozenExclusions),
                            frozenExclusions,
                            new ConductorAcceptanceCohortFairnessDecision(
                                ConductorAcceptanceCohortFairnessOutcome.YieldedToRunnableYounger,
                                fairnessContext.BlockedHeadGoalId,
                                candidate.GoalId,
                                headConflict,
                                fairnessContext.OvertakeCount,
                                Array.AsReadOnly(candidateConflicts.ToArray())));
                    }
                }

                return new ConductorAcceptanceCohortSelectionResult(
                    Selection: null,
                    Array.AsReadOnly(exclusions.ToArray()),
                    new ConductorAcceptanceCohortFairnessDecision(
                        ConductorAcceptanceCohortFairnessOutcome.NoRunnableYounger,
                        fairnessContext.BlockedHeadGoalId,
                        SelectedGoalId: null,
                        headConflict,
                        fairnessContext.OvertakeCount,
                        Array.AsReadOnly(candidateConflicts.ToArray())));
            }
        }

        var forcedCandidateIsReady = false;
        if (forcedCandidate is not null)
        {
            var forcedIndex = ready.FindIndex(candidate => candidate.GoalId == forcedCandidate);
            if (forcedIndex < 0)
            {
                return new ConductorAcceptanceCohortSelectionResult(
                    Selection: null,
                    Array.AsReadOnly((exclusions.Count > 0
                        ? exclusions
                        : [new ConductorAcceptanceCohortPairExclusion(
                            forcedCandidate,
                            forcedCandidate,
                            ConductorAcceptanceCohortPairExclusionReason.UpstreamExcluded,
                            "forced candidate has no current Ready projection")]).ToArray()));
            }
            else
            {
                var forced = ready[forcedIndex];
                ready.RemoveAt(forcedIndex);
                ready.Insert(0, forced);
                forcedCandidateIsReady = true;
            }
        }

        var firstCandidateLimit = forcedCandidateIsReady ? Math.Min(1, ready.Count - 1) : ready.Count - 1;
        for (var firstIndex = 0; firstIndex < firstCandidateLimit; firstIndex++)
        {
            for (var secondIndex = firstIndex + 1; secondIndex < ready.Count; secondIndex++)
            {
                var first = ready[firstIndex];
                var second = ready[secondIndex];
                var exclusion = FindExclusion(first, second, suppressedPairFingerprints);
                if (exclusion is null)
                {
                    var frozenExclusions = Array.AsReadOnly(exclusions.ToArray());
                    return new ConductorAcceptanceCohortSelectionResult(
                        new ConductorAcceptanceCohortSelection(
                            Array.AsReadOnly([first, second]),
                            frozenExclusions),
                        frozenExclusions);
                }
                exclusions.Add(exclusion);
            }
        }

        return new ConductorAcceptanceCohortSelectionResult(
            Selection: null,
            Array.AsReadOnly(exclusions.ToArray()));
    }

    private static ConductorAcceptanceHeldConflict? FindHeldConflict(
        GateReadyCandidateProjection candidate,
        IReadOnlyList<ConductorAcceptanceHeldResource> heldResources)
    {
        foreach (var holder in heldResources
                     .OrderBy(holder => holder.HolderGoalId, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(holder.HolderGoalId) ||
                holder.ResourceKeys is null ||
                holder.LandingPaths is null ||
                (holder.ResourceKeys.Count == 0 && holder.LandingPaths.Count == 0) ||
                holder.ResourceKeys.Contains(
                    RepositoryLandingScopeNormalization.UnknownAcceptanceScopeResourceKey,
                    StringComparer.OrdinalIgnoreCase))
            {
                return new ConductorAcceptanceHeldConflict(
                    candidate.GoalId,
                    string.IsNullOrWhiteSpace(holder.HolderGoalId) ? "unknown" : holder.HolderGoalId,
                    ConductorAcceptanceHeldConflictKind.UnknownResourceIdentity,
                    RepositoryLandingScopeNormalization.UnknownAcceptanceScopeResourceKey);
            }

            var sharedResource = candidate.ResourceKeys
                .Intersect(holder.ResourceKeys, StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (sharedResource is not null)
            {
                return new ConductorAcceptanceHeldConflict(
                    candidate.GoalId,
                    holder.HolderGoalId,
                    ConductorAcceptanceHeldConflictKind.SerializedResource,
                    sharedResource);
            }

            foreach (var candidatePath in candidate.LandingPaths.Order(StringComparer.OrdinalIgnoreCase))
            {
                foreach (var heldPath in holder.LandingPaths.Order(StringComparer.OrdinalIgnoreCase))
                {
                    if (RepositoryPathOverlap.Overlaps(candidatePath, heldPath))
                    {
                        return new ConductorAcceptanceHeldConflict(
                            candidate.GoalId,
                            holder.HolderGoalId,
                            ConductorAcceptanceHeldConflictKind.LandingPath,
                            $"path:{candidatePath}:{heldPath}");
                    }
                }
            }
        }

        return null;
    }

    internal static string PairFingerprint(GateReadyCandidateProjection first, GateReadyCandidateProjection second) =>
        $"{first.GoalId.Value}:{first.CandidateRevision}:{second.GoalId.Value}:{second.CandidateRevision}:{first.MainRevision}";

    private static ConductorAcceptanceCohortPairExclusion? FindExclusion(
        GateReadyCandidateProjection first,
        GateReadyCandidateProjection second,
        IReadOnlySet<string>? suppressedPairFingerprints)
    {
        if (first.GoalId == second.GoalId)
        {
            return Excluded(first, second, ConductorAcceptanceCohortPairExclusionReason.DuplicateGoal);
        }
        if (first.LandingPaths.Count == 0 || second.LandingPaths.Count == 0)
        {
            return Excluded(first, second, ConductorAcceptanceCohortPairExclusionReason.IncompleteLandingScope);
        }
        var firstDisposition = GoalAcceptanceVerifier.ClassifyDotnetShardDisposition(first.LandingPaths);
        var secondDisposition = GoalAcceptanceVerifier.ClassifyDotnetShardDisposition(second.LandingPaths);
        if (firstDisposition == DotnetShardDisposition.DocsTreeOnlyCandidate ||
            secondDisposition == DotnetShardDisposition.DocsTreeOnlyCandidate)
        {
            var docsGoalId = firstDisposition == DotnetShardDisposition.DocsTreeOnlyCandidate
                ? first.GoalId
                : second.GoalId;
            return Excluded(
                first,
                second,
                ConductorAcceptanceCohortPairExclusionReason.DocsTreeOnlyCandidate,
                $"goal={docsGoalId.Value}; disposition=docs-tree-only-candidate");
        }
        if (first.ResourceKeys.Count == 0 || second.ResourceKeys.Count == 0)
        {
            return Excluded(first, second, ConductorAcceptanceCohortPairExclusionReason.IncompleteResourceKeys);
        }
        if (!first.MainRevision.Equals(second.MainRevision, StringComparison.Ordinal))
        {
            return Excluded(
                first,
                second,
                ConductorAcceptanceCohortPairExclusionReason.MainRevisionMismatch,
                $"{first.MainRevision}:{second.MainRevision}");
        }
        foreach (var firstPath in first.LandingPaths)
        {
            foreach (var secondPath in second.LandingPaths)
            {
                if (RepositoryPathOverlap.Overlaps(firstPath, secondPath))
                {
                    return Excluded(
                        first,
                        second,
                        ConductorAcceptanceCohortPairExclusionReason.LandingPathOverlap,
                        $"{firstPath}:{secondPath}");
                }
            }
        }
        var sharedResource = first.ResourceKeys.FirstOrDefault(resource =>
            second.ResourceKeys.Contains(resource, StringComparer.OrdinalIgnoreCase));
        if (sharedResource is not null)
        {
            return Excluded(
                first,
                second,
                ConductorAcceptanceCohortPairExclusionReason.SerializedResourceOverlap,
                sharedResource);
        }
        if (suppressedPairFingerprints?.Contains(PairFingerprint(first, second)) == true)
        {
            return Excluded(first, second, ConductorAcceptanceCohortPairExclusionReason.SuppressedInteraction);
        }
        return null;
    }

    private static ConductorAcceptanceCohortPairExclusion Excluded(
        GateReadyCandidateProjection first,
        GateReadyCandidateProjection second,
        ConductorAcceptanceCohortPairExclusionReason reason,
        string? evidence = null) => new(first.GoalId, second.GoalId, reason, evidence);
}

internal static class ConductorAcceptanceCohortAttribution
{
    internal static AcceptanceCohortAttributionOutcome Classify(
        AcceptanceCohortGateOutcome first,
        AcceptanceCohortGateOutcome second)
    {
        if (first is AcceptanceCohortGateOutcome.InfrastructureFailure or AcceptanceCohortGateOutcome.Invalidated ||
            second is AcceptanceCohortGateOutcome.InfrastructureFailure or AcceptanceCohortGateOutcome.Invalidated)
        {
            return AcceptanceCohortAttributionOutcome.Indeterminate;
        }
        if (first == AcceptanceCohortGateOutcome.Failed && second == AcceptanceCohortGateOutcome.Passed)
        {
            return AcceptanceCohortAttributionOutcome.FirstMemberFailed;
        }
        if (first == AcceptanceCohortGateOutcome.Passed && second == AcceptanceCohortGateOutcome.Failed)
        {
            return AcceptanceCohortAttributionOutcome.SecondMemberFailed;
        }
        if (first == AcceptanceCohortGateOutcome.Failed && second == AcceptanceCohortGateOutcome.Failed)
        {
            return AcceptanceCohortAttributionOutcome.BothMembersFailed;
        }
        if (first == AcceptanceCohortGateOutcome.Passed && second == AcceptanceCohortGateOutcome.Passed)
        {
            return AcceptanceCohortAttributionOutcome.InteractionOnly;
        }
        return AcceptanceCohortAttributionOutcome.Indeterminate;
    }
}
internal sealed partial class ConductorDriver
{
    internal IReadOnlyList<ConductorAcceptanceCohortSelection> FindLandablePassedCohortSelections(
        IReadOnlyList<ConductorSpeculativeAcceptanceCandidate> candidates) =>
        PassedCohortReceiptReplay.FindLandableSelections(
            _cohortAcceptanceStore, candidates, ReadSuppressedCohortPairs, HasCurrentPassedCohortIdentity);

    internal AcceptanceCohortReceipt? ReadCurrentPassedCohortReceipt(ConductorAcceptanceCohortSelection selection) =>
        _cohortAcceptanceStore?.ReadPassedReceiptsForGoal(selection.Members[0].GoalId)
            .FirstOrDefault(receipt => HasCurrentPassedCohortIdentity(selection, receipt));

    internal bool HasFinalizedCohortLanding(GoalId goalId) =>
        _cohortAcceptanceStore?.ReadFinalizedLandingsForGoal(goalId).Count > 0;

    private bool HasCurrentPassedCohortIdentity(
        ConductorAcceptanceCohortSelection selection,
        AcceptanceCohortReceipt receipt) =>
        GroupedGateIdentityInspector.HasCurrentPassedCohortIdentity(selection, receipt,
            _cohortKernel, _cohortWorkspace, _cohortAcceptanceVerifier, _integrationBranch, _cohortCleanupHooks);

    // A background cohort gate that faulted is parked here as a typed fault instead of being rethrown
    // into the conduct tick, and drained by the next RunAcceptanceCohort call for the same member pair.
    private readonly ConcurrentDictionary<string, ConductorAcceptanceCohortGateFault> _cohortGateFaults =
        new(StringComparer.Ordinal);
    // Transient cohort-gate faults counted per member pair. The pair fingerprint moves with the main
    // revision, so counting by fingerprint would reset before the cap and loop forever; the member pair
    // is the stable identity of "this cohort keeps faulting".
    private readonly ConcurrentDictionary<string, int> _cohortGateFaultCounts = new(StringComparer.Ordinal);
    private readonly object _cohortGateRegistrationSync = new();

    private void SweepCompletedCohortGateRuns()
    {
        ObserveOwnedGroupedGateAttempts();
        foreach (var pair in _cohortGateRuns)
        {
            if (!pair.Value.Completion.Task.IsCompleted ||
                !_cohortGateRuns.TryRemove(pair.Key, out var completed))
            {
                continue;
            }

            ObserveCohortGateCompletion(pair.Key, completed);
        }
        RestoreRunningGroupedGateAttempts();
    }

    private bool TryGetActiveCohortGateRun(
        IReadOnlySet<string> candidateMemberGoalIds,
        out CohortGateRun? activeRun)
    {
        activeRun = _cohortGateRuns
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Value)
            .FirstOrDefault(run =>
                !run.Completion.Task.IsCompleted &&
                candidateMemberGoalIds.Overlaps(run.MemberGoalIds));
        return activeRun is not null;
    }

    private bool TryRegisterCohortGateRun(
        string memberPairKey,
        CohortGateRun run,
        out CohortGateRun? blockingRun,
        Action? afterSweepForTests = null)
    {
        lock (_cohortGateRegistrationSync)
        {
            while (true)
            {
                SweepCompletedCohortGateRuns();
                afterSweepForTests?.Invoke();
                afterSweepForTests = null;
                if (TryGetActiveCohortGateRun(run.MemberGoalIds, out blockingRun))
                {
                    return false;
                }

                if (_cohortGateRuns.TryAdd(memberPairKey, run))
                {
                    blockingRun = null;
                    return true;
                }

                if (!_cohortGateRuns.TryGetValue(memberPairKey, out var incumbent) ||
                    incumbent.Completion.Task.IsCompleted)
                {
                    continue;
                }

                throw new InvalidOperationException(
                    $"Cohort gate registration for '{memberPairKey}' lost ownership without an active overlapping run.");
            }
        }
    }

    internal IReadOnlySet<string> GetActiveCohortGateMemberGoalIds(
        Action<IReadOnlySet<string>, string>? observeActiveRun = null)
    {
        SweepCompletedCohortGateRuns();
        var activeRuns = _cohortGateRuns.Values
            .Where(run => !run.Completion.Task.IsCompleted)
            .OrderBy(run => run.PairFingerprint, StringComparer.Ordinal)
            .ToArray();
        foreach (var run in activeRuns)
        {
            observeActiveRun?.Invoke(run.MemberGoalIds, FormatCohortGateInFlightDetail(run, _utcNow()));
        }

        return activeRuns
            .SelectMany(run => run.MemberGoalIds)
            .ToHashSet(StringComparer.Ordinal);
    }

    internal bool TryRegisterCohortGateRunForTests(
        ConductorAcceptanceCohortSelection selection,
        TaskCompletionSource completion,
        Action? afterSweep = null)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(completion);
        var run = new CohortGateRun(
            _utcNow(),
            selection.Members.Select(member => member.GoalId.Value).ToHashSet(StringComparer.Ordinal),
            ConductorAcceptanceCohortSelector.PairFingerprint(selection.Members[0], selection.Members[1]),
            completion);
        return TryRegisterCohortGateRun(CohortGateMemberPairKey(selection), run, out _, afterSweep);
    }

    private static string FormatCohortGateInFlightDetail(CohortGateRun run, DateTimeOffset now) =>
        $"outcome=inflight owner={string.Join('+', run.MemberGoalIds.OrderBy(id => id, StringComparer.Ordinal))} " +
        $"fingerprint={run.PairFingerprint} elapsed_ms={Math.Max(0L, (long)(now - run.StartedAt).TotalMilliseconds)}";

    // Observes this member pair's completed background gate, if any, and takes the fault it parked. Called
    // before the ordinary cohort path so a faulted pair is reported as data instead of being restarted as a
    // fresh gate, and so the exception is never observed by a GetAwaiter().GetResult() on the tick thread.
    private ConductorAcceptanceCohortGateFault? TakeCohortGateFault(
        ConductorAcceptanceCohortSelection selection)
    {
        var memberPairKey = CohortGateMemberPairKey(selection);
        if (_cohortGateRuns.TryGetValue(memberPairKey, out var run) &&
            run.Completion.Task.IsCompleted &&
            _cohortGateRuns.TryRemove(memberPairKey, out var completed))
        {
            ObserveCohortGateCompletion(memberPairKey, completed);
        }

        SweepCompletedCohortGateRuns();
        return _cohortGateFaults.TryRemove(memberPairKey, out var fault) ? fault : null;
    }

    // Observes a completed background cohort gate without rethrowing: a fault is parked as typed data
    // for its member pair, and a clean completion clears that pair's transient-fault count.
    private void ObserveCohortGateCompletion(string memberPairKey, CohortGateRun run)
    {
        if (run.Completion.Task.Exception is { } aggregate)
        {
            var faults = memberPairKey.StartsWith("train:", StringComparison.Ordinal)
                ? _trainGateFaults
                : _cohortGateFaults;
            faults[memberPairKey] = new ConductorAcceptanceCohortGateFault(
                memberPairKey,
                run.MemberGoalIds,
                run.PairFingerprint,
                aggregate.InnerExceptions.Count == 1 ? aggregate.InnerExceptions[0] : aggregate);
            return;
        }

        _cohortGateFaultCounts.TryRemove(memberPairKey, out _);
    }

    // Publishes an already-completed background cohort gate run for the selection's member pair so the
    // drain path can be exercised without standing up a live gate.
    internal void PublishCompletedCohortGateRunForTests(
        ConductorAcceptanceCohortSelection selection,
        Exception? fault)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (fault is null)
        {
            completion.SetResult();
        }
        else
        {
            completion.SetException(fault);
        }

        _cohortGateRuns[CohortGateMemberPairKey(selection)] = new CohortGateRun(
            _utcNow(),
            selection.Members.Select(member => member.GoalId.Value).ToHashSet(StringComparer.Ordinal),
            ConductorAcceptanceCohortSelector.PairFingerprint(selection.Members[0], selection.Members[1]),
            completion);
    }

    private ConductorAcceptanceCohortRunResult CohortGateFaulted(
        IReadOnlyList<Goal> orderedGoals,
        ConductorAutonomyPolicy policy,
        ConductorAcceptanceCohortGateFault fault)
    {
        var goals = orderedGoals.Where(goal => fault.MemberGoalIds.Contains(goal.Id.Value)).ToArray();
        var detail =
            $"outcome=gate-fault fingerprint={fault.PairFingerprint} fault={fault.FaultType} " +
            $"detail={BoundCohortDetail(fault.Message)}";
        return new ConductorAcceptanceCohortRunResult(
            Receipt: null,
            goals.ToDictionary(
                goal => goal.Id.Value,
                goal => MakeResult(
                    goal.Id.Value,
                    goal.Id.Value[..8],
                    policy,
                    new ConductorAdvanceOutcome.Held(
                        GoalLifecycleState.Verified,
                        $"Acceptance cohort gate faulted: {detail}")),
                StringComparer.Ordinal),
            detail);
    }
}
