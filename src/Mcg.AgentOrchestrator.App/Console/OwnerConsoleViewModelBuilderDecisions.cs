using System.Collections.Immutable;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed partial class OwnerConsoleViewModelBuilder
{
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
}
