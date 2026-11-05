using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owns workspace preparation and its evidence-mutation lease wiring. Recovery retains
// the coordinator's scope through ordinary preparation and the dispatch checkpoint.
internal sealed class ConductorWorkspacePreparation
{
    private readonly string _executionDirectory;
    private readonly ReconcileSweepRemediationStore _leaseStore;
    private readonly TimeSpan _leaseDuration;
    private readonly Func<IReadOnlyList<Goal>> _readGoals;
    private readonly Func<GoalId, bool> _hasGateOwner;
    private readonly GoalWorktreeCleanupHooks _hooks;
    private readonly Action<Goal, string> _workspaceRestored;

    internal GoalEvidenceOperationCoordinator Coordinator { get; }

    internal ConductorWorkspacePreparation(
        OrchestratorWorkspace workspace, TimeSpan leaseDuration,
        Func<IReadOnlyList<Goal>> readGoals, GoalWorktreeCleanupHooks hooks,
        Action<Goal, string> workspaceRestored)
    {
        _executionDirectory = workspace.ExecutionDirectory;
        _leaseStore = new ReconcileSweepRemediationStore(workspace.SqliteStatePath);
        _leaseDuration = leaseDuration;
        _readGoals = readGoals;
        _hasGateOwner = GoalLiveAcceptancePredicate.ForWorkspace(workspace, requireWorkspaceSettlement: true);
        _hooks = hooks;
        _workspaceRestored = workspaceRestored;
        Coordinator = new GoalEvidenceOperationCoordinator(
            _leaseStore, _executionDirectory, leaseDuration, pidProbe: new ConductLockPidProbe());
    }

    internal ReconcileAcceptanceLeaseState? ReadLease(Goal goal) =>
        _leaseStore.TryGetAcceptanceLease(goal.Id.Value, _leaseDuration);

    internal GoalEvidenceOperationStart BeginDeveloperIntegration(Goal goal) =>
        Coordinator.TryBegin(goal, "conductor:developer-branch-integration");

    internal IDisposable? AcquireMutationLease(Goal goal, string operation)
    {
        Coordinator.TryRecoverTerminal(goal);
        var owner = $"goal-evidence:{operation}:{Environment.ProcessId}:{Guid.NewGuid():N}";
        return _leaseStore.TryAcquireAcceptanceLease(goal.Id.Value, owner, _leaseDuration);
    }

    internal GoalEvidenceOperationScope? PrepareMissing(Goal goal) =>
        ConductorMissingWorktreeRecovery.Prepare(
            _executionDirectory, goal, Coordinator, _readGoals, _hasGateOwner,
            _hooks, path => _workspaceRestored(goal, path));

    internal string Create(Goal goal)
    {
        using var evidenceMutationLease = AcquireMutationLease(goal, "conductor:workspace-create")
            ?? throw new ConductorDriver.EvidenceMutationLeaseUnavailableException(
                $"GOAL_OPERATION_BLOCKED goal={goal.Id.Value} operation=conductor:workspace-create reason=concurrent-acceptance-or-replacement");
        GoalOperationJournal.Begin(_executionDirectory, goal, "conductor:workspace-create", GoalWorktrees.BranchName(goal.Id));
        var hasWorkspaceHistory = goal.Tasks.Any(task => task.LastDispatch is not null) ||
            GoalOperationJournal.ReadStrict(_executionDirectory, goal.Id).Entries.Any(entry => entry.Status == GoalOperationStatus.Completed &&
                entry.Operation is "conductor:workspace-create" or ConductorMissingWorktreeRecovery.Operation);
        var path = GoalWorktrees.PrepareForDispatch(_executionDirectory, goal.Id, allowInitialCreation: !hasWorkspaceHistory,
            () => ConductorMissingWorktreeRecovery.OwnerRefusal(goal.Id, _readGoals(), _hasGateOwner),
            receipt => ConductorMissingWorktreeRecovery.RecordReceipt(_executionDirectory, goal, receipt), _hooks);
        GoalOperationJournal.Completed(_executionDirectory, goal, "conductor:workspace-create", path);
        _workspaceRestored(goal, path);
        return path;
    }
}
