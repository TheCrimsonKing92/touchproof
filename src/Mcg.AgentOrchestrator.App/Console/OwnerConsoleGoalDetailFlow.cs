using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleGoalDetailFlow(OwnerConsoleScreenController controller,
    OwnerGoalDetailQuery goalDetail, OwnerConsoleLastGood<string, OwnerConsoleGoalDialog.Content> goalLastGood,
    IOwnerConsoleDialogs dialogs, TimeProvider clock, IOwnerConsoleEpicSource? epics,
    OwnerQuestionResolutionReader? resolutions, Func<OwnerConsoleViewModel?> model,
    Func<OwnerConsoleDecision, OwnerConsoleScreenOperation?, CancellationToken, Task> showDecision,
    Action<string?, string?> requestNavigation)
{
    private readonly OwnerGoalDetailQuery _goalDetail = goalDetail;
    private readonly OwnerConsoleLastGood<string, OwnerConsoleGoalDialog.Content> _goalLastGood = goalLastGood;
    private OwnerConsoleViewModel? Model => model();

    internal Task<OwnerGoalChangeSummary?> LoadGoalSummaryAsync(string goalId, CancellationToken token) =>
        _goalDetail.LoadSummaryAsync(goalId, token);

    internal async Task ShowGoalDetailAsync(string goalId, OwnerConsoleScreenOperation? operation = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var row = Model?.Board.FirstOrDefault(item => !item.IsForeign && !item.IsDiagnostic && item.GoalId == goalId);
        var retained = _goalLastGood.TryGet(goalId, out var prior);
        using var dialog = new OwnerConsoleGoalDialog(controller, retained ? prior : OwnerConsoleGoalDetailLoading.Placeholder(goalId, row),
            token => ComposeGoalAsync(goalId, token),
            decision => showDecision(decision, operation, cancellationToken), dialogs.ShowTextAsync, cancellationToken,
            loadOnStart: true, initialContentLoaded: retained, remember: content => _goalLastGood.Remember(goalId, content),
            structuredText: async (title, text, fields) => { await dialogs.ShowStructuredAsync(title, text, fields); });
        await dialogs.ShowGoalAsync(dialog);
        requestNavigation(dialog.RequestedEpicId, dialog.RequestedGateGoalId);
    }

    internal async Task<OwnerConsoleGoalDialog.Content?> ComposeGoalAsync(string goalId, CancellationToken token)
    {
        var output = new OwnerConsoleCapturedOutput();
        var goal = await OwnerConsoleGoalDetailFormatter.ComposeAsync(_goalDetail, goalId, output, token,
            clock.LocalTimeZone, () => Model?.Gate,
            Model?.Board.FirstOrDefault(item => !item.IsForeign && !item.IsDiagnostic && item.GoalId == goalId)?.PriorityWarning).ConfigureAwait(false);
        if (goal is null) return null;
        var resolved = resolutions is null ? [] : await resolutions.ListForGoalAsync(goalId, token).ConfigureAwait(false);
        var lines = output.Text.Replace("\r", "").TrimEnd('\n').Split('\n').ToList();
        var choiceLines = new List<int>();
        if (resolved.Count > 0)
        {
            lines.Add("Resolved questions (Enter to open):");
            foreach (var question in resolved)
            { choiceLines.Add(lines.Count); lines.Add(OwnerQuestionResolutionText.Summary(question, clock.LocalTimeZone)); }
        }
        var openQuestion = Model?.Decisions.Where(item => item.GoalId == goalId).OrderBy(item => item.Number).FirstOrDefault();
        var epicId = epics is null ? null : await epics.FindGoalEpicAsync(goalId, token).ConfigureAwait(false);
        var failure = goal.RetainedAcceptanceFailure is { } recorded ? OwnerConsoleGoalLanding.FailureText(recorded, clock.LocalTimeZone) : null;
        var gateGoalId = Model?.Gate?.ForMember(goalId) is not null ? goalId : null;
        return new(string.Join(Environment.NewLine, lines), choiceLines, resolved, openQuestion, failure, epicId, gateGoalId,
            new(output.Structured?.Detail, choiceLines.Select((line, index) => (line, fields: OwnerQuestionResolutionText.Fields(resolved[index])))
                .ToDictionary(item => item.line, item => item.fields)),
            goal.RetainedAcceptanceFailure is { } failed ? new(OwnerConsoleGoalLanding.FailureFields(failed)) : null);
    }
}
