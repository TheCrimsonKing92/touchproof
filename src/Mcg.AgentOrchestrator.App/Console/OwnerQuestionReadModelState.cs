using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed record OwnerQuestionStateSnapshot(IReadOnlyDictionary<string, GoalStatus> Goals,
    IReadOnlySet<string> AwaitingVerification, IReadOnlyList<OwnerQuestionCandidate> Candidates);

internal sealed record OwnerQuestionCandidate(
    OwnerQuestion Question, DateTimeOffset CreatedAt, bool BlocksActiveWork = true);

internal sealed class OwnerQuestionReadModelState(
    IOrchestratorStateQueries state, OwnerConsoleRefreshGoalSnapshots? snapshots)
{
    private readonly Dictionary<string, string> _terminalUpdatedAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, OwnerQuestionCandidate> _terminalHolds = new(StringComparer.OrdinalIgnoreCase);
    internal int TerminalHoldCount => _terminalHolds.Count;

    internal void ClearTerminalHolds()
    {
        _terminalHolds.Clear();
        _terminalUpdatedAt.Clear();
    }

    // State questions depend only on the state store. Collaboration-only commits must
    // not hydrate every goal again on the independent decision-first refresh path.
    internal async Task<OwnerQuestionStateSnapshot> ReadAsync(
        IReadOnlyList<GoalSummary> metadata, CancellationToken cancellationToken)
    {
        var terminal = metadata.Where(item => IsTerminal(item.Status)).ToArray();
        var ids = metadata.Where(item => !IsTerminal(item.Status)).Select(item => new GoalId(item.Id)).ToArray();
        var requests = await state.ListOpenHumanInputRequestsAsync(cancellationToken);
        await RefreshTerminalHoldsAsync(terminal, cancellationToken);
        var kernel = snapshots is not null ? null : ids.Length == 0 ? new AgentOrchestratorKernel() :
            await state.LoadGoalsAsync(ids, cancellationToken);
        var loaded = snapshots is null ? null : await snapshots.LoadAsync(ids, cancellationToken);
        var loadedIds = ids.Select(id => id.Value).ToHashSet(StringComparer.Ordinal);
        var loadedGoals = (loaded?.Goals ?? kernel!.Goals).Where(goal => loadedIds.Contains(goal.Id.Value)).ToArray();
        var goals = loadedGoals.ToDictionary(goal => goal.Id.Value, goal => goal.Status,
            StringComparer.OrdinalIgnoreCase);
        var awaitingVerificationGoals = loadedGoals
            .Where(goal => GoalLifecycle.ResolveState(goal) == GoalLifecycleState.AwaitingVerification)
            .Select(goal => goal.Id.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in terminal) goals[item.Id] = Enum.Parse<GoalStatus>(item.Status, ignoreCase: true);
        var candidates = new List<OwnerQuestionCandidate>();
        // The hydrated kernel owns request repair/sweeping for non-terminal goals. Detached rows
        // supply terminal and orphan requests, while retaining main's stable request order and ties.
        var detached = requests.Where(request => !loadedIds.Contains(request.GoalId)).ToArray();
        var detachedKernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([], detached));
        var authorHolds = loadedGoals.ToDictionary(goal => goal.Id.Value,
            goal => AuthorOwnerQuestionHold.TryParse(goal.CurrentHold), StringComparer.OrdinalIgnoreCase);
        var open = (loaded?.Requests ?? kernel!.HumanInputRequests).Where(request => !request.IsCompleted && loadedIds.Contains(request.GoalId.Value))
            .Concat(detachedKernel.HumanInputRequests.Where(request => !request.IsCompleted))
            .Select(request => RequestCandidate(request, authorHolds.GetValueOrDefault(request.GoalId.Value)))
            .ToDictionary(candidate => candidate.Question.ItemId, StringComparer.Ordinal);
        var ordered = requests.Select(request => request.Id).Concat(open.Keys).Distinct(StringComparer.Ordinal);
        candidates.AddRange(ordered.Where(open.ContainsKey).Select(id => open[id]));
        foreach (var goal in loadedGoals)
        {
            if (goal.CurrentHold is { } hold &&
                hold.State.Equals("steward-owner-question", StringComparison.OrdinalIgnoreCase))
                candidates.Add(new OwnerQuestionCandidate(new OwnerQuestion(hold.Identity, goal.Id.Value,
                    OwnerQuestionKind.StewardHold, StewardQuestionText(hold.Blocker)), hold.StartedAt));
        }
        candidates.AddRange(_terminalHolds.Values);
        return new(goals, awaitingVerificationGoals, candidates);
    }

    private async Task RefreshTerminalHoldsAsync(IReadOnlyList<GoalSummary> terminal,
        CancellationToken cancellationToken)
    {
        var terminalIds = terminal.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var id in _terminalUpdatedAt.Keys.Where(id => !terminalIds.Contains(id)).ToArray())
            _terminalUpdatedAt.Remove(id);
        foreach (var id in _terminalHolds.Keys.Where(id => !terminalIds.Contains(id)).ToArray())
            _terminalHolds.Remove(id);
        var changedTerminalIds = terminal.Where(item =>
                !_terminalUpdatedAt.TryGetValue(item.Id, out var updatedAt) || updatedAt != item.UpdatedAt)
            .Select(item => item.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (changedTerminalIds.Count > 0)
        {
            // Unchanged terminal goals reuse the session cache; changed rows replace even removed holds.
            var holds = await state.ListTerminalOwnerQuestionHoldsAsync(
                changedTerminalIds.Select(id => new GoalId(id)).ToArray(), cancellationToken);
            foreach (var id in changedTerminalIds) _terminalHolds.Remove(id);
            foreach (var hold in holds.Where(hold => changedTerminalIds.Contains(hold.GoalId)))
                _terminalHolds[hold.GoalId] = new OwnerQuestionCandidate(new OwnerQuestion(hold.Identity, hold.GoalId,
                    OwnerQuestionKind.StewardHold, StewardQuestionText(hold.Blocker)), hold.StartedAt);
            foreach (var item in terminal.Where(item => changedTerminalIds.Contains(item.Id)))
                _terminalUpdatedAt[item.Id] = item.UpdatedAt;
        }
    }

    private static bool IsTerminal(string status) =>
        Enum.TryParse<GoalStatus>(status, ignoreCase: true, out var parsed) &&
        parsed is GoalStatus.Completed or GoalStatus.Cancelled or GoalStatus.Superseded;

    private static OwnerQuestionCandidate RequestCandidate(HumanInputRequest request, AuthorOwnerQuestionHold? author)
    {
        var matches = author is { TargetKind: nameof(OperatorAnswerTargetKind.HumanInput) } &&
            string.Equals(author.TargetId, request.Id.Value, StringComparison.OrdinalIgnoreCase);
        return new(new OwnerQuestion(request.Id.Value, request.GoalId.Value, OwnerQuestionKind.HumanInput,
            request.Question, BlastRadius: matches ? author!.BlastRadius : null,
            ProposedDefault: matches && string.IsNullOrWhiteSpace(request.SuggestedDefaultAnswer)
                ? author!.Recommendation : request.SuggestedDefaultAnswer), request.RequestedAt,
            HumanWaitPolicyDefaults.BlocksActiveWork(request.Kind));
    }

    private static string StewardQuestionText(string blocker)
    {
        const string marker = "question=";
        var start = blocker.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return blocker;
        start += marker.Length;
        var end = blocker.IndexOf(" evidence=[", start, StringComparison.Ordinal);
        return (end < 0 ? blocker[start..] : blocker[start..end]).Trim();
    }
}
