using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleScreenController(IOwnerQuestionSource questions, IOwnerAnswerSubmitter answers,
    IOwnerConsoleDialogs dialogs, OwnerGoalDetailQuery goalDetail, IOwnerConsoleConductor conductor,
    IOwnerConsoleDigestReport digest, IOwnerDigestSummary summary, TimeProvider clock, IOwnerConsoleEpicSource? epics = null,
    AnswerIntentStatusTracker? answerTracking = null, OwnerQuestionResolutionReader? resolutions = null,
    IOwnerConsoleNotifier? notifier = null, OwnerConsoleNotificationSettings? notifications = null,
    Func<OwnerFleetViewModel>? fleet = null, Func<string, OwnerLandingsPage>? landings = null,
    OwnerConsoleSoundCommand? sound = null, string? conductorPolicyPath = null, OrchestratorWorkspace? workspace = null, OwnerConsoleAgentsSettingsTab? agentsSettings = null) : IDisposable
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
    internal OwnerConsoleAgentsSettingsTab? AgentsSettings => agentsSettings;
    internal string? ConductorPolicyPath => conductorPolicyPath;
    internal OrchestratorWorkspace? Workspace => workspace;
    private readonly AnswerIntentStatusTracker _answerTracking = answerTracking ?? new(answers.ReadStatusAsync, clock);
    private OwnerDecisionWorkflow? _decisionWorkflow;
    private OwnerConsoleCommandDispatch? _commandDispatch;
    private OwnerConsoleGoalDetailFlow? _goalDetailFlow;
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
            if (!await OwnerConsoleCommandDispatch.RunDependencyAsync("question resolution", async stepToken =>
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
        if (!await OwnerConsoleCommandDispatch.RunDependencyAsync("activity decision state", async stepToken =>
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
        else if (key == ConsoleKey.Enter && SelectedDecision is { } detail)
            await ShowDecisionAsync(detail, operation, cancellationToken);
        else if (character == OwnerConsoleBindings.Accept.Key || character == OwnerConsoleBindings.Answer.Key) await Decisions.AnswerAsync(SelectedDecision, character == OwnerConsoleBindings.Accept.Key, operation, cancellationToken);
        else if (character == OwnerConsoleBindings.CommandLineKey)
        {
            var command = await dialogs.PromptTextAsync("Command", OwnerConsoleKeyHints.CommandPrompt);
            if (command is not null) await RunCommandAsync(command, operation, cancellationToken);
        }
        else if (character == OwnerConsoleBindings.Quit.Key) QuitRequested = true;
    }

    internal OwnerConsoleDecision? SelectedDecision => Model?.Decisions.FirstOrDefault(item => item.Id == SelectedDecisionId);

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
        (_goalDetailFlow ??= NewGoalDetailFlow()).LoadGoalSummaryAsync(goalId, token);

    internal Task ShowGoalDetailAsync(string goalId, OwnerConsoleScreenOperation? operation = null,
        CancellationToken cancellationToken = default) =>
        (_goalDetailFlow ??= NewGoalDetailFlow()).ShowGoalDetailAsync(goalId, operation, cancellationToken);

    private Task<OwnerConsoleGoalDialog.Content?> ComposeGoalAsync(string goalId, CancellationToken token) =>
        (_goalDetailFlow ??= NewGoalDetailFlow()).ComposeGoalAsync(goalId, token);

    internal Task RunCommandAsync(string raw, OwnerConsoleScreenOperation? operation = null,
        CancellationToken cancellationToken = default) =>
        (_commandDispatch ??= NewCommandDispatch()).RunAsync(raw, operation, cancellationToken);

    private OwnerConsoleGoalDetailFlow NewGoalDetailFlow() => new(this, _goalDetail, _goalLastGood, dialogs, clock,
        epics, resolutions, () => Model, ShowDecisionAsync,
        (epicId, gateGoalId) => { _requestedEpicId = epicId; _requestedGateGoalId = gateGoalId; });

    private OwnerConsoleCommandDispatch NewCommandDispatch() => new(dialogs, conductor, digest, summary, clock,
        fleet, landings, sound, _goalDetail, _fleetKeys, _notificationGate, _bell, () => _settingsRequested,
        () => _settingsRequested = true, () => _requestedEpicId, () => _requestedGateGoalId, ShowGoalDetailAsync);
}
