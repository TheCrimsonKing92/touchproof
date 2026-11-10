using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using GuiApplication = Terminal.Gui.App.Application;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleFullScreenHost
{
    internal static Task<int> RunAsync(OrchestratorWorkspace workspace, CancellationToken cancellationToken,
        string? inputTracePath = null, OwnerConsoleHostRowOwner? owners = null, OwnerConsoleBoardSource? boardSource = null) =>
        OwnerConsoleFullScreenHostStartup.RunAsync(workspace, cancellationToken, inputTracePath, owners, boardSource);

    internal static Task<(OwnerConsoleViewModel Model, List<OwnerConductEvent> Recent)> BuildInitialViewModelAsync(
        OwnerConsoleViewModelBuilder builder, string conductLogPath, DateTimeOffset opened,
        DateTimeOffset? last, CancellationToken token) =>
        OwnerConsoleFullScreenHostStartup.BuildInitialViewModelAsync(builder, conductLogPath, opened, last, token);

    internal static async Task<int> RunCoreAsync(OrchestratorWorkspace workspace, CancellationToken cancellationToken,
        OwnerConsoleInputDiagnostics? diagnostics, OwnerConsoleHostRowOwner? owners, OwnerConsoleBoardSource? boardSource)
    {
        var clock = TimeProvider.System;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = linked.Token;
        IConductEventSource? events = null;
        OwnerConsoleActivityLoader? activityLoader = null;
        Task? fill = null;
        Task<OwnerConsoleViewModel>? initialBoardRead = null;
        OwnerConsoleStartupLoader? startup = null;
        OwnerRefreshCoordinator? refresh = null;
        SqliteStoreChangeObservation? stateChanges = null;
        SqliteStoreChangeObservation? questionChanges = null;
        SqliteStoreChangeObservation? portfolioChanges = null;
        using IApplication app = OwnerConsoleFrameOutputInstaller.CreateApplication();
        using var backoff = new OwnerConsoleLoopBackoff(clock,
            rate => GuiApplication.MaximumIterationsPerSecond = (ushort)rate, GuiApplication.MaximumIterationsPerSecond);
        ITimer? idleTimer = null;
        IKeyboard? keyboard = null;
        IMouse? mouse = null;
        void OnKey(object? sender, Key key) => backoff.OnInput();
        void OnMouse(object? sender, Mouse item) => backoff.OnInput();
        void OnDraw(object? sender, EventArgs args) => backoff.OnRender();
        OwnerConsoleFullScreenView? view = null;
        Task? reader = null;
        Task periodicRefresh = Task.CompletedTask;
        Task streamRefresh = Task.CompletedTask;
        OwnerConsoleRefreshCoalescer? coalescer = null;
        var timings = new OwnerConsoleRefreshTimings(clock);
        var timingsFile = new OwnerConsoleRefreshTimingsFile(
            Path.Combine(workspace.LogDirectory, "owner-console-refresh-timings.txt"), clock, Environment.ProcessId);
        void Post(string phase, Action action)
        {
            Action render = () => { backoff.OnRender(); action(); };
            app.Invoke(diagnostics?.ObservePost(phase, render) ?? render);
        }
        try
        {
            IOrchestratorStateQueries state = new EmptyOwnerConsoleStateQueries(workspace.SqliteStatePath);
            stateChanges = new(workspace.SqliteStatePath);
            questionChanges = new(Path.Combine(workspace.OrchestratorDirectory, "collaboration-items.db"));
            portfolioChanges = new(workspace.PortfolioStorePath);
            string? QuestionVersion()
            {
                var stateVersion = stateChanges.Observe();
                var questionVersion = questionChanges.Observe();
                return stateVersion is not null && questionVersion is not null ? stateVersion + "/" + questionVersion : null;
            }
            var refreshFilter = new OwnerConsoleEventRefreshFilter();
            var snapshots = new OwnerConsoleRefreshGoalSnapshots(state);
            // The inner SQLite snapshot is reusable; onboarding files/registry remain conservatively read.
            // Do not join a board generation whose state token may predate a question read.
            IOwnerQuestionSource questions = new SerializedOwnerQuestionSource(OwnerQuestionReadModelFactory.Create(
                state, workspace.OrchestratorDirectory, changeMarker: QuestionVersion));
            if (boardSource is not null)
                questions = new OnboardingQuestionSource(questions, boardSource.InScopeProjects, clock,
                    () => coalescer?.Request(null, OwnerConsoleRefreshScope.All), boardSource.HasLandedAsync);
            var cohortEvidence = new OwnerActivityEvidenceReader(Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db"));
            var gateEvidence = new OwnerGateFailureEvidence(workspace.OrchestratorDirectory);
            var jointEvidence = new OwnerJointGateEvidenceStore(new OwnerJointGateFailureEvidence(workspace.OrchestratorDirectory).Observe, clock);
            var evidence = new OwnerActivityEvidenceLoader(item => item.EventKind == "acceptance-cohort"
                ? jointEvidence.Read(item, cohortEvidence.Read(item)) : gateEvidence.Read(item) ?? jointEvidence.Read(item));
            var conductTail = new OwnerConsoleJsonlTail(workspace.ConductEventsLogPath);
            var durationPath = AcceptanceLaneDurationStore.ResolveStorePath(workspace.RootDirectory);
            var durationTail = new OwnerConsoleJsonlTail(durationPath);
            var gateMotion = new OwnerGateMotionReader(workspace.ConductEventsLogPath, clock, conductTail);
            var latestTick = new OwnerConsoleLatestTickReader(conductTail, workspace.RunEventStorePath);
            var gateProgress = new OwnerConsoleGateProgressLoader(workspace.ConductEventsLogPath,
                Path.Combine(workspace.RootDirectory, "config", "acceptance-manifest.json"),
                durationPath, clock,
                executorConfigPath: RemoteLaneExecutorConfiguration.ResolveStorePath(workspace.RootDirectory),
                conductTail: conductTail, durationTail: durationTail, owners: owners);
            var changes = new ChangeStreamFileReader(Path.Combine(workspace.LogDirectory, Orchestration.ChangeStreamWriter.FileName));
            var builder = new OwnerConsoleViewModelBuilder(state, questions,
                new ConductorLeaseLiveness(workspace.OrchestratorDirectory), new PortfolioGoalEpicLookup(workspace.PortfolioStorePath), clock,
                null, id => timings.Time("gate-motion", () => gateMotion.Describe(id)),
                latestTick: () => timings.Time("latest-tick", latestTick.Read),
                gateProgress: titles => timings.Time("gate-progress", () => gateProgress.Load(titles)), snapshots: snapshots,
                candidateGate: goal =>
                {
                    var head = GitCli.Run(workspace.ExecutionDirectory, "rev-parse", "--verify",
                        $"refs/heads/{GoalWorktrees.BranchName(goal.Id)}");
                    return (Candidate: head.ExitCode == 0 ? head.Output.Trim() : null,
                        Attempts: GoalOperationJournal.Read(workspace.ExecutionDirectory, goal.Id).Entries);
                }, boardMarker: stateChanges.Observe, epicMarker: portfolioChanges.Observe);
            activityLoader = new(workspace.ConductEventsLogPath, workspace.GoalLifecycleEventsDirectory, clock);
            refresh = new(clock, token, builder, boardSource, snapshots, timings, refreshFilter, diagnostics,
                activityLoader.ReadNew, new OwnerConsoleLandedTodayScan(workspace.GoalLifecycleEventsDirectory, clock).Current,
                timingsFile.Write, action => app.Invoke(() => { backoff.OnRender(); action(); }), Post, () => view!, evidence);
            coalescer = new((items, scope) => refresh.RefreshAsync(items, scope: scope));

            refresh.HoldForStartup();
            try
            {
                // Keep Init, initial Render and Run on this host thread. Metadata is
                // read on the pool before GUI initialization, with the existing bound.
                initialBoardRead = Task.Run(async () =>
                {
                    var own = refresh.TrackBoard(await builder.BuildBoardAsync(token));
                    return boardSource is null ? own : await boardSource.ComposeAsync(own, token);
                }, token);
                var board = OwnerConsoleStartupLoader.LoadingBoard(initialBoardRead
                    .WaitAsync(OwnerConsoleLoopOptions.Default.OperationBound, clock, token).GetAwaiter().GetResult());
                app.Init();
                keyboard = app.Keyboard;
                mouse = app.Mouse;
                if (keyboard is not null) keyboard.KeyDown += OnKey;
                if (mouse is not null) mouse.MouseEvent += OnMouse;
                app.LayoutAndDrawComplete += OnDraw;
                backoff.OnRender();
                idleTimer = clock.CreateTimer(_ => backoff.Tick(), null,
                    TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));
                var settingsPath = Path.Combine(workspace.OrchestratorDirectory, "owner-console-settings.json");
                var settings = new OwnerConsoleNotificationSettingsProvider(OwnerConsoleNotificationSettings.Load(settingsPath));
                var soundOutput = new WindowsOwnerConsoleSoundOutput();
                var notifier = new SystemOwnerConsoleNotifier(soundOutput, settings.Current, File.Exists,
                    Directory.Exists, path => Directory.EnumerateFiles(path, "*.wav", SearchOption.TopDirectoryOnly),
                    Random.Shared.Next, current: () => settings.Current);
                var sound = new OwnerConsoleSoundCommand(settingsPath, settings, notifier, soundOutput,
                    OwnerConsoleSoundCommand.DefaultLibraryRoot(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
                    Directory.Exists, path => Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly));
                var controller = new OwnerConsoleScreenController(questions, new AttentionAnswerHandlerAdapter(workspace),
                    new TerminalGuiOwnerConsoleDialogs(app, token), new OwnerGoalDetailQuery(state,
                        new GoalEventFileTail(workspace.GoalLifecycleEventsDirectory), summary: OwnerGoalChangeSummaryQuery.ForWorkspace(workspace),
                        evidence: new(Path.Combine(workspace.OrchestratorDirectory, "pre-review-evidence-attempts"), clock)),
                    new CliConductorConsoleAdapter(workspace), new CliOwnerDigestConsoleAdapter(workspace),
                    new OwnerDigestSummaryAdapter(workspace), clock, new WorkspaceEpicProgressSource(workspace),
                    resolutions: new OwnerQuestionResolutionReader(state, new GoalEventFileTail(workspace.GoalLifecycleEventsDirectory),
                        workspace.OrchestratorDirectory, workspace.LogDirectory, clock),
                    notifier: notifier,
                    notifications: settings.Current,
                    fleet: () => OwnerFleetViewModelBuilder.Load(workspace, new ConductorLeaseLiveness(workspace.OrchestratorDirectory), owners: owners),
                    landings: argument => OwnerLandingsPage.Load(state, workspace.OrchestratorDirectory, clock, argument), sound: sound,
                    conductorPolicyPath: Path.Combine(workspace.OrchestratorDirectory, "conductor-policy.json"), workspace: workspace,
                    agentsSettings: new(workspace.AgentCatalogPath, workspace.ModelFunctionCatalogPath, workspace.WorkerProfilePath));
                view = new(app, controller, () => refresh.RefreshAsync(), token, clock, OwnerConsoleLoopOptions.Default, diagnostics,
                    projectLabel: OwnerConsoleProjectLabel.ForWorkspace(workspace), boardSource: boardSource);
                view.Render(board);
                startup = new OwnerConsoleStartupLoader(builder, activityLoader, action => Post("startup-render", action), clock,
                    evidence: evidence);
                fill = startup.FillAsync(board, model => view.Render(model), refresh.StartupInputs(), refresh.Seed,
                    token, ownBoardGoalIds: refresh.BoardIds);
            }
            catch { refresh.Release(); throw; }
            using var stop = token.Register(() => app.Invoke(() =>
            {
                app.RequestStop();
                app.RequestStop(view!.Window);
            }));
            reader = Task.Run(async () =>
            {
                try { await fill; }
                finally { refresh.Release(); }
                token.ThrowIfCancellationRequested();
                events = activityLoader.Events ?? new OwnerConsoleStartupEventSource(workspace.ConductEventsLogPath, clock);
                await new OwnerConsoleEventPump(events, clock,
                    message => app.Invoke(() => { if (!token.IsCancellationRequested) view!.ShowNotice(message, OwnerConsoleNoticeSeverity.Failure, OwnerConsoleNoticeSource.Refresh); }))
                    .RunAsync(item => { refreshFilter.Route(item, coalescer); return Task.CompletedTask; }, token);
            });
            periodicRefresh = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(OwnerConsoleLoopOptions.Default.RefreshInterval, clock, token);
                    app.Invoke(() => { if (!token.IsCancellationRequested) view.RefreshStatus(); });
                    // Skip rather than queue another refresh behind startup or an event.
                    await refresh.RefreshAsync(skipIfBusy: true);
                }
            }, token);
            streamRefresh = Task.Run(() => new OwnerConsoleStreamRefresh(changes,
                scope => { coalescer.Request(null, scope); return Task.CompletedTask; }, clock).RunAsync(token), token);
            app.Run(view!.Window);
            return 0;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return 0; }
        catch (Exception ex)
        {
            // The application has restored the terminal before reporting startup failure.
            app.Dispose();
            System.Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
        finally
        {
            idleTimer?.Dispose();
            if (keyboard is not null) keyboard.KeyDown -= OnKey;
            if (mouse is not null) mouse.MouseEvent -= OnMouse;
            app.LayoutAndDrawComplete -= OnDraw;
            backoff.Dispose();
            linked.Cancel();
            view?.Dispose();
            var pending = Task.WhenAll(new[] { reader, fill, periodicRefresh, streamRefresh, coalescer?.Completion }
                .OfType<Task>());
            async ValueTask DisposeReads()
            {
                // These are read cursors, observers and semaphores; no mandatory write flush
                // is deferred. In particular, startup may create Events after its bound.
                try
                {
                    if (events is not null || activityLoader is not null)
                        await (events?.DisposeAsync() ?? activityLoader!.DisposeAsync());
                }
                finally
                {
                    refresh?.Dispose();
                    boardSource?.Dispose();
                    stateChanges?.Dispose();
                    questionChanges?.Dispose();
                    portfolioChanges?.Dispose();
                }
            }
            await OwnerConsoleScreenOperation.DrainAsync(pending,
                () => Task.WhenAll(new[] { initialBoardRead, startup?.Completion, refresh?.Completion, view?.Completion }.OfType<Task>()),
                DisposeReads, clock, ex => System.Console.Error.WriteLine($"Console shutdown read cleanup: {ex.Message}"));
        }
    }
}
