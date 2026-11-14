using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleSessionPrinter(
    IOrchestratorStateQueries state,
    IConductorLiveness liveness,
    IOwnerDigestSummary digest,
    IGoalEventTail eventTail,
    IOwnerConsoleOutput output,
    TimeProvider clock,
    string? projectLabel,
    IReadOnlyDictionary<int, OwnerQuestion> open,
    Dictionary<string, OwnerGoalCard> board,
    Func<bool> bell,
    Func<int> hiddenCount,
    Func<DateTimeOffset?> lastConductEvent,
    Func<IReadOnlyList<GoalSummary>?, CancellationToken, Task<IReadOnlyList<OwnerGoalCard>>> loadActive,
    Action<string> announce)
{
    private ConductorLiveness _lastStableLiveness = ConductorLiveness.NotRunning;

    internal void PrintQuestion(int number, OwnerQuestion question)
    {
        if (bell()) output.Write("\a");
        var fields = new[]
        {
            question.BlastRadius is null ? null : $"blast radius: {question.BlastRadius}",
            question.Confidence is null ? null : $"confidence: {question.Confidence}",
            question.ProposedDefault is null ? null : $"default: {question.ProposedDefault}"
        }.Where(field => field is not null);
        var stewardGuidance = question.Kind == OwnerQuestionKind.StewardHold
            ? $" | view only | CLI: touchproof.cmd retry --goal {question.GoalId} <task-number> <message> or touchproof.cmd adjudicate --goal {question.GoalId} <task-number> <close|reopen-regate|route> --text-file <path> --evidence <ref>"
            : string.Empty;
        announce($"[{number}] {question.GoalId[..Math.Min(8, question.GoalId.Length)]} {question.Text}{(fields.Any() ? " | " + string.Join(" | ", fields) : "")}{stewardGuidance}");
    }

    internal async Task PrintHeaderAsync(IReadOnlyList<GoalSummary>? metadata, CancellationToken cancellationToken)
    {
        var goals = await loadActive(metadata, cancellationToken);
        var lastEvent = lastConductEvent();
        var age = lastEvent is null ? "unknown" : Age(lastEvent.Value);
        var count = hiddenCount();
        var hidden = count > 0 ? $" | hidden: {count}" : string.Empty;
        var reading = liveness.IsRunning();
        if (reading != ConductorLiveness.Unknown) _lastStableLiveness = reading;
        announce((projectLabel is null ? "" : $"project: {projectLabel} | ") +
            $"conductor: {(_lastStableLiveness == ConductorLiveness.Running ? "running" : "stopped")} | active goals: {goals.Count} | owner questions: {open.Count}{hidden} | last event: {age}");
    }

    internal async Task PrintBoardAsync(IReadOnlyList<GoalSummary>? metadata, CancellationToken cancellationToken)
    {
        var goals = await loadActive(metadata, cancellationToken);
        board.Clear();
        foreach (var goal in goals) board[goal.Id] = goal;
        announce($"board | active goals: {goals.Count} | owner questions: {open.Count}");
        foreach (var goal in goals)
            PrintBoardRow(goal);
    }

    internal void PrintBoardRow(OwnerGoalCard goal) =>
        output.WriteLine($"{goal.Id[..Math.Min(8, goal.Id.Length)]} | {goal.Title.Replace('\r', ' ').Replace('\n', ' ')} | {goal.State} | {goal.CurrentRole?.ToString() ?? "-"} | {Age(goal.LastEvent)}");

    internal async Task PrintGoalAsync(string prefix, CancellationToken cancellationToken)
    {
        var metadata = await state.ListGoalMetadataAsync(cancellationToken);
        var matches = metadata.Where(item => item.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0) { announce($"no goal matches '{prefix}'"); return; }
        if (matches.Length > 1) { announce($"ambiguous goal: {string.Join(", ", matches.Select(item => item.Id[..8]))}"); return; }
        var kernel = await state.LoadGoalsAsync([new GoalId(matches[0].Id)], cancellationToken);
        var goal = kernel.Goals.SingleOrDefault();
        if (goal is null) { announce("goal state unavailable"); return; }
        announce($"{goal.Id.Value} | {OwnerGoalTitle.From(goal.Objective)} | {goal.Status}");
        foreach (var line in eventTail.ReadLast(goal.Id.Value, 15)) output.WriteLine(line);
    }

    internal async Task PrintDigestAsync(IReadOnlyList<GoalSummary>? metadata, CancellationToken cancellationToken)
    {
        foreach (var line in digest.ReadSummaryLines().Take(5)) announce(line);
        await PrintHeaderAsync(metadata, cancellationToken);
        await PrintBoardAsync(metadata, cancellationToken);
    }

    private string Age(DateTimeOffset? timestamp)
    {
        if (timestamp is null) return "unknown";
        var span = clock.GetUtcNow() - timestamp.Value;
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        return span.TotalMinutes < 1 ? $"{span.TotalSeconds:0}s" : span.TotalHours < 1 ? $"{span.TotalMinutes:0}m" : $"{span.TotalHours:0}h";
    }
}
