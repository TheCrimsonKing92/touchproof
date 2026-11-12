using System.Collections.Immutable;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Owns refresh state under the rebuild gate; startup seeds it while holding that gate.
internal sealed class OwnerRefreshCoordinator(TimeProvider clock, CancellationToken token,
    OwnerConsoleViewModelBuilder builder, OwnerConsoleBoardSource? boardSource,
    OwnerConsoleRefreshGoalSnapshots snapshots, OwnerConsoleRefreshTimings timings,
    OwnerConsoleEventRefreshFilter refreshFilter, OwnerConsoleInputDiagnostics? diagnostics,
    Func<IReadOnlyCollection<string>, OwnerConsoleRefreshScope, IReadOnlyList<OwnerConductEvent>> readLifecycle,
    Func<int> landedToday, Action<string> writeTimings, Action<Action> invoke,
    Action<string, Action> post, Func<OwnerConsoleFullScreenView> view,
    OwnerActivityEvidenceLoader? evidence = null) : IDisposable
{
    private readonly OwnerActivityEvidenceLoader _evidence = evidence ?? new(null);
    private readonly SemaphoreSlim _rebuild = new(1, 1);
    internal DateTimeOffset Opened { get; } = clock.GetUtcNow();
    internal DateTimeOffset? Last { get; private set; }
    internal int Landings { get; private set; }
    internal DateTime LandingDate { get; private set; } = clock.GetLocalNow().Date;
    private readonly HashSet<OwnerConductEvent> _countedLandings = [];
    private List<OwnerConductEvent> _recent = [];
    private OwnerConsoleViewModel? _published;
    internal IReadOnlyCollection<OwnerConductEvent> CountedLandings => _countedLandings.ToArray();
    internal IReadOnlyList<OwnerConductEvent> Recent => _recent.ToArray();
    internal IReadOnlyCollection<string> BoardIds { get; private set; } = [];
    private readonly OwnerConsoleScreenOperation _operation = new(clock,
        label => invoke(() => { if (!token.IsCancellationRequested) view().SetWorking("refresh", label); }),
        message => invoke(() => { if (!token.IsCancellationRequested) view().ShowNotice(message, OwnerConsoleNoticeSeverity.Failure, OwnerConsoleNoticeSource.Refresh); }));

    internal void HoldForStartup() => _rebuild.Wait(token);
    internal void Release() => _rebuild.Release();
    internal Task Completion => _operation.Completion;
    public void Dispose() => _rebuild.Dispose();

    internal OwnerConsoleViewModel TrackBoard(OwnerConsoleViewModel board)
    {
        BoardIds = board.Board.Select(row => row.GoalId).ToArray();
        return board;
    }

    internal OwnerConsoleViewInputs StartupInputs() => new(Opened, Last, [], 0);

    // Startup posts can still be queued when the rebuild gate opens. Only seed
    // its completed panes, and never overwrite a model owned by a newer refresh.
    internal void SeedPublished(OwnerConsoleViewModel model)
    {
        if (model.DecisionsState?.Loading != true && model.ActivityState?.Loading != true)
            Interlocked.CompareExchange(ref _published, model, null);
    }

    internal void Seed(OwnerConsoleActivityLoad loaded)
    {
        _recent = loaded.Recent.Select(builder.EnrichEvent).ToList();
        Last = loaded.LastActivity;
        Landings = loaded.LandedToday;
        if (landedToday.Target is OwnerConsoleLandedTodayScan scan) Landings = scan.Seed(Landings);
        timings.Triggers.Record(OwnerConsoleRefreshTrigger.Startup);
        foreach (var landing in loaded.Recent.Where(OwnerLandingNarration.IsLanding)) _countedLandings.Add(landing);
    }

    internal async Task RefreshAsync(IReadOnlyList<OwnerConductEvent>? items = null,
        bool skipIfBusy = false, OwnerConsoleRefreshScope? scope = null)
    {
        using var trace = diagnostics?.Begin("refresh-work", detail: skipIfBusy ? "skip-if-busy" : "queued");
        using (diagnostics?.Begin("refresh-admission"))
        {
            if (skipIfBusy)
            {
                if (!await _rebuild.WaitAsync(0, token)) return;
            }
            else await _rebuild.WaitAsync(token);
        }
        try
        {
            var triggers = OwnerConsoleRefreshTriggerCounts.Identify(items, skipIfBusy, scope);
            timings.Triggers.Record(triggers);
            OwnerConsoleViewInputs inputs;
            using (diagnostics?.Begin("refresh-fold")) inputs = Fold(items);
            await _operation.RunAsync(items is not { Count: > 0 } ? "refresh" : "refresh after conductor event",
                stepToken => _published is not null && ReferenceEquals(scope, OwnerConsoleRefreshScope.None) &&
                    items is { Count: > 0 } && items.All(OwnerConsoleEventRoute.IsPaneOnly)
                    ? RunPanesAsync(inputs, stepToken)
                    : RunStepsAsync(inputs, scope ?? OwnerConsoleRefreshScope.All,
                        triggers.HasFlag(OwnerConsoleRefreshTrigger.OwnerCommand), !skipIfBusy, stepToken), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        { invoke(() => view().ShowNotice(ex.Message, OwnerConsoleNoticeSeverity.Failure, OwnerConsoleNoticeSource.Refresh)); }
        finally
        {
            _rebuild.Release();
            diagnostics?.Mark("refresh-loaders", detail: timings.Detail());
            writeTimings(timings.Line(snapshots.StoreLoadedGoals));
        }
    }

    private OwnerConsoleViewInputs Fold(IReadOnlyList<OwnerConductEvent>? items)
    {
        if (clock.GetLocalNow().Date != LandingDate)
        { LandingDate = clock.GetLocalNow().Date; Landings = 0; _countedLandings.Clear(); }
        var landed = timings.Time("landed-today", landedToday);
        foreach (var item in refreshFilter.TakeDeferred().Concat(items ?? []))
        {
            Last = item.Timestamp;
            if (OwnerLandingNarration.IsLanding(item) &&
                TimeZoneInfo.ConvertTime(item.Timestamp, clock.LocalTimeZone).Date == LandingDate && _countedLandings.Add(item) &&
                !(landedToday.Target is OwnerConsoleLandedTodayScan scan && scan.Includes(item))) Landings++;
            OwnerConsoleStartupActivity.Append(_recent, item, OwnerConsoleStartupActivity.MaxRawEvents);
        }
        // The scan can already include this batch; reconcile after folding it.
        Landings = Math.Max(Landings, landed);
        Last = refreshFilter.FoldLast(Last);
        return new(Opened, Last, _recent.ToArray(), Landings);
    }

    private async Task RunStepsAsync(OwnerConsoleViewInputs inputs, OwnerConsoleRefreshScope scope, bool forceDecisions,
        bool progressive, CancellationToken stepToken)
    {
        var ticket = _evidence.Begin();
        // Read questions outside the board generation. A question source must
        // not join a retained board load that predates its authoritative read.
        (ImmutableArray<OwnerConsoleDecision> decisions, int hidden) decisionRead;
        using (diagnostics?.Begin("refresh-decisions"))
            decisionRead = await timings.TimeAsync("decisions", () => builder.ReadDecisionsAsync(stepToken, forceDecisions, independent: true));
        var (decisions, hidden) = decisionRead;
        if (progressive && _published is { } previous &&
            (!previous.Decisions.SequenceEqual(decisions) || previous.Status.HiddenQuestions != hidden ||
                previous.DecisionsState is not null))
            Publish(builder.WithDecisions(previous, decisions, hidden) with { DecisionsState = null }, ticket, stepToken, complete: false);

        using var goalLoads = snapshots.BeginRefresh(reuseUnchanged: true);
        OwnerConsoleViewModel board;
        using (diagnostics?.Begin("refresh-board"))
            board = TrackBoard(await timings.TimeAsync("board", () => builder.BuildBoardAsync(stepToken, forceDecisions)));
        if (boardSource is not null)
        {
            using var composition = diagnostics?.Begin("refresh-projects");
            board = await boardSource.ComposeAsync(board, stepToken, forceDecisions);
        }
        board = builder.WithDecisions(board, decisions, hidden);
        // Activity evidence is independent of the usable board/decision rows.
        // Keep the previous activity until its replacement is ready. Periodic
        // idle refreshes retain their single render rather than adding paints.
        if (progressive && _published is { } current &&
            (!current.Board.SequenceEqual(board.Board) || current.Gate?.Live == true || board.Gate?.Live == true))
            Publish(board with { Activity = current.Activity, ActivityState = current.ActivityState }, ticket, stepToken, complete: false);

        using var activityTrace = diagnostics?.Begin("refresh-activity");
        foreach (var lifecycle in timings.Time("lifecycle-tail", () => readLifecycle(BoardIds, scope).ToArray()))
            OwnerConsoleStartupActivity.Append(_recent, builder.EnrichEvent(lifecycle), OwnerConsoleStartupActivity.MaxRawEvents);
        var programHealth = OwnerConsoleProgramHealthNotice.Current(_recent);
        invoke(() => { if (!token.IsCancellationRequested) view().NoticeStrip.SetProgramHealth(programHealth); });
        var model = timings.Time("activity", () =>
        {
            var resolved = _evidence.Resolve(_recent);
            return builder.WithActivity(board,
                inputs with { RecentEvents = _recent.ToArray(), LandedToday = Landings }, resolved);
        });
        Publish(model, ticket, stepToken);
    }

    private void Publish(OwnerConsoleViewModel model, long ticket, CancellationToken stepToken, bool complete = true)
    {
        stepToken.ThrowIfCancellationRequested();
        if (!_evidence.IsCurrent(ticket)) return;
        Interlocked.Exchange(ref _published, model);
        post("refresh-render", () =>
        {
            if (!token.IsCancellationRequested && !stepToken.IsCancellationRequested && _evidence.IsCurrent(ticket))
                timings.Time("render", () => { view().Render(model, refreshPreview: complete); return true; });
        });
    }

    private Task RunPanesAsync(OwnerConsoleViewInputs inputs, CancellationToken stepToken)
    {
        var model = builder.WithPanes(_published!, inputs);
        var ticket = _evidence.Begin();
        Publish(model, ticket, stepToken);
        return Task.CompletedTask;
    }
}
