using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleScreenController(IOwnerQuestionSource questions, IOwnerAnswerSubmitter answers,
    IOwnerConsoleDialogs dialogs, OwnerGoalDetailQuery goalDetail,
    IOwnerConsoleConductor conductor, IOwnerConsoleDigestReport digest,
    IOwnerDigestSummary summary, TimeProvider clock, IOwnerConsoleEpicSource? epics = null,
    AnswerIntentStatusTracker? answerTracking = null, OwnerQuestionResolutionReader? resolutions = null,
    IOwnerConsoleNotifier? notifier = null, OwnerConsoleNotificationSettings? notifications = null,
    Func<OwnerFleetViewModel>? fleet = null, Func<string, OwnerLandingsPage>? landings = null,
    OwnerConsoleSoundCommand? sound = null, OrchestratorWorkspace? workspace = null) : IDisposable
{
    private readonly OwnerEpicDetailQuery _epicDetail = new(epics, clock);
    private readonly OwnerFleetViewKeys _fleetKeys = new();
    private readonly OwnerGoalDetailQuery _goalDetail = goalDetail;
    private readonly OwnerConsoleLastGood<string, OwnerConsoleGoalDialog.Content> _goalLastGood = new();
    private readonly object _notificationGate = new();
    private readonly IOwnerConsoleNotifier _notifier = notifier ?? new NullOwnerConsoleNotifier();
    private readonly OwnerConsoleNotificationDetector _notifications = new(notifications ?? new(),
        () => sound?.Settings.Current ?? notifications ?? new());
    private readonly OwnerConsoleBellCommand _bell = new(sound);
    private OwnerConsoleBellTiers EffectiveBellTiers => _bell.Tiers;
    internal OwnerConsoleSoundCommand? Sound => sound;
    internal OrchestratorWorkspace? Workspace => workspace;
    private readonly AnswerIntentStatusTracker _answerTracking = answerTracking ?? new(answers.ReadStatusAsync, clock);
    private OwnerDecisionWorkflow? _decisionWorkflow;
    private OwnerDecisionWorkflow Decisions => _decisionWorkflow ??= new(questions, answers, dialogs, _answerTracking, resolutions, clock);
    public void Dispose() => _answerTracking.Dispose();
    internal event Action? ModelApplied;
    internal Task<OwnerConsoleEpicViewModel?> LoadEpicViewAsync(OwnerConsoleEpicWindow window,
        string? detailId, CancellationToken cancellationToken) =>
        _epicDetail.LoadAsync(window, detailId, cancellationToken);
    internal OwnerConsoleViewModel? Model { get; private set; }
    internal string? SelectedDecisionId { get; private set; }
    internal bool BellEnabled { get { lock (_notificationGate) return EffectiveBellTiers != OwnerConsoleBellTiers.None; } }
    internal string BellStatusText { get { lock (_notificationGate) return EffectiveBellTiers.StatusText; } }
    internal bool QuitRequested { get; private set; }
    private string? _requestedEpicId;
    private string? _requestedGateGoalId;
    private bool _settingsRequested;
    internal bool TakeSettingsRequested()
    { var requested = _settingsRequested; _settingsRequested = false; return requested; }
    internal string? TakeRequestedEpicId()
    { var id = _requestedEpicId; _requestedEpicId = null; return id; }
    internal string? TakeRequestedGateGoalId()
    { var id = _requestedGateGoalId; _requestedGateGoalId = null; return id; }
    internal async Task ShowActivityMeaningAsync(OwnerConsoleActivityItem item, OwnerConsoleScreenOperation? operation = null,
        CancellationToken cancellationToken = default)
    {
        if (item.Kind == "owner-question-resolved" && resolutions is not null)
        {
            OwnerQuestionResolution? resolution = null;
            if (!await RunDependencyAsync("question resolution", async stepToken =>
                resolution = await resolutions.ReadAsync(item, stepToken), operation, cancellationToken)) return;
            if (resolution is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var resolvedDetail = OwnerActivityDetailPresentation.Resolved(item, resolution, clock.LocalTimeZone);
                await dialogs.ShowStructuredAsync("What this means", resolvedDetail.Text, resolvedDetail.Structured);
                return;
            }
        }
        IReadOnlyList<OwnerConsoleDecision> live = [];
        if (!await RunDependencyAsync("activity decision state", async stepToken =>
        {
            var snapshot = await questions.ReadAsync(stepToken);
            live = Model?.Decisions.Where(decision => snapshot.Live.Contains(decision.ToQuestion())).ToArray() ?? [];
        }, operation, cancellationToken)) return;
        cancellationToken.ThrowIfCancellationRequested();
        var explanation = OwnerActivityExplanation.ExplainWithFields(item, live);
        await dialogs.ShowStructuredAsync("What this means", explanation.Text, explanation.Structured);
    }
    internal int SelectedIndex => Model is null ? -1 :
        Array.FindIndex(Model.Decisions.ToArray(), item => item.Id == SelectedDecisionId);
    internal void Apply(OwnerConsoleViewModel model)
    {
        IReadOnlyList<OwnerConsoleNotificationTier> tiers;
        lock (_notificationGate)
        {
            tiers = _notifications.Detect(Model, model, clock.GetUtcNow(), EffectiveBellTiers);
            var oldIndex = SelectedIndex;
            Model = model;
            if (!model.Decisions.Any(item => item.Id == SelectedDecisionId))
                SelectedDecisionId = model.Decisions.Length == 0 ? null :
                    model.Decisions[Math.Clamp(oldIndex, 0, model.Decisions.Length - 1)].Id;
        }
        foreach (var tier in tiers)
        {
            try { _notifier.Notify(tier); }
            catch { /* Sound failure must not break rendering. */ }
        }
        ModelApplied?.Invoke();
    }
    internal void SelectIndex(int index)
    {
        if (Model is { Decisions.Length: > 0 })
            SelectedDecisionId = Model.Decisions[Math.Clamp(index, 0, Model.Decisions.Length - 1)].Id;
    }

    internal async Task HandleKeyAsync(ConsoleKey key, char character = '\0', OwnerConsoleScreenOperation? operation = null,
        CancellationToken cancellationToken = default)
    {
        if (key == ConsoleKey.UpArrow) SelectIndex(SelectedIndex - 1);
        else if (key == ConsoleKey.DownArrow) SelectIndex(SelectedIndex + 1);
        else if (key == ConsoleKey.Enter && Selected() is { } detail)
            await ShowDecisionAsync(detail, operation, cancellationToken);
        else if (character == OwnerConsoleBindings.Accept.Key || character == OwnerConsoleBindings.Answer.Key) await Decisions.AnswerAsync(Selected(), character == OwnerConsoleBindings.Accept.Key, operation, cancellationToken);
        else if (character == OwnerConsoleBindings.CommandLineKey)
        {
            var command = await dialogs.PromptTextAsync("Command", OwnerConsoleKeyHints.CommandPrompt);
            if (command is not null) await RunCommandAsync(command, operation, cancellationToken);
        }
        else if (character == OwnerConsoleBindings.Quit.Key) QuitRequested = true;
    }

    private OwnerConsoleDecision? Selected() => Model?.Decisions.FirstOrDefault(item => item.Id == SelectedDecisionId);

    internal OwnerConsoleDecision? SelectedDecision => Selected();

    internal static bool AnswersInConsole(OwnerConsoleDecision decision) =>
        OwnerDecisionWorkflow.AnswersInConsole(decision);

    internal Task ShowHelpAsync() => dialogs.ShowTextAsync("Help", OwnerConsoleKeyHints.HelpText);
    internal Task ShowFieldsAsync(OwnerConsoleFieldSnapshot fields, string? selectedKey = null) =>
        dialogs.ShowFieldsAsync(fields, selectedKey);
    internal async Task ShowResolutionAsync(OwnerQuestionResolution resolution) =>
        await dialogs.ShowStructuredAsync("Question resolution", ResolutionText(resolution), new(OwnerQuestionResolutionText.Fields(resolution)));

    internal Task ShowDecisionAsync(OwnerConsoleDecision decision) => ShowDecisionAsync(decision, null, CancellationToken.None);

    internal string ResolutionText(OwnerQuestionResolution resolution) =>
        OwnerQuestionResolutionText.Build(resolution, clock.LocalTimeZone);

    private Task ShowDecisionAsync(OwnerConsoleDecision decision, OwnerConsoleScreenOperation? operation,
        CancellationToken cancellationToken) =>
        Decisions.ShowDecisionAsync(decision, () => Model, handler => ModelApplied += handler,
            handler => ModelApplied -= handler, operation, cancellationToken);

    internal Task<OwnerGoalChangeSummary?> LoadGoalSummaryAsync(string goalId, CancellationToken token) =>
        _goalDetail.LoadSummaryAsync(goalId, token);

    internal async Task ShowGoalDetailAsync(string goalId, OwnerConsoleScreenOperation? operation = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var row = Model?.Board.FirstOrDefault(item => !item.IsForeign && !item.IsDiagnostic && item.GoalId == goalId);
        var retained = _goalLastGood.TryGet(goalId, out var prior);
        using var dialog = new OwnerConsoleGoalDialog(this, retained ? prior : OwnerConsoleGoalDetailLoading.Placeholder(goalId, row),
            token => ComposeGoalAsync(goalId, token),
            decision => ShowDecisionAsync(decision, operation, cancellationToken), dialogs.ShowTextAsync, cancellationToken,
            loadOnStart: true, initialContentLoaded: retained, remember: content => _goalLastGood.Remember(goalId, content),
            structuredText: async (title, text, fields) => { await dialogs.ShowStructuredAsync(title, text, fields); });
        await dialogs.ShowGoalAsync(dialog);
        _requestedEpicId = dialog.RequestedEpicId;
        _requestedGateGoalId = dialog.RequestedGateGoalId;
    }

    private async Task<OwnerConsoleGoalDialog.Content?> ComposeGoalAsync(string goalId, CancellationToken token)
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

    internal async Task RunCommandAsync(string raw, OwnerConsoleScreenOperation? operation = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var line = raw.Trim().TrimStart(OwnerConsoleBindings.CommandLineKey).Trim();
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;
        var output = new OwnerConsoleCapturedOutput();
        string? goalId = null;
        OwnerFleetViewModel? fleetModel = null;
        OwnerLandingsPage? landingsPage = null;
        var control = new OwnerConsoleControlCommands(conductor, digest, output, clock);
        if (!await RunDependencyAsync(line, ExecuteAsync, operation, cancellationToken)) return;
        cancellationToken.ThrowIfCancellationRequested();
        if (_settingsRequested) return;
        if (goalId is not null) { await ShowGoalDetailAsync(goalId, operation, cancellationToken); return; }
        if (fleetModel is not null) { await dialogs.ShowFleetAsync(_fleetKeys, fleetModel, OwnerConsoleDialogWidth.ForDiagram(dialogs.TextWidth)); return; }
        if (landingsPage is not null) { await ShowLandingsAsync(landingsPage, operation, cancellationToken); return; }
        if ((output.Structured ?? control.Fields) is { } commandFields)
            await dialogs.ShowStructuredAsync("Command result", output.Text, commandFields);
        else await dialogs.ShowTextAsync("Command result", output.Text);

        async Task ExecuteAsync(CancellationToken stepToken)
        {
            switch (parts[0].ToLowerInvariant())
            {
                case "conductor": control.HandleConductor(line); break;
                case "digest":
                    if (parts.Length != 1) output.WriteLine("usage: digest");
                    else
                    {
                        var result = summary.ReadStructuredSummary();
                        foreach (var item in result.Lines.Take(5)) output.WriteLine(item);
                        output.Structured = result.Fields;
                    }
                    break;
                case "metrics": control.HandleMetrics(line); break;
                case "fleet":
                    if (parts.Length != 1) output.WriteLine("usage: fleet");
                    else if (fleet is null) output.WriteLine("fleet view unavailable in this session");
                    else fleetModel = fleet();
                    break;
                case "landings":
                    if (parts.Length > 2) output.WriteLine(OwnerLandingsQuery.Usage);
                    else if (landings is null) output.WriteLine("landings view unavailable in this session");
                    else landingsPage = landings(parts.Length == 2 ? parts[1] : "");
                    break;
                case "sound":
                    if (sound is null) output.WriteLine("sound unavailable in this session");
                    else sound.Run(line, output);
                    break;
                case OwnerConsoleBindings.SettingsCommand:
                    if (parts.Length != 1) output.WriteLine("usage: settings");
                    else if (sound is null) output.WriteLine("settings unavailable in this session");
                    else _settingsRequested = true;
                    break;
                case "bell":
                    lock (_notificationGate) _bell.Run(parts.Skip(1).ToArray(), output);
                    break;
                case var verb when verb == OwnerConsoleBindings.Goal.Command:
                    if (parts.Length != 2) { output.WriteLine("usage: " + OwnerConsoleBindings.Goal.Usage); break; }
                    goalId = await _goalDetail.ResolveGoalIdAsync(parts[1], output, stepToken);
                    break;
                default:
                    output.WriteLine("unknown command");
                    output.WriteLine("valid commands: " + OwnerConsoleKeyHints.CommandPrompt);
                    break;
            }
        }
    }

    private async Task ShowLandingsAsync(OwnerLandingsPage page, OwnerConsoleScreenOperation? operation,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chosen = page.Fields is { } fields
                ? await dialogs.ShowStructuredAsync("Landings", page.Text, fields, page.ChoiceLines)
                : await dialogs.ShowPageAsync("Landings", page.Text, page.ChoiceLines);
            if (chosen is not { } index || index < 0 || index >= page.ChoiceGoalIds.Count) return;
            await ShowGoalDetailAsync(page.ChoiceGoalIds[index], operation, cancellationToken);
            if (_requestedEpicId is not null || _requestedGateGoalId is not null) return;
        }
    }

    private static async Task<bool> RunDependencyAsync(string label, Func<CancellationToken, Task> action,
        OwnerConsoleScreenOperation? operation, CancellationToken cancellationToken)
    {
        if (operation is not null) return await operation.RunAsync(label, action, cancellationToken);
        await action(cancellationToken);
        return true;
    }
}
