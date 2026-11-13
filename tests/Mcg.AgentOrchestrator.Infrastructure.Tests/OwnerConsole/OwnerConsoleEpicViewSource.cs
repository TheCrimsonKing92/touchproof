using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

internal sealed class OwnerConsoleEpicViewSource : IOwnerConsoleEpicSource
{
    internal IOrchestratorStateQueries State = null!;
    internal int ListCalls;
    internal int DetailCalls;
    internal CancellationToken DetailToken;
    internal readonly List<PortfolioEpic> Epics = [];
    internal readonly List<PortfolioEpicMember> Members = [];
    internal readonly List<GoalSummary> Goals = [];
    internal readonly List<BacklogItem> Backlog = [];
    internal readonly Dictionary<string, EpicPlanView> Plans = [];
    internal readonly List<string> PlanIds = [];
    internal int PlanCalls;
    internal Exception? PlanError;
    internal readonly TaskCompletionSource PlanStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource<EpicPlanView>? PendingPlan;
    internal int Calls;
    internal Exception? Error;
    internal TaskCompletionSource LoadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource<IReadOnlyList<EpicProgressRollup>>? Pending;
    internal TaskCompletionSource<IReadOnlyList<EpicProgressRollup>> HoldNextLoad()
    {
        LoadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        return Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    internal void Release(TaskCompletionSource<IReadOnlyList<EpicProgressRollup>> pending, DateTimeOffset? since)
    {
        Pending = null;
        pending.SetResult(Build(since));
    }
    internal IReadOnlyList<EpicProgressRollup> Build(DateTimeOffset? since) =>
        EpicProgressReadModel.Build(Epics, [], Members, Goals, Backlog, since);
    public Task<IReadOnlyList<EpicProgressRollup>> LoadAsync(DateTimeOffset? since, CancellationToken token)
    {
        ListCalls++;
        return ReadRows(since);
    }
    private Task<IReadOnlyList<EpicProgressRollup>> ReadRows(DateTimeOffset? since)
    {
        Calls++;
        LoadStarted.TrySetResult();
        if (Pending is { } pending) return pending.Task;
        return Error is null ? Task.FromResult(Build(since)) : Task.FromException<IReadOnlyList<EpicProgressRollup>>(Error);
    }
    public async Task<EpicDetailReadSnapshot> LoadDetailAsync(string epicId, DateTimeOffset? since, CancellationToken token)
    {
        DetailCalls++;
        DetailToken = token;
        var row = (await ReadRows(since).WaitAsync(token)).Single(row => row.Epic.Id == epicId);
        var plan = await LoadPlanAsync(row.Epic, token);
        var ids = row.MemberGoals.Where(member => member.Bucket is EpicProgressBucket.Active or EpicProgressBucket.Verifying or EpicProgressBucket.Parked ||
                member.Bucket == EpicProgressBucket.Failed && (since is null || member.UpdatedAt >= since))
            .Select(member => new GoalId(member.Id)).ToArray();
        var goals = (await State.LoadGoalsAsync(ids, token)).Goals.ToDictionary(goal => goal.Id.Value);
        return new(row with { Plan = plan }, since, goals);
    }
    public Task<string?> FindGoalEpicAsync(string goalId, CancellationToken token) =>
        Task.FromResult(Members.FirstOrDefault(member => member.Kind == PortfolioMemberKind.Goal && member.MemberId == goalId)?.EpicId);
    public Task<EpicPlanView> LoadPlanAsync(PortfolioEpic epic, CancellationToken token)
    {
        PlanCalls++;
        PlanIds.Add(epic.Id);
        PlanStarted.TrySetResult();
        if (PendingPlan is { } pending) return pending.Task;
        if (PlanError is { } error) return Task.FromException<EpicPlanView>(error);
        return Task.FromResult(Plans.GetValueOrDefault(epic.Id) ?? new(new(epic.Id, null, [], []), [], []));
    }
}
