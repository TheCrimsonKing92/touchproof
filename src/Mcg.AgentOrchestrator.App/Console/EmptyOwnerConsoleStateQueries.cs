using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Re-resolve absence on each read so a store created after startup becomes visible.
internal sealed class EmptyOwnerConsoleStateQueries(string? path = null) : IOrchestratorStateQueries
{
    private IOrchestratorStateQueries? Current => path is not null && OwnerConsoleBoardSource.Exists(path)
        ? SqliteOrchestratorStateRepository.OpenReadOnly(path) : null;

    public Task<IReadOnlyList<AcceptanceQueueFact>> ListAcceptanceQueueFactsAsync(CancellationToken cancellationToken = default) =>
        Current?.ListAcceptanceQueueFactsAsync(cancellationToken) ?? Task.FromResult<IReadOnlyList<AcceptanceQueueFact>>([]);

    public Task<IReadOnlyList<GoalSummary>> ListGoalBoardRowsAsync(GoalBoardRowQuery query,
        CancellationToken cancellationToken = default) => Current?.ListGoalBoardRowsAsync(query, cancellationToken) ??
        Task.FromResult<IReadOnlyList<GoalSummary>>([]);
    public Task<IReadOnlyList<HumanInputRequestSnapshot>> ListOpenHumanInputRequestsAsync(
        CancellationToken cancellationToken = default) =>
        Current?.ListOpenHumanInputRequestsAsync(cancellationToken) ?? Task.FromResult<IReadOnlyList<HumanInputRequestSnapshot>>([]);

    public Task<IReadOnlyList<TerminalOwnerQuestionHold>> ListTerminalOwnerQuestionHoldsAsync(
        IReadOnlyCollection<GoalId> goalIds, CancellationToken cancellationToken = default) =>
        Current?.ListTerminalOwnerQuestionHoldsAsync(goalIds, cancellationToken) ?? Task.FromResult<IReadOnlyList<TerminalOwnerQuestionHold>>([]);

    public Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(CancellationToken cancellationToken = default) =>
        Current?.ListGoalMetadataAsync(cancellationToken) ?? Task.FromResult<IReadOnlyList<GoalSummary>>([]);

    public Task<AgentOrchestratorKernel> LoadGoalsAsync(IReadOnlyCollection<GoalId> ids, CancellationToken cancellationToken = default) =>
        Current?.LoadGoalsAsync(ids, cancellationToken) ?? Task.FromResult(new AgentOrchestratorKernel());
}
