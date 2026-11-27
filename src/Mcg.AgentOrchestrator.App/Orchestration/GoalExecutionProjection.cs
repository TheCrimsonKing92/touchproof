using System.Diagnostics;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class GoalExecutionProjection
{
    private GoalExecutionProjection(
        Goal goal,
        string? workspacePath,
        GoalLifecycleFacts lifecycleFacts,
        DispatchReadinessVerdict verdict,
        GoalAcceptanceSummary? acceptance)
    {
        Goal = goal;
        WorkspacePath = workspacePath;
        LifecycleFacts = lifecycleFacts;
        Verdict = verdict;
        Acceptance = acceptance;
    }

    public Goal Goal { get; }
    public string? WorkspacePath { get; }
    public GoalLifecycleFacts LifecycleFacts { get; }
    public DispatchReadinessVerdict Verdict { get; }
    public GoalAcceptanceSummary? Acceptance { get; }

    internal static GoalExecutionProjection Build(
        Goal goal,
        IReadOnlyList<AgentDefinition> agents,
        string? executionDirectory,
        WorkerProfileCatalog? profiles,
        IReadOnlyCollection<Goal>? providerHoldScope,
        DateTimeOffset now,
        Func<string, GoalId, string?>? resolveWorktree = null,
        AgentOrchestratorKernel? kernel = null,
        ModelInventory? inventory = null,
        string? integrationBranch = null,
        OrchestratorWorkspace? workspace = null,
        bool? hasOpenClarification = null)
    {
        var worktree = executionDirectory is null
            ? null
            : (resolveWorktree ?? GoalWorktrees.TryResolve)(executionDirectory, goal.Id);
        DispatchReadinessVerdict verdict = profiles is not null
            ? DispatchReadinessAssessment.Evaluate(goal, providerHoldScope, agents, profiles, now, inventory).Verdict
            : DispatchReadinessRules.HasAssignedDispatchCandidates(goal)
                ? new DispatchReadinessReady()
                : new DispatchReadinessBlocked("No assigned dispatch candidates");

        var journal = executionDirectory is null ? null : TryReadJournal(executionDirectory, goal.Id);
        var branchHead = journal is null || string.IsNullOrWhiteSpace(worktree)
            ? null
            : GoalAcceptanceStatusProjector.TryResolveHead(worktree);
        var mainHead = string.IsNullOrWhiteSpace(branchHead) || executionDirectory is null
            ? null
            : GoalAcceptanceStatusProjector.TryResolveHead(executionDirectory);
        var lifecycleJournal = journal ?? new GoalOperationJournalSummary(string.Empty, [], [], []);
        var isBlocked = HasBlockingAcceptance(goal, lifecycleJournal, branchHead, mainHead) ||
            lifecycleJournal.LatestByOperation.Any(entry =>
                entry.Status == GoalOperationStatus.Failed &&
                (entry.Operation.Contains("land", StringComparison.OrdinalIgnoreCase) ||
                 entry.Operation.Contains("cleanup", StringComparison.OrdinalIgnoreCase) ||
                 entry.Operation.Contains("workspace:remove", StringComparison.OrdinalIgnoreCase)));
        var facts = new GoalLifecycleFacts(
            WorkspaceExists: worktree is not null,
            IsBlocked: isBlocked,
            IsMerged: GoalOperationJournal.HasCompletedLandingEvidence(lifecycleJournal),
            IsRecorded: GoalOperationJournal.HasCompletedRecordEvidence(lifecycleJournal),
            IsCleanedUp: GoalOperationJournal.HasCompletedCleanupEvidence(lifecycleJournal),
            HasOpenClarification: hasOpenClarification ?? (workspace is not null && GoalRefinementGate.HasOpenClarification(workspace, goal)));

        var acceptance = kernel is null
            ? null
            : GoalAcceptanceStatusProjector.Build(kernel, goal,
                worktree is not null && journal is not null ? executionDirectory : null,
                integrationBranch ?? TrunkBranchName.Default);
        return new GoalExecutionProjection(goal, worktree, facts, verdict, acceptance);
    }

    private static GoalOperationJournalSummary? TryReadJournal(string executionDirectory, GoalId goalId)
    {
        try
        {
            return GoalOperationJournal.Read(executionDirectory, goalId);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Goal {0} journal could not be read for execution projection. {1}", goalId.Value, error.Message);
            return null;
        }
    }

    private static bool HasBlockingAcceptance(
        Goal goal, GoalOperationJournalSummary journal, string? branchHead, string? mainHead)
    {
        if (string.IsNullOrWhiteSpace(branchHead) || string.IsNullOrWhiteSpace(mainHead))
        {
            return goal.LatestAcceptanceFailure is not null || journal.LatestByOperation.Any(entry =>
                entry.Status == GoalOperationStatus.Failed &&
                entry.Operation.Contains("acceptance", StringComparison.OrdinalIgnoreCase));
        }

        return GoalOperationJournal.NewestAcceptanceOutcomeForCandidate(journal, branchHead, mainHead)?.AcceptanceOutcome is { } outcome &&
            !outcome.Equals("passed", StringComparison.OrdinalIgnoreCase);
    }
}
