using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class DecisionSnapshotEvaluator(
    string stateDirectory,
    SqliteOrchestratorStateRepository repository,
    ConductorTransitionDecision verifiedTransition,
    DateTimeOffset asOf)
{
    internal const int SlotIndex = 0;
    private readonly CollaborationItemStore? _collaboration = CollaborationItemStore.OpenReadOnlyOfflineCopy(stateDirectory);
    private readonly IReadOnlyList<AgentDefinition> _agents = AgentCatalogStore.Load(Path.Combine(stateDirectory, "agents.json")).Agents;
    private readonly WorkerProfileCatalog _profiles = WorkerProfileStore.Load(Path.Combine(stateDirectory, "workers.json"));

    internal DecisionSnapshotDecision Evaluate(Goal goal, IReadOnlyCollection<Goal> providerHoldScope)
    {
        var journal = new Lazy<GoalOperationJournalSummary>(() => GoalOperationJournal.ReadFromStateCopy(stateDirectory, goal.Id));
        var decline = ParallelAcceptanceEligibility.GetDeclineReason(goal, verifiedTransition,
            HasRoutableCohortFailure, subject => ReadLifecycleFacts(subject, journal.Value),
            subject => HasAcceptanceHold(subject, journal.Value));
        if (decline is null)
        {
            var lease = repository.ReadAcceptanceLease(goal.Id, ConductorDriver.EvidenceMutationLeaseDuration);
            var activeLease = lease?.ExpiresAtUtc > asOf ? lease : null;
            ParallelAcceptanceEligibility.ValidateSlot(goal, SlotIndex,
                id => activeLease is not null && HasLiveAttempt(id),
                _ => activeLease, _ => ConductorBatchLoop.DefaultParallelAcceptanceCapacity);

            _ = ConductorParallelAcceptanceCandidate.Create(goal, SlotIndex, ConductorDriver.InferRecordedFileScopes(goal));
        }

        var verdicts = new SortedDictionary<string, bool>(StringComparer.Ordinal);
        foreach (var task in goal.Tasks) verdicts.Add(task.Id.Value, AcceptancePrecheck.CountsAsPassed(goal, task));

        return new(ReadReadyIds(goal, providerHoldScope),
            decline is null ? DecisionSnapshotDecision.CandidateBuilt : DecisionSnapshotDecision.Declined,
            decline, goal.Status, goal.CurrentHold, verdicts);
    }

    private IReadOnlyList<string> ReadReadyIds(Goal goal, IReadOnlyCollection<Goal> providerHoldScope)
    {
        if (!DispatchReadinessAssessment.IsCandidateGoal(goal) ||
            !DispatchReadinessRules.HasAssignedDispatchCandidates(goal, asOf)) return [];

        var sandbox = new WorkerSandboxOptions(false, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);
        var available = ReadWithLauncherAvailability(true);
        var unavailable = ReadWithLauncherAvailability(false);
        // Launcher availability is not a fact in the copy. Preserve an unavailable-input
        // exception only where the two readiness outcomes actually depend on that fact.
        if (!available.SequenceEqual(unavailable))
            throw new InvalidOperationException("Dispatch readiness depends on launcher availability outside the copied state.");
        return available;

        string[] ReadWithLauncherAvailability(bool available) =>
            DispatchReadinessAssessment.ReadyItems(DispatchReadinessAssessment.Evaluate(
                    goal, providerHoldScope, _agents, _profiles, asOf, sandbox, _ => available,
                    () => throw new InvalidOperationException("Dispatch readiness requires credential evidence outside the copied state.")))
                .Select(item => item.TaskId).Order(StringComparer.Ordinal).ToArray();
    }

    private GoalLifecycleFacts ReadLifecycleFacts(Goal goal, GoalOperationJournalSummary journal)
    {
        var items = _collaboration?.ListAsync(goal.Id.Value).GetAwaiter().GetResult() ?? [];
        return new GoalLifecycleFacts(
            IsMerged: GoalOperationJournal.HasCompletedLandingEvidence(journal),
            IsRecorded: GoalOperationJournal.HasCompletedRecordEvidence(journal),
            IsCleanedUp: GoalOperationJournal.HasCompletedCleanupEvidence(journal),
            HasOpenClarification: GoalRefinementService.HasOpenClarification(items));
    }

    private static bool HasRoutableCohortFailure(Goal goal)
    {
        if (goal.LatestAcceptanceFailure is not { } failure ||
            !ConductorDriver.IsRecordedCohortAttributionFailure(failure)) return false;
        if (string.IsNullOrWhiteSpace(failure.BranchHeadSha) || string.IsNullOrWhiteSpace(failure.MainHeadSha)) return false;
        throw new InvalidOperationException("Cohort attribution requires current branch and integration revisions outside the copied state.");
    }

    private bool HasAcceptanceHold(Goal goal, GoalOperationJournalSummary journal)
    {
        if (goal.LatestAcceptanceFailure is not { } failure) return false;
        if (failure.IsEnvironmentalApparatus)
        {
            if (string.IsNullOrWhiteSpace(failure.BranchHeadSha) && string.IsNullOrWhiteSpace(failure.MainHeadSha)) return true;
            throw new InvalidOperationException("Apparatus hold requires current revisions outside the copied state.");
        }

        if (failure.BranchHeadSha is not { } sha ||
            ConductorDriver.ReadOwnerReviewHold(failure, sha, journal) is not { } hold) return false;

        if (_collaboration is not null &&
            (AcceptancePolicyChangeDecision.IsApprovedAsync(_collaboration, goal.Id.Value, sha).GetAwaiter().GetResult() ||
             hold.Fingerprint is { } fingerprint &&
             AcceptancePolicyChangeDecision.IsApprovedForFingerprintAsync(_collaboration, goal.Id.Value, fingerprint).GetAwaiter().GetResult()))
        {
            return false;
        }

        throw new InvalidOperationException("Owner review hold requires the current branch revision outside the copied state.");
    }

    private bool HasLiveAttempt(string goalId)
    {
        var root = Path.Combine(stateDirectory, "acceptance-gate-attempts");
        var attempt = ConductorParallelAcceptanceAttemptCoordinator.ReadLatest(root, goalId, () => asOf);
        if (attempt is not { Outcome: ConductorParallelAcceptanceAttemptOutcome.Running, ReconciledAt: null })
            return false;

        var prefix = attempt.MetadataPath[..^".attempt.json".Length];
        if (File.Exists(prefix + ".result.json") || File.Exists(prefix + ".exit.txt")) return false;
        var heartbeat = ConductorParallelAcceptanceAttemptCoordinator.ReadHeartbeatObservedAt(prefix + ".heartbeat.json") ??
            attempt.LastHeartbeatAt;
        if (asOf - heartbeat >= DispatchRecoveryPolicy.DefaultRecentHeartbeatGrace) return false;
        throw new InvalidOperationException("The lease decision requires process liveness outside the copied state.");
    }
}
