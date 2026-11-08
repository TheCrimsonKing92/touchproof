using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleSession(
    IOrchestratorStateQueries state,
    IOwnerQuestionSource questions,
    IOwnerAnswerSubmitter answers,
    IConductorLiveness liveness,
    IOwnerDigestSummary digest,
    IGoalEventTail eventTail,
    IOwnerConsoleOutput output,
    TimeProvider clock,
    IOwnerConsoleConductor? conductor = null,
    IOwnerConsoleDigestReport? digestReport = null,
    ChangeStreamFileReader? changes = null,
    AnswerIntentStatusTracker? answerTracking = null,
    Func<OwnerFleetViewModel>? fleet = null,
    Func<int?>? terminalWidth = null,
    string? projectLabel = null) : IDisposable
{
    private readonly AnswerIntentStatusTracker _answerTracking = answerTracking ?? new(answers.ReadStatusAsync, clock);
    public void Dispose() => _answerTracking.Dispose();
    private readonly OwnerConsoleControlCommands _control = new(conductor, digestReport, output, clock);
    private readonly Dictionary<string, int> _numbers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, OwnerQuestion> _open = [];
    private readonly HashSet<int> _retired = [];
    private int _hiddenCount;
    private int _nextNumber = 1;
    private bool _bell = true;
    private DateTimeOffset? _lastConductEvent;
    private long _coveredSequence;
    private readonly Dictionary<string, OwnerGoalCard> _board = new(StringComparer.OrdinalIgnoreCase);

    private OwnerConsoleSessionPrinter? _printer;
    private OwnerConsoleCommandHandler? _commands;

    private OwnerConsoleSessionPrinter Printer => _printer ??= new(
        state, liveness, digest, eventTail, output, clock, projectLabel, _open, _board,
        () => _bell, () => _hiddenCount, () => _lastConductEvent, LoadActiveAsync, Announce);

    private OwnerConsoleCommandHandler Commands => _commands ??= new(
        answers, _answerTracking, _control, Printer, output, fleet, terminalWidth,
        _open, _retired, value => _bell = value, RefreshQuestionsAsync, Announce);

    internal async Task StartAsync(DateTimeOffset? lastActivity, CancellationToken cancellationToken)
    {
        _lastConductEvent = lastActivity;
        _coveredSequence = changes?.Reanchor() ?? 0;
        foreach (var line in digest.ReadSummaryLines().Take(5))
            Announce(line);
        var metadata = await RefreshQuestionsAsync(cancellationToken);
        await Printer.PrintHeaderAsync(metadata, cancellationToken);
        await Printer.PrintBoardAsync(metadata, cancellationToken);
        Announce("Type help for commands.");
    }

    internal async Task HandleEventAsync(OwnerConductEvent item, CancellationToken cancellationToken)
    {
        _lastConductEvent = item.Timestamp;
        if (changes is not null)
        {
            await ApplyChangesAsync(cancellationToken);
            return;
        }
        var metadata = await RefreshQuestionsAsync(cancellationToken);
        if (item.EventKind is "watch-transition" or "acceptance" or "loop-relaunch" or "goal-escalation")
            await Printer.PrintBoardAsync(metadata, cancellationToken);
    }

    private async Task ApplyChangesAsync(CancellationToken cancellationToken)
    {
        var records = changes!.ReadAvailable(out var discontinuity);
        if (discontinuity is not null)
        {
            await RefreshAllAsync(cancellationToken);
            return;
        }
        foreach (var record in records)
        {
            if (record.Sequence <= _coveredSequence) continue;
            // The reader consumes the batch before state reads complete. A failed read can
            // leave session coverage behind even when the reader sees contiguous sequences.
            if (record.Sequence > _coveredSequence + 1)
            {
                await RefreshAllAsync(cancellationToken);
                return;
            }
            if (record.ChangeKind == ChangeStreamRecord.OwnerDecisionRaised)
                await RefreshQuestionsAsync(cancellationToken);
            else
            {
                if (record.ChangeKind is ChangeStreamRecord.OwnerDecisionAnswered or ChangeStreamRecord.OwnerDecisionRetired)
                    await RefreshQuestionsAsync(cancellationToken);
                var id = record.GoalId;
                if (id.Length != 32)
                {
                    var matches = _board.Keys.Where(key => key.StartsWith(id, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (matches.Length != 1)
                    {
                        await RefreshAllAsync(cancellationToken);
                        return;
                    }
                    id = matches[0];
                }
                var kernel = await state.LoadGoalsAsync([new GoalId(id)], cancellationToken);
                var goal = kernel.Goals.SingleOrDefault(goal => goal.Id.Value.Equals(id, StringComparison.OrdinalIgnoreCase));
                if (goal is null || !IsActive(goal.Status))
                {
                    _board.Remove(id);
                    Announce($"{id[..Math.Min(8, id.Length)]} | removed from board");
                }
                else
                {
                    var card = ToCard(goal);
                    _board[id] = card;
                    Printer.PrintBoardRow(card);
                }
            }
            _coveredSequence = record.Sequence;
        }
    }

    private async Task RefreshAllAsync(CancellationToken cancellationToken)
    {
        var anchor = changes!.Reanchor();
        var metadata = await RefreshQuestionsAsync(cancellationToken);
        await Printer.PrintBoardAsync(metadata, cancellationToken);
        _coveredSequence = anchor;
    }

    internal Task<bool> HandleCommandAsync(string raw, CancellationToken cancellationToken) =>
        Commands.HandleCommandAsync(raw, cancellationToken);

    private async Task<IReadOnlyList<GoalSummary>?> RefreshQuestionsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<GoalSummary>? metadata = null;
        OwnerQuestionSnapshot snapshot;
        if (questions is OwnerQuestionReadModel model)
        {
            metadata = await state.ListGoalMetadataAsync(cancellationToken);
            snapshot = await model.ReadAsync(metadata, cancellationToken);
        }
        else
            snapshot = await questions.ReadAsync(cancellationToken);
        var current = snapshot.Live;
        _hiddenCount = snapshot.Hidden.Count;
        var ids = current.Select(item => item.ItemId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _open.Where(pair => !ids.Contains(pair.Value.ItemId)).ToArray())
        { _open.Remove(pair.Key); _retired.Add(pair.Key); }
        foreach (var question in current)
        {
            if (!_numbers.TryGetValue(question.ItemId, out var number))
            { number = _nextNumber++; _numbers.Add(question.ItemId, number); Printer.PrintQuestion(number, question); }
            if (!_retired.Contains(number)) _open[number] = question;
        }
        return metadata;
    }

    private async Task<IReadOnlyList<OwnerGoalCard>> LoadActiveAsync(
        IReadOnlyList<GoalSummary>? metadata, CancellationToken cancellationToken)
    {
        metadata ??= await state.ListGoalMetadataAsync(cancellationToken);
        var ids = metadata.Where(item => Enum.TryParse<GoalStatus>(item.Status, true, out var status) && IsActive(status))
            .Select(item => new GoalId(item.Id)).ToArray();
        if (ids.Length == 0) return [];
        var kernel = await state.LoadGoalsAsync(ids, cancellationToken);
        return kernel.Goals.Where(goal => IsActive(goal.Status)).Select(ToCard).ToArray();
    }

    private static OwnerGoalCard ToCard(Goal goal)
    {
        var task = goal.Tasks.FirstOrDefault(item => item.Status != WorkTaskStatus.Completed) ?? goal.Tasks.LastOrDefault();
        return new OwnerGoalCard(goal.Id.Value, OwnerGoalTitle.From(goal.Objective), goal.Status, task?.RequiredRole,
            goal.Timeline.OrderByDescending(item => item.OccurredAt).FirstOrDefault()?.OccurredAt);
    }

    private static bool IsActive(GoalStatus status) => status is not
        (GoalStatus.Parked or GoalStatus.Completed or GoalStatus.Failed or GoalStatus.Cancelled or GoalStatus.Superseded);

    private void Announce(string line) => output.WriteLine(ConsoleAnnouncementFormatter.Format(clock, line));
}
