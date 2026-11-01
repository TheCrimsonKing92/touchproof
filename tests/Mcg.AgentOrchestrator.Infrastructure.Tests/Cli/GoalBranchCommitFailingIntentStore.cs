using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Injects one precisely selected enqueue failure; every successful request uses the real store.
internal sealed class GoalBranchCommitFailingIntentStore(IOperatorIntentStore inner) : IOperatorIntentStore
{
    private int _mapSubmissions;
    internal int FailureCount { get; private set; }

    public Task<OperatorIntentRecord> EnqueueAsync(OperatorIntentRecord intent, CancellationToken cancellationToken = default)
    {
        if (intent.Verb == OperatorIntentVerbs.CriterionEvidenceMap && ++_mapSubmissions == 2)
        {
            FailureCount++;
            throw new InvalidOperationException("injected-second-rebind-failure");
        }
        return inner.EnqueueAsync(intent, cancellationToken);
    }

    public Task<OperatorIntentRecord?> ClaimNextAsync(string goalId, string claimOwner, CancellationToken cancellationToken = default) =>
        inner.ClaimNextAsync(goalId, claimOwner, cancellationToken);
    public Task<OperatorIntentRecord?> ClaimNextPendingAsync(string goalId, string claimOwner, CancellationToken cancellationToken = default) =>
        inner.ClaimNextPendingAsync(goalId, claimOwner, cancellationToken);
    public Task<OperatorIntentRecord?> ClaimNextByVerbAsync(string goalId, string verb, string claimOwner,
        CancellationToken cancellationToken = default) => inner.ClaimNextByVerbAsync(goalId, verb, claimOwner, cancellationToken);
    public Task CompleteAsync(string intentId, string claimOwner, OperatorIntentStatus status, string outcome,
        DateTimeOffset completedAt, CancellationToken cancellationToken = default) =>
        inner.CompleteAsync(intentId, claimOwner, status, outcome, completedAt, cancellationToken);
    public Task<OperatorIntentRecord?> GetAsync(string intentId, CancellationToken cancellationToken = default) =>
        inner.GetAsync(intentId, cancellationToken);
    public Task<IReadOnlyList<OperatorIntentRecord>> ListForGoalAsync(string goalId, int limit = 20,
        CancellationToken cancellationToken = default) => inner.ListForGoalAsync(goalId, limit, cancellationToken);
    public Task<IReadOnlyList<string>> ListActionableGoalIdsAsync(CancellationToken cancellationToken = default) =>
        inner.ListActionableGoalIdsAsync(cancellationToken);
    public Task<IReadOnlyDictionary<string, ActionableOperatorIntentSummary>> ListActionableSummariesAsync(
        IReadOnlyCollection<string> goalIds, CancellationToken cancellationToken = default) =>
        inner.ListActionableSummariesAsync(goalIds, cancellationToken);
    public void AcknowledgeWake(string intentId) => inner.AcknowledgeWake(intentId);
}
