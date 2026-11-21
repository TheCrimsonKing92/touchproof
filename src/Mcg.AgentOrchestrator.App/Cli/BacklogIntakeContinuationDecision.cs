using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal enum BacklogIntakeContinuationAction
{
    OrdinaryIntake,
    Continue,
    Resume,
    Replay,
    Refuse
}

internal enum BacklogIntakeContinuationRefusal
{
    ClaimMissing,
    OwnerMissing,
    OwnerActive,
    OwnerNotLanded,
    FullCoverage,
    ItemNotOpen,
    StaleIntake,
    IncompatibleOptions,
    SnapshotChanged,
    InvalidReservation,
    TransactionUnavailable
}

internal sealed record BacklogIntakeContinuationSnapshot(
    BacklogItemStatus? ItemStatus,
    SourceBacklogClaimSnapshot? Claim,
    GoalStatus? OwnerStatus,
    GoalLifecycleState? OwnerLifecycle,
    bool OwnerTerminal,
    BacklogIntakeRecord? Intake,
    IReadOnlyList<GoalReplacementLineageSnapshot> IntakeReplacementPath,
    BacklogIntakeContinuationReservation? Reservation,
    SourceBacklogContinuationLineage? Lineage)
{
    internal bool OwnerLanded => OwnerLifecycle is
        GoalLifecycleState.Merged or GoalLifecycleState.Recorded or GoalLifecycleState.CleanedUp;

    internal bool IntakeMatchesOwner => Intake is null ||
        (Intake.Status == "GoalCreated" &&
            (Intake.GoalId == Claim?.OwnerGoalId || IntakeReplacementPath.Count != 0));

    internal bool Matches(BacklogIntakeContinuationSnapshot expected) =>
        (this with { IntakeReplacementPath = expected.IntakeReplacementPath }) == expected &&
        IntakeReplacementPath.SequenceEqual(expected.IntakeReplacementPath);
}

internal readonly record struct BacklogIntakeContinuationDecision(
    BacklogIntakeContinuationAction Action,
    BacklogIntakeContinuationRefusal? Refusal = null)
{
    internal static BacklogIntakeContinuationDecision Evaluate(
        BacklogIntakeContinuationSnapshot snapshot, bool forceReclaim,
        bool correction, bool correctionReplay, GoalIntakePipelineRequest? pipeline, SourceBacklogCoverage? coverage)
    {
        if (!forceReclaim)
            return new(BacklogIntakeContinuationAction.OrdinaryIntake);
        if (snapshot.Reservation is not null)
            return CheckOptions(BacklogIntakeContinuationAction.Resume, pipeline, coverage);
        if (snapshot.Claim is null)
            return snapshot.Intake?.GoalId is null
                ? new(BacklogIntakeContinuationAction.OrdinaryIntake)
                : Refuse(BacklogIntakeContinuationRefusal.ClaimMissing);
        if (snapshot.OwnerStatus is null)
            return Refuse(BacklogIntakeContinuationRefusal.OwnerMissing);
        if (correctionReplay || (snapshot.OwnerTerminal && !snapshot.OwnerLanded && correction))
            return new(BacklogIntakeContinuationAction.OrdinaryIntake);
        if (!snapshot.OwnerTerminal && snapshot.Lineage is not null &&
            snapshot.Intake is { Status: "GoalCreated" } intake && intake.GoalId == snapshot.Lineage.SuccessorGoalId)
            return CheckOptions(BacklogIntakeContinuationAction.Replay, pipeline, coverage);
        if (snapshot.Claim.Coverage != SourceBacklogCoverage.Slice)
            return Refuse(BacklogIntakeContinuationRefusal.FullCoverage);
        if (!snapshot.OwnerTerminal)
            return Refuse(BacklogIntakeContinuationRefusal.OwnerActive);
        if (!snapshot.OwnerLanded)
            return Refuse(BacklogIntakeContinuationRefusal.OwnerNotLanded);
        if (snapshot.ItemStatus != BacklogItemStatus.Open)
            return Refuse(BacklogIntakeContinuationRefusal.ItemNotOpen);
        if (!snapshot.IntakeMatchesOwner)
            return Refuse(BacklogIntakeContinuationRefusal.StaleIntake);
        return CheckOptions(BacklogIntakeContinuationAction.Continue, pipeline, coverage);
    }

    private static BacklogIntakeContinuationDecision CheckOptions(
        BacklogIntakeContinuationAction action, GoalIntakePipelineRequest? pipeline, SourceBacklogCoverage? coverage) =>
        pipeline is (null or GoalIntakePipelineRequest.Auto or GoalIntakePipelineRequest.Scout) &&
            coverage is (null or SourceBacklogCoverage.Slice)
            ? new(action)
            : Refuse(BacklogIntakeContinuationRefusal.IncompatibleOptions);

    private static BacklogIntakeContinuationDecision Refuse(BacklogIntakeContinuationRefusal reason) =>
        new(BacklogIntakeContinuationAction.Refuse, reason);
}

internal sealed class BacklogIntakeContinuationException(string itemId, BacklogIntakeContinuationRefusal reason)
    : InvalidOperationException($"BACKLOG_INTAKE_CONTINUATION_REFUSED reason={reason} backlogItem={itemId}")
{
    internal BacklogIntakeContinuationRefusal Reason { get; } = reason;
}

internal sealed record BacklogIntakeContinuationHost(
    IOrchestratorStateOutboxRepository Repository,
    GoalIntakeRequestRecord? Request = null,
    bool RequestReplay = false,
    BacklogIntakeContinuationHooks? Hooks = null);

internal sealed record BacklogIntakeContinuationHooks(
    Action? BeforeFirstWrite = null,
    Action<BacklogIntakeContinuationReservation>? AfterPhaseCommit = null);

internal static class BacklogIntakeContinuation
{
    internal static bool TryExecute(
        CliExecutionContext context, BacklogIntakeItem item, BacklogItem backlogItem, GoalObjectivePlan plan,
        GoalIntakePipelineRequest? pipeline, SourceBacklogCoverage? coverage, bool forceReclaim, out bool created,
        Func<GoalObjectivePlan> buildScoutPlan)
    {
        created = false;
        if (!forceReclaim)
            return false;

        var workspace = context.Workspace;
        var claims = new SourceBacklogClaimStore(workspace.SqliteStatePath);
        var intake = new BacklogIntakeRecordStore(workspace.SqliteStatePath);
        var snapshot = CaptureBacklogContinuation(workspace, context.Kernel, item.Id, backlogItem.Status, claims, intake);
        var host = context.BacklogContinuation;
        if (host is { RequestReplay: true } && snapshot.Reservation?.RequestKey != host.Request?.RequestKey)
            return true; // Preserve the ordinary in-progress receipt when this key owns no continuation.

        var owner = context.Kernel.Goals.FirstOrDefault(goal => goal.Id.Value == snapshot.Claim?.OwnerGoalId);
        var hasMismatch = owner is not null && !snapshot.OwnerLanded && pipeline is not null &&
            BacklogIntakeGoalReuse.HasPipelineMismatch(plan, owner);
        var correctionReplay = !snapshot.OwnerTerminal && !hasMismatch && snapshot.IntakeReplacementPath.Count != 0;
        var decision = BacklogIntakeContinuationDecision.Evaluate(
            snapshot, forceReclaim, hasMismatch, correctionReplay, pipeline, coverage);
        if (decision.Action == BacklogIntakeContinuationAction.OrdinaryIntake)
            return false;
        if (decision.Refusal is { } refusal && refusal != BacklogIntakeContinuationRefusal.StaleIntake)
            throw new BacklogIntakeContinuationException(item.Id, refusal);
        if (host is null)
            throw new BacklogIntakeContinuationException(item.Id, BacklogIntakeContinuationRefusal.TransactionUnavailable);

        var scout = decision.Action == BacklogIntakeContinuationAction.Continue
            ? buildScoutPlan()
            : null;
        if (scout is not null)
        {
            GoalObjectivePlanner.ThrowIfBlocked(scout);
            GoalLifecycleCommands.EnsureRequestedPipelineCanBeSatisfied(scout, context.Agents);
        }

        var (successor, lineage) = CommitPhases(context, host, item, backlogItem, snapshot, scout,
            decision.Refusal, claims, intake);

        context.Kernel.ReplaceGoalWithSnapshot(successor);
        context.RecordDurableGoalBaseline(successor);
        context.CurrentGoal = context.Kernel.GetGoal(new GoalId(successor.Id));
        CliPersistentStateRunner.DeliverGoalCreationSideEffects(host.Repository, workspace, context.Kernel, context.CurrentGoal.Id);
        if (decision.Action != BacklogIntakeContinuationAction.Replay)
            EpicAtCreation.InheritAfterCommit(context, context.CurrentGoal, requested: null, item.Id);
        PrintBacklogContinuationLineage(lineage);
        GoalTextView.PrintGoal(context.CurrentGoal);
        created = scout is not null;
        return true;
    }

    private static (GoalSnapshot Successor, SourceBacklogContinuationLineage Lineage) CommitPhases(
        CliExecutionContext context, BacklogIntakeContinuationHost host, BacklogIntakeItem item, BacklogItem backlogItem,
        BacklogIntakeContinuationSnapshot snapshot, GoalObjectivePlan? scout, BacklogIntakeContinuationRefusal? refusal,
        SourceBacklogClaimStore claims, BacklogIntakeRecordStore intake)
    {
        var workspace = context.Workspace;
        var backlog = new BacklogStore(workspace.BacklogStorePath);
        host.Hooks?.BeforeFirstWrite?.Invoke();
        using var lease = backlog.TryAcquireOpenItemLease(item.Id, out var itemStatus);
        if (lease is null || itemStatus != snapshot.ItemStatus)
            throw BacklogIntakeSnapshotChanged(item.Id);
        var currentItem = BacklogStore.OpenReadOnly(workspace.BacklogStorePath)
            .GetByExactIdAsync(item.Id).GetAwaiter().GetResult();
        if (currentItem is null || currentItem.UpdatedAt != backlogItem.UpdatedAt ||
            !currentItem.Dependencies.SequenceEqual(backlogItem.Dependencies))
            throw BacklogIntakeSnapshotChanged(item.Id);

        var goalDependencies = backlogItem.Dependencies.Where(d => d.TargetKind == BacklogDependencyTargetKind.Goal)
            .Select(d => new GoalId(d.PrerequisiteId)).ToArray();
        var sourceIds = backlogItem.Dependencies.Where(d => d.TargetKind == BacklogDependencyTargetKind.Backlog)
            .Select(d => d.PrerequisiteId).Append(item.Id).ToArray();
        var scope = new GoalCreationLoadScope(goalDependencies, sourceIds);
        var requests = new GoalIntakeRequestStore(workspace.SqliteStatePath);
        GoalSnapshot? successor = null;
        SourceBacklogContinuationLineage? lineage = null;
        // Each commit advances the same reservation. The prepared goal stays Draft until
        // the claim and the intake record both identify it as the successor.
        while (successor is null)
        {
            var expected = snapshot;
            snapshot = host.Repository.TransactGoalCreationWithOutboxAsync(scope, (current, cancellationToken) =>
            {
                if (!CaptureBacklogContinuation(workspace, current, item.Id, itemStatus, claims, intake).Matches(expected))
                    throw BacklogIntakeSnapshotChanged(item.Id);
                if (refusal is { } stableRefusal)
                    throw new BacklogIntakeContinuationException(item.Id, stableRefusal);

                var goalChanged = false;
                IReadOnlyList<OrchestratorStateOutboxMessage> messages = [];
                if (expected.Reservation is { } pending)
                {
                    var prepared = current.Goals.FirstOrDefault(goal => goal.Id.Value == pending.SuccessorId);
                    if (prepared is null || prepared.Status != GoalStatus.Draft || prepared.SourceBacklogItemId != item.Id ||
                        prepared.SourceBacklogCoverage != SourceBacklogCoverage.Slice || expected.Intake != pending.ExpectedIntake)
                        throw InvalidBacklogIntakeReservation(item.Id);

                    switch (pending.Phase)
                    {
                        case BacklogContinuationPhase.Prepared:
                            if (expected.Claim is not { Coverage: SourceBacklogCoverage.Slice } predecessor ||
                                predecessor.OwnerGoalId != pending.PredecessorId || predecessor.Version != pending.ClaimVersion ||
                                !expected.OwnerTerminal || !expected.OwnerLanded || !expected.IntakeMatchesOwner)
                                throw BacklogIntakeSnapshotChanged(item.Id);
                            _ = claims.TransferContinuation(predecessor, pending.SuccessorId)
                                ?? throw BacklogIntakeSnapshotChanged(item.Id);
                            intake.MarkContinuationTransferred(pending);
                            break;

                        case BacklogContinuationPhase.Transferred:
                        case BacklogContinuationPhase.Rearmed:
                            lineage = expected.Lineage;
                            if (lineage is null || lineage.PredecessorGoalId != pending.PredecessorId ||
                                lineage.SuccessorGoalId != pending.SuccessorId || lineage.ClaimVersionBefore != pending.ClaimVersion)
                                throw InvalidBacklogIntakeReservation(item.Id);
                            if (pending.Phase == BacklogContinuationPhase.Transferred)
                            {
                                intake.RearmContinuation(pending, item.Heading);
                                break;
                            }

                            var events = new DeferredGoalLifecycleEventWriter();
                            current.SetEventWriter(events);
                            current.ActivateGoal(prepared.Id, context.Agents);
                            if (prepared.Status != GoalStatus.Active)
                                throw InvalidBacklogIntakeReservation(item.Id);
                            intake.CompleteContinuation(pending);
                            BindContinuationRequest(requests, pending.RequestKey, pending.RequestFingerprint, pending.SuccessorId);
                            if (host.Request?.RequestKey != pending.RequestKey)
                                BindContinuationRequest(requests, host.Request?.RequestKey, host.Request?.Fingerprint, pending.SuccessorId);
                            messages = [GoalCreationSideEffectDelivery.CreateMessage(prepared.Id, [], events.SnapshotEffects())];
                            successor = current.ExportGoalSnapshot(prepared.Id);
                            goalChanged = true;
                            break;

                        default:
                            throw InvalidBacklogIntakeReservation(item.Id);
                    }
                }
                else if (scout is not null)
                {
                    var scoped = new CliExecutionContext(current, workspace, context.Providers,
                        context.Agents, context.WorkerProfiles, currentGoal: null);
                    CliCommandHandlers.ValidateBacklogPromotionPrerequisites(scoped, backlogItem);
                    var existing = expected.Claim!;
                    // A legacy linked owner has an inferred version until the first store write.
                    if (existing.UpdatedAt == DateTimeOffset.MinValue)
                        existing = claims.ResolveOrMaterializeClaim(current, item.Id);
                    var draft = GoalLifecycleCommands.CreateGoalFromPlan(current, scout);
                    current.SetGoalSourceBacklogItemLink(draft.Id, item.Id, SourceBacklogCoverage.Slice);
                    CliCommandHandlers.ApplyBacklogPromotionDependencies(scoped, draft, backlogItem);
                    intake.PrepareContinuation(new(item.Id, existing.OwnerGoalId, existing.Version, draft.Id.Value,
                        BacklogContinuationPhase.Prepared, expected.Intake, host.Request?.RequestKey, host.Request?.Fingerprint));
                    goalChanged = true;
                }
                else
                {
                    lineage = expected.Lineage ?? throw InvalidBacklogIntakeReservation(item.Id);
                    successor = current.ExportGoalSnapshot(new GoalId(lineage.SuccessorGoalId));
                    BindContinuationRequest(requests, host.Request?.RequestKey, host.Request?.Fingerprint, successor.Id);
                }
                return Task.FromResult((goalChanged,
                    CaptureBacklogContinuation(workspace, current, item.Id, itemStatus, claims, intake), messages));
            }).GetAwaiter().GetResult();
            scout = null;
            if (snapshot.Reservation is { } committedPhase)
                host.Hooks?.AfterPhaseCommit?.Invoke(committedPhase);
        }
        return (successor, lineage ?? throw InvalidBacklogIntakeReservation(item.Id));
    }

    private static BacklogIntakeContinuationSnapshot CaptureBacklogContinuation(
        OrchestratorWorkspace workspace, AgentOrchestratorKernel kernel, string itemId, BacklogItemStatus? itemStatus,
        SourceBacklogClaimStore claims, BacklogIntakeRecordStore intake)
    {
        var claim = claims.ResolveClaim(kernel, itemId);
        var owner = kernel.Goals.FirstOrDefault(goal => goal.Id.Value == claim?.OwnerGoalId);
        var lifecycle = owner is null ? (GoalLifecycleState?)null : ReadOwnerLifecycle(workspace, owner);
        var record = intake.Get(itemId);
        var replacementPath = ReadIntakeReplacementPath(kernel, claims, itemId, owner, record?.GoalId);
        return new(itemStatus, claim, owner?.Status, lifecycle, owner?.IsTerminal == true, record, replacementPath,
            intake.GetContinuation(itemId), claim is null ? null : claims.FindContinuationLineage(claim));
    }

    private static IReadOnlyList<GoalReplacementLineageSnapshot> ReadIntakeReplacementPath(
        AgentOrchestratorKernel kernel, SourceBacklogClaimStore claims, string itemId, Goal? owner, string? intakeGoalId)
    {
        if (owner is null || intakeGoalId is null || intakeGoalId == owner.Id.Value ||
            !kernel.Goals.Any(goal => goal.Id.Value == intakeGoalId))
            return [];

        var replacements = claims.ListLineage(itemId).ToDictionary(row => row.SuccessorGoalId, StringComparer.Ordinal);
        var path = new List<GoalReplacementLineageSnapshot>();
        var successor = owner.Id.Value;
        // A replacement keeps the intake record on the original goal. Trace from the
        // current owner back to that goal, including each row in the write-boundary check.
        while (replacements.Remove(successor, out var change))
        {
            path.Add(change);
            if (change.PredecessorGoalId == intakeGoalId)
                return path;
            successor = change.PredecessorGoalId;
        }
        return [];
    }

    private static GoalLifecycleState ReadOwnerLifecycle(OrchestratorWorkspace workspace, Goal owner)
    {
        if (!owner.IsTerminal || owner.Status != GoalStatus.Completed)
            return GoalLifecycle.ResolveState(owner);

        var journal = GoalOperationJournal.Read(workspace.ExecutionDirectory, owner.Id);
        var landed = !GoalOperationJournal.HasRetiredTerminalDisposition(journal) &&
            (GoalOperationJournal.HasCompletedLandingEvidence(journal) ||
                GoalOperationJournal.HasMergeEvidenceTerminalDisposition(journal));
        return GoalLifecycle.ResolveState(owner, new GoalLifecycleFacts(
            IsMerged: landed,
            IsRecorded: GoalOperationJournal.HasCompletedRecordEvidence(journal),
            IsCleanedUp: GoalOperationJournal.HasCompletedCleanupEvidence(journal)));
    }

    private static void BindContinuationRequest(
        GoalIntakeRequestStore requests, string? requestKey, string? fingerprint, string goalId)
    {
        if (requestKey is null)
            return;
        if (requests.Get(requestKey) is { State: GoalIntakeRequestStates.Created } existing && existing.GoalId == goalId)
            return;
        _ = requests.MarkCreated(requestKey, fingerprint!, goalId);
    }

    internal static void PrintBacklogContinuationLineage(SourceBacklogContinuationLineage lineage) =>
        Console.WriteLine($"BACKLOG_INTAKE_CONTINUED backlogItem={lineage.BacklogItemId} " +
            $"predecessor={lineage.PredecessorGoalId} successor={lineage.SuccessorGoalId} " +
            $"claimVersionBefore={lineage.ClaimVersionBefore} claimVersionAfter={lineage.ClaimVersionAfter} " +
            $"transferredAt={lineage.TransferredAt:O}");

    private static BacklogIntakeContinuationException BacklogIntakeSnapshotChanged(string itemId) =>
        new(itemId, BacklogIntakeContinuationRefusal.SnapshotChanged);

    private static BacklogIntakeContinuationException InvalidBacklogIntakeReservation(string itemId) =>
        new(itemId, BacklogIntakeContinuationRefusal.InvalidReservation);
}
