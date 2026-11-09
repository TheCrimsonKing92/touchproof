using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Terminal.Gui.App;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleFullScreenHost
{
    // Eager history/model helper retained for callers that need a complete snapshot.
    // Production progressive startup uses OwnerConsoleStartupLoader.FillAsync.
    internal static async Task<(OwnerConsoleViewModel Model, List<OwnerConductEvent> Recent)> BuildInitialViewModelAsync(
        OwnerConsoleViewModelBuilder builder, string conductLogPath, DateTimeOffset opened,
        DateTimeOffset? last, CancellationToken token)
    {
        var history = OwnerConsoleStartupActivity.ReadHistory(conductLogPath, null, TimeProvider.System, token);
        var recent = history.Recent.ToList();
        var model = await builder.BuildAsync(new(opened, last, recent, history.LandedToday), token);
        return (model, recent);
    }

    internal static async Task<int> RunAsync(OrchestratorWorkspace workspace, CancellationToken cancellationToken,
        string? inputTracePath = null, OwnerConsoleHostRowOwner? owners = null, OwnerConsoleBoardSource? boardSource = null)
    {
        if (inputTracePath is null) return await RunCoreAsync(workspace, cancellationToken, null, owners, boardSource);
        try
        {
            using var destination = OwnerConsoleInputTraceOptions.OpenDestination(inputTracePath);
            var diagnostics = new OwnerConsoleInputDiagnostics();
            var result = await RunCoreAsync(workspace, cancellationToken, diagnostics, owners, boardSource);
            // RunCoreAsync has disposed the application and restored the terminal before export.
            diagnostics.Export(destination);
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            System.Console.Error.WriteLine($"Input trace failed: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunCoreAsync(OrchestratorWorkspace workspace, CancellationToken cancellationToken,
        OwnerConsoleInputDiagnostics? diagnostics, OwnerConsoleHostRowOwner? owners, OwnerConsoleBoardSource? boardSource)
    {
        var clock = TimeProvider.System;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = linked.Token;
        IConductEventSource? events = null;
        OwnerConsoleActivityLoader? activityLoader = null;
        Task? fill = null;
        OwnerRefreshCoordinator? refresh = null;
        using IApplication app = OwnerConsoleFrameOutputInstaller.CreateApplication();
        OwnerConsoleFullScreenView? view = null;
        Task? reader = null;
        Task periodicRefresh = Task.CompletedTask;
        Task streamRefresh = Task.CompletedTask;
        OwnerConsoleRefreshCoalescer? coalescer = null;
        var timings = new OwnerConsoleRefreshTimings(clock);
        var timingsFile = new OwnerConsoleRefreshTimingsFile(
            Path.Combine(workspace.LogDirectory, "owner-console-refresh-timings.txt"), clock, Environment.ProcessId);
        void Post(string phase, Action action) => app.Invoke(diagnostics?.ObservePost(phase, action) ?? action);
        try
        {
            IOrchestratorStateQueries state = File.Exists(workspace.SqliteStatePath)
                ? SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath) : new EmptyOwnerConsoleStateQueries();
            var refreshFilter = new OwnerConsoleEventRefreshFilter();
            var snapshots = new OwnerConsoleRefreshGoalSnapshots(state);
            IOwnerQuestionSource questions = new SerializedOwnerQuestionSource(OwnerQuestionReadModelFactory.Create(state, workspace.OrchestratorDirectory, snapshots));
            if (boardSource is not null)
                questions = new OnboardingQuestionSource(questions, boardSource.InScopeProjects, clock,
                    () => coalescer?.Request(null, OwnerConsoleRefreshScope.All));
            var cohortEvidence = new OwnerActivityEvidenceReader(Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db"));
            var gateEvidence = new OwnerGateFailureEvidence(workspace.OrchestratorDirectory);
            var jointEvidence = new OwnerJointGateFailureEvidence(workspace.OrchestratorDirectory);
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
                }, decisionsMarker: boardSource is null ? OwnerConsoleStoreFingerprint.For(workspace.SqliteStatePath,
                    Path.Combine(workspace.OrchestratorDirectory, "collaboration-items.db")) : null);
            activityLoader = new(workspace.ConductEventsLogPath, workspace.GoalLifecycleEventsDirectory, clock);
            refresh = new(clock, token, builder, boardSource, snapshots, timings, refreshFilter, diagnostics,
                activityLoader.ReadNew, new OwnerConsoleLandedTodayScan(workspace.GoalLifecycleEventsDirectory, clock).Current,
                timingsFile.Write, action => app.Invoke(action), Post, () => view!, evidence);
            coalescer = new((items, scope) => refresh.RefreshAsync(items, scope: scope));

            refresh.HoldForStartup();
            try
            {
                // Keep Init, initial Render and Run on this host thread. Metadata is
                // read on the pool before GUI initialization, with the existing bound.
                var board = OwnerConsoleStartupLoader.LoadingBoard(Task.Run(async () =>
                {
                    var own = refresh.TrackBoard(await builder.BuildBoardAsync(token));
                    return boardSource is null ? own : await boardSource.ComposeAsync(own, token);
                }, token)
                    .WaitAsync(OwnerConsoleLoopOptions.Default.OperationBound, clock, token).GetAwaiter().GetResult());
                app.Init();
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
                    landings: argument => OwnerLandingsPage.Load(state, workspace.OrchestratorDirectory, clock, argument), sound: sound);
                view = new(app, controller, () => refresh.RefreshAsync(), token, clock, OwnerConsoleLoopOptions.Default, diagnostics,
                    projectLabel: OwnerConsoleProjectLabel.ForWorkspace(workspace), boardSource: boardSource);
                view.Render(board);
                var startup = new OwnerConsoleStartupLoader(builder, activityLoader, action => Post("startup-render", action), clock,
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
            linked.Cancel();
            try { await periodicRefresh.WaitAsync(OwnerConsoleLoopOptions.Default.ShutdownBound, clock, CancellationToken.None); }
            catch (OperationCanceledException) { }
            catch (TimeoutException) { }
            try { await streamRefresh.WaitAsync(OwnerConsoleLoopOptions.Default.ShutdownBound, clock, CancellationToken.None); }
            catch (OperationCanceledException) { }
            catch (TimeoutException) { }
            if (reader is not null)
            {
                try { await reader.WaitAsync(OwnerConsoleLoopOptions.Default.ShutdownBound, clock, CancellationToken.None); }
                catch (OperationCanceledException) { }
                catch (TimeoutException) { }
            }
            if (coalescer is not null)
            {
                try { await coalescer.Completion.WaitAsync(OwnerConsoleLoopOptions.Default.ShutdownBound, clock, CancellationToken.None); }
                catch (OperationCanceledException) { }
                catch (TimeoutException) { }
            }
            if (events is not null || activityLoader is not null)
            {
                try { await (events?.DisposeAsync() ?? activityLoader!.DisposeAsync()).AsTask().WaitAsync(OwnerConsoleLoopOptions.Default.ShutdownBound, clock, CancellationToken.None); }
                catch (TimeoutException) { }
            }
            view?.Dispose();
            refresh?.Dispose();
        }
    }
}
