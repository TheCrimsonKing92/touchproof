using System.Collections.Immutable;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleViewModelBuilder(IOrchestratorStateQueries state,
    IOwnerQuestionSource questions, IConductorLiveness liveness, IOwnerGoalEpicLookup epics, TimeProvider clock,
    Func<OwnerConductEvent, OwnerActivityTestEvidence?>? testEvidence = null, Func<string, string?>? gateMotion = null,
    Func<DateTimeOffset?>? latestTick = null,
    Func<IReadOnlyDictionary<string, string>, OwnerConsoleGateProgress?>? gateProgress = null,
    OwnerConsoleRefreshGoalSnapshots? snapshots = null,
    Func<Goal, (string? Candidate, IReadOnlyList<GoalOperationJournalEntry> Attempts)?>? candidateGate = null,
    Func<string?>? decisionsMarker = null, Func<string?>? boardMarker = null,
    Func<string?>? epicMarker = null)
{
    internal const int MaxActivityItems = 100;
    internal const int RecentTerminalTitleRows = 200;
    internal const int TitlePrefixChars = 1024;
    private readonly Dictionary<string, int> _numbers = new(StringComparer.OrdinalIgnoreCase);
    private readonly OwnerConsoleGoalTitleCache _titles = new();
    private IReadOnlyDictionary<string, AgentRole> _roles = new Dictionary<string, AgentRole>();
    private IReadOnlyDictionary<string, Goal> _goals = new Dictionary<string, Goal>();
    private readonly object _observationGate = new();
    private readonly OwnerNeedsYouLedger _attention = new();
    private readonly Dictionary<string, bool> _previousHolds = new();
    private readonly List<OwnerConductEvent> _observed = [];
    private readonly OwnerConsoleDecisionCache _decisions = new();
    private readonly OwnerConsoleDecisionMarker _decisionMarker = new(decisionsMarker);
    private string? _boardVersion;
    private IReadOnlyList<GoalSummary>? _metadata;
    private IReadOnlyCollection<Goal>? _boardGoals;
    private (Goal Goal, OwnerConsoleBoardSortKey Key, string Title)[]? _ranked;
    private string? _epicVersion;
    private string[] _epicIds = [];
    private IReadOnlyDictionary<string, string>? _epicTitles;
    private OwnerDecisionRead? _projectedRead;
    private ImmutableArray<OwnerConsoleDecision> _projectedDecisions;
    private int _nextNumber;
    internal (int Goals, int Roles, int Numbers, int Holds) Retained => (_goals.Count, _roles.Count, _numbers.Count, _previousHolds.Count);

    internal async Task<OwnerConsoleViewModel> BuildAsync(OwnerConsoleViewInputs inputs,
        CancellationToken cancellationToken = default)
    {
        var (decisions, hidden) = await ReadDecisionsAsync(cancellationToken);
        var model = await BuildBoardAsync(cancellationToken);
        var evidence = new OwnerActivityEvidenceLoader(testEvidence).Resolve(inputs.RecentEvents);
        return WithActivity(WithDecisions(model, decisions, hidden), inputs, evidence);
    }

    internal async Task<(ImmutableArray<OwnerConsoleDecision> Decisions, int Hidden)> ReadDecisionsAsync(
        CancellationToken cancellationToken, bool force = false)
    {
        if (force) questions.Invalidate();
        var read = await _decisions.ReadAsync(questions, _decisionMarker.Read(), force, cancellationToken, _decisionMarker.Read);
        var snapshot = read.Snapshot;
        var unchanged = _projectedRead is { } previous && previous.Entries.SequenceEqual(read.Entries) &&
            previous.Snapshot.Live.SequenceEqual(snapshot.Live);
        var decisions = unchanged ? _projectedDecisions : read.Entries.Zip(snapshot.Live, (question, source) =>
        {
            if (!_numbers.TryGetValue(question.ItemId, out var number))
                _numbers.Add(question.ItemId, number = ++_nextNumber);
            var label = question.Kind == OwnerQuestionKind.Onboarding &&
                OnboardingQuestionSource.TryIdentify(question.ItemId, out var project, out _, out _)
                ? project : question.GoalPrefix;
            return new OwnerConsoleDecision(question.ItemId, number, question.GoalId, label,
                question.Kind, Summary(question.Text), question.Text, question.BlastRadius,
                question.Confidence, question.ProposedDefault,
                question.RaisedAt, source.CanAnswer);
        }).ToImmutableArray();
        _projectedRead = read;
        _projectedDecisions = decisions;
        var liveIds = snapshot.Live.Select(question => question.ItemId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var id in _numbers.Keys.Where(id => !liveIds.Contains(id)).ToArray()) _numbers.Remove(id);
        lock (_observationGate)
        {
            _attention.Observe(snapshot.Live, clock.GetUtcNow());
        }
        return (decisions, snapshot.Hidden.Count);
    }

    internal async Task<OwnerConsoleViewModel> BuildBoardAsync(CancellationToken cancellationToken = default, bool force = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _decisionMarker.BeforeBoard();
        var version = boardMarker?.Invoke();
        var reuse = !force && version is not null && version == _boardVersion && _metadata is not null && _boardGoals is not null;
        if (!reuse)
        {
            _boardVersion = null;
            if (boardMarker is not null) snapshots?.Invalidate();
        }
        var metadata = reuse ? _metadata! : await state.ListGoalBoardRowsAsync(
            new(RecentTerminalTitleRows, TitlePrefixChars, BacklogIntakePlanner.TargetScopeHeadingLine), cancellationToken);
        if (!reuse) { _titles.Merge(metadata, force: boardMarker is not null); _titles.Retain(metadata.Select(row => row.Id)); }
        var now = clock.GetUtcNow();
        OwnerConsoleGateProgress? gate = null;
        try { gate = gateProgress?.Invoke(_titles.Titles); }
        catch { /* A failed observation must not block the board refresh. */ }
        var ids = metadata.Where(item => Enum.TryParse<GoalStatus>(item.Status, true, out var status) &&
                (IsActive(status) || status is GoalStatus.Failed or GoalStatus.Parked &&
                    (!DateTimeOffset.TryParse(item.UpdatedAt, out var updated) || now - updated <= TimeSpan.FromHours(24))))
            .Select(item => new GoalId(item.Id)).ToArray();
        snapshots?.Observe(metadata.Where(item => ids.Any(id => id.Value == item.Id)).ToArray());
        var board = ImmutableArray.CreateBuilder<OwnerConsoleBoardRow>();
        if (ids.Length > 0)
        {
            var loaded = reuse ? _boardGoals! : snapshots is null ? (await state.LoadGoalsAsync(ids, cancellationToken)).Goals :
                (await snapshots.LoadAsync(ids, cancellationToken)).Goals;
            if (!reuse)
            {
                // Retain roles for the tail's final read after a goal leaves the board.
                var goals = loaded.Concat(_goals.Values).DistinctBy(goal => goal.Id.Value)
                    .Take(loaded.Count + MaxActivityItems).ToDictionary(goal => goal.Id.Value);
                var roles = new Dictionary<string, AgentRole>();
                foreach (var task in goals.Values.SelectMany(goal => goal.Tasks))
                    roles[task.Id.Value] = task.RequiredRole;
                _roles = roles;
                _goals = goals;
                lock (_observationGate)
                {
                    foreach (var goal in loaded)
                    {
                        var hasHold = goal.CurrentHold is not null;
                        if (_previousHolds.GetValueOrDefault(goal.Id.Value) && !hasHold)
                            Observe(new(clock.GetUtcNow(), "owner-hold-cleared", goal.Id.Value, ""));
                        _previousHolds[goal.Id.Value] = hasHold;
                    }
                }
            }
            foreach (var id in _previousHolds.Keys.Where(id => !ids.Any(goal => goal.Value == id)).ToArray())
                _previousHolds.Remove(id);
            var rankedSource = reuse && _ranked is not null ? _ranked : loaded
                .Select(goal => (Goal: goal, Key: OwnerConsoleBoardSortKey.For(goal), Title: OwnerGoalTitle.Full(goal.Objective)))
                .OrderBy(item => item.Key).ToArray();
            var ranked = rankedSource.Where(item => ids.Contains(item.Goal.Id))
                .Where(item => IsActive(item.Goal.Status) || item.Key.Rank == OwnerConsoleAttention.RecentTerminal &&
                    item.Key.Since is { } at && now - at <= TimeSpan.FromHours(24)).ToArray();
            var epicIds = ranked.Select(item => item.Goal.Id.Value).ToArray();
            var epicVersion = epicMarker?.Invoke();
            var reuseEpics = !force && epicVersion is not null && epicVersion == _epicVersion &&
                _epicTitles is not null && _epicIds.SequenceEqual(epicIds);
            if (!reuseEpics) _epicVersion = null;
            var epicTitles = reuseEpics ? _epicTitles! :
                epicIds.Length == 0 ? new Dictionary<string, string>() : await epics.GetTitlesAsync(epicIds, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _epicVersion = epics.CanReuse && epicVersion is not null && epicVersion == epicMarker?.Invoke() ? epicVersion : null;
            _epicIds = epicIds;
            _epicTitles = epicTitles;
            _boardGoals = loaded;
            _ranked = rankedSource;
            var queuePosition = 0;
            foreach (var (goal, key, title) in ranked)
            {
                var stage = OwnerConsoleStageDescriber.Describe(goal, key,
                    key.Rank == OwnerConsoleAttention.WaitingForGate && gate?.ForMember(goal.Id.Value) is null ? queuePosition + 1 : null,
                    now, gate, candidateGate);
                var passedHeld = stage.StartsWith("passed gate, held:", StringComparison.Ordinal) ||
                    stage.StartsWith("gate passed, landing held:", StringComparison.Ordinal);
                if (key.Rank == OwnerConsoleAttention.WaitingForGate && !passedHeld &&
                    gate?.ForMember(goal.Id.Value) is null) queuePosition++;
                if (key.Rank == OwnerConsoleAttention.GateRunning && (gate is null || gate.ForMember(goal.Id.Value) is not null) &&
                    gateMotion?.Invoke(goal.Id.Value) is { } motion &&
                    !string.IsNullOrWhiteSpace(motion)) stage += ", " + motion;
                var row = new OwnerConsoleBoardRow(Prefix(goal.Id.Value), epicTitles.GetValueOrDefault(goal.Id.Value, ""),
                    title, goal.Status.ToString(), stage,
                    Age(goal.Timeline.MaxBy(item => item.OccurredAt)?.OccurredAt), goal.Id.Value,
                    key.Rank == OwnerConsoleAttention.RecentTerminal, OwnerConsolePriorityText.Column(goal),
                    OwnerConsolePriorityText.Warning(goal, []))
                    { SortKey = key, StageWithoutQuestion = stage, IsPassedGateHeld = passedHeld };
                foreach (var line in stage.Split('\n'))
                    board.Add(row with { Stage = line, StageWithoutQuestion = line });
            }
        }
        else
        {
            _boardGoals = []; _ranked = []; _previousHolds.Clear();
            _goals = _goals.Values.Take(MaxActivityItems).ToDictionary(goal => goal.Id.Value);
            _roles = _goals.Values.SelectMany(goal => goal.Tasks).DistinctBy(task => task.Id.Value)
                .ToDictionary(task => task.Id.Value, task => task.RequiredRole);
        }
        cancellationToken.ThrowIfCancellationRequested();
        _metadata = metadata;
        _boardVersion = version is not null && version == boardMarker?.Invoke() ? version : null;
        DateTimeOffset? tick = null;
        try { tick = latestTick?.Invoke(); }
        catch { /* An unavailable tick must not prevent status refresh or produce a stale alarm. */ }
        return new(new(liveness.IsRunning(), board.Where(row => !row.Dimmed).Select(row => row.GoalId).Distinct().Count(), 0, 0, null, 0,
            board.Count(row => row.Dimmed && row.State == nameof(GoalStatus.Failed)), tick), [], board.ToImmutable(), [], Gate: gate);
    }

    internal OwnerConsoleViewModel WithDecisions(OwnerConsoleViewModel model,
        ImmutableArray<OwnerConsoleDecision> decisions, int hidden)
    {
        var questionGoals = decisions.Where(item => item.Kind != OwnerQuestionKind.PassedGateHold)
            .Select(item => item.GoalId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = model.Board.Select(row => row.IsForeign || row.IsDiagnostic ? row : row with
        {
            StageWithoutQuestion = row.StageWithoutQuestion ?? row.Stage,
            Stage = !row.Dimmed && !row.IsPassedGateHeld && questionGoals.Contains(row.GoalId) ? OwnerConsoleStageDescriber.OwnerQuestion :
                row.StageWithoutQuestion ?? row.Stage
        }).OrderBy(row => row.IsDiagnostic)
            .ThenBy(row => row.SortKey.WithOwnerQuestion(!row.IsForeign && questionGoals.Contains(row.GoalId)))
            .ThenBy(row => row.Project, StringComparer.OrdinalIgnoreCase).ThenBy(row => row.GoalId, StringComparer.Ordinal).ToImmutableArray();
        return model with { Board = rows, Decisions = decisions,
            Status = model.Status with { LiveDecisions = decisions.Length, HiddenQuestions = hidden } };
    }

    internal OwnerConsoleViewModel WithPanes(OwnerConsoleViewModel model, OwnerConsoleViewInputs inputs) =>
        OwnerConsolePaneRefresh.Apply(model, inputs, clock, _titles.Titles, gateProgress, latestTick, liveness, gateMotion);

    internal OwnerConsoleViewModel WithActivity(OwnerConsoleViewModel model, OwnerConsoleViewInputs inputs,
        IReadOnlyDictionary<OwnerConductEvent, OwnerActivityTestEvidence?>? evidence = null)
    {
        var planned = OwnerConsoleStopClassifier.Classify(inputs.RecentEvents);
        OwnerConductEvent[] observed;
        OwnerAttentionObservation[] attention;
        lock (_observationGate) { observed = _observed.ToArray(); attention = _attention.Snapshot(); }
        var activity = OwnerActivityNarrator.Narrate(inputs.RecentEvents.Concat(observed)
            .Select(item => OwnerGoalLifecycleEvent.WithRole(item, _roles)), Title,
            evidence is null ? null : item => evidence.GetValueOrDefault(item), attention, item =>
            {
                var matches = _goals.Values.Where(goal => item.GoalId is not null &&
                    goal.Id.Value.StartsWith(item.GoalId, StringComparison.OrdinalIgnoreCase)).ToArray();
                return matches.Length == 1 ? OwnerConsoleGoalDetailFormatter.Finding(matches[0], item) : null;
            }).ToImmutableArray();
        return model with { Activity = activity,
            Board = model.Board.Select(row => !row.IsForeign && !row.IsDiagnostic && _goals.TryGetValue(row.GoalId, out var goal)
                ? row with { Priority = OwnerConsolePriorityText.Column(goal),
                    PriorityWarning = OwnerConsolePriorityText.Warning(goal, inputs.RecentEvents) } : row).ToImmutableArray(),
            Status = model.Status with
            { LastEventAge = Age(inputs.LastConductEvent), PlannedStopReason = planned?.Reason,
                PlannedStopAt = planned?.At, LandedToday = inputs.LandedToday } };
    }

    private void Observe(OwnerConductEvent item)
    {
        _observed.Add(item);
        if (_observed.Count > MaxActivityItems) _observed.RemoveAt(0);
    }

    internal OwnerConductEvent EnrichEvent(OwnerConductEvent item) => OwnerGoalLifecycleEvent.WithRole(item, _roles);

    private string Title(string? id) => _titles.Title(id);

    internal static string Prefix(string id) => id[..Math.Min(8, id.Length)];

    private TimeSpan? Age(DateTimeOffset? timestamp) => timestamp is null ? null :
        TimeSpan.FromTicks(Math.Max(0, (clock.GetUtcNow() - timestamp.Value).Ticks));

    private static bool IsActive(GoalStatus status) => status is not
        (GoalStatus.Parked or GoalStatus.Completed or GoalStatus.Failed or GoalStatus.Cancelled or GoalStatus.Superseded);

    private static string Summary(string text)
    {
        var line = text.Split(['\r', '\n'], 2)[0];
        line = string.Join(" ", line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line;
    }
}
