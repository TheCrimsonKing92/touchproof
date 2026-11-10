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
    public void Dispose() => _rebuild.Dispose();

    internal OwnerConsoleViewModel TrackBoard(OwnerConsoleViewModel board)
    {
        BoardIds = board.Board.Select(row => row.GoalId).ToArray();
        return board;
    }

    internal OwnerConsoleViewInputs StartupInputs() => new(Opened, Last, [], 0);

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
        if (skipIfBusy)
        {
            if (!await _rebuild.WaitAsync(0, token)) return;
        }
        else await _rebuild.WaitAsync(token);
        try
        {
            var triggers = OwnerConsoleRefreshTriggerCounts.Identify(items, skipIfBusy, scope);
            timings.Triggers.Record(triggers);
            var inputs = Fold(items);
            await _operation.RunAsync(items is not { Count: > 0 } ? "refresh" : "refresh after conductor event",
                stepToken => _published is not null && ReferenceEquals(scope, OwnerConsoleRefreshScope.None) &&
                    items is { Count: > 0 } && items.All(OwnerConsoleEventRoute.IsPaneOnly)
                    ? RunPanesAsync(inputs, stepToken)
                    : RunStepsAsync(inputs, scope ?? OwnerConsoleRefreshScope.All,
                        triggers.HasFlag(OwnerConsoleRefreshTrigger.OwnerCommand), stepToken), token);
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
        CancellationToken stepToken)
    {
        using var goalLoads = snapshots.BeginRefresh(reuseUnchanged: true);
        var board = TrackBoard(await timings.TimeAsync("board", () => builder.BuildBoardAsync(stepToken, forceDecisions)));
        if (boardSource is not null) board = await boardSource.ComposeAsync(board, stepToken, forceDecisions);
        foreach (var lifecycle in timings.Time("lifecycle-tail", () => readLifecycle(BoardIds, scope).ToArray()))
            OwnerConsoleStartupActivity.Append(_recent, builder.EnrichEvent(lifecycle), OwnerConsoleStartupActivity.MaxRawEvents);
        var programHealth = OwnerConsoleProgramHealthNotice.Current(_recent);
        invoke(() => { if (!token.IsCancellationRequested) view().NoticeStrip.SetProgramHealth(programHealth); });
        var (decisions, hidden) = await timings.TimeAsync("decisions", () => builder.ReadDecisionsAsync(stepToken, forceDecisions));
        long ticket = 0;
        var model = timings.Time("activity", () =>
        {
            ticket = _evidence.Begin();
            var resolved = _evidence.Resolve(_recent);
            return builder.WithActivity(builder.WithDecisions(board, decisions, hidden),
                inputs with { RecentEvents = _recent.ToArray(), LandedToday = Landings }, resolved);
        });
        stepToken.ThrowIfCancellationRequested();
        if (!_evidence.IsCurrent(ticket)) return;
        _published = model;
        post("refresh-render", () =>
        {
            if (!token.IsCancellationRequested && _evidence.IsCurrent(ticket))
                timings.Time("render", () => { view().Render(model); return true; });
        });
    }

    private Task RunPanesAsync(OwnerConsoleViewInputs inputs, CancellationToken stepToken)
    {
        var model = builder.WithPanes(_published!, inputs);
        stepToken.ThrowIfCancellationRequested();
        var ticket = _evidence.Begin();
        _published = model;
        post("refresh-render", () =>
        {
            if (!token.IsCancellationRequested && _evidence.IsCurrent(ticket))
                timings.Time("render", () => { view().Render(model); return true; });
        });
        return Task.CompletedTask;
    }
}
