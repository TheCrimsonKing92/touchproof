using System.Data;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleFullScreenComposer
{
    internal static DataTable CreateBoardTable()
    {
        var table = new DataTable();
        foreach (var name in new[] { "GOAL", "EPIC", "TITLE", "PRIORITY", "STATUS", "LAST EVENT" }) table.Columns.Add(name);
        return table;
    }

    internal static (OwnerConsoleNoticeStrip NoticeStrip, OwnerConsoleNoticeExpiry NoticeExpiry,
        OwnerConsoleScreenOperation Operation, OwnerConsoleFullScreenLayout Layout,
        OwnerConsoleRenderCoordinator RenderCoordinator, OwnerConsolePreviewPane PreviewPane,
        OwnerConsoleViewTrace Trace, OwnerConsoleBoardSelection Selection,
        OwnerConsolePaneRenderer PaneRenderer, OwnerConsoleEpicDialog EpicView, OwnerConsoleSettingsView SettingsView) Build(
        OwnerConsoleFullScreenView view, IApplication app, OwnerConsoleScreenController controller,
        CancellationToken token, CancellationToken lifetimeToken, TimeProvider clock,
        OwnerConsoleLoopOptions? options, OwnerConsoleInputDiagnostics? diagnostics,
        OwnerConsoleDecisionsPane decisionsPane, OwnerConsoleActivityPane activityPane, OwnerConsoleBoardPane board,
        Label notice, Label status, Label hints, Label epicHint, OwnerStatusPresenter statusPresenter,
        OwnerConsoleRenderState state, Dictionary<string, string> working, OwnerConsoleBoardCollapse collapse,
        OwnerConsoleBoardSource? boardSource = null)
    {
        var noticeStrip = new OwnerConsoleNoticeStrip(clock);
        var refreshInterval = (options ?? OwnerConsoleLoopOptions.Default).RefreshInterval;
        if (refreshInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options), "Refresh interval must be positive.");
        var noticeExpiry = new OwnerConsoleNoticeExpiry(clock, view.Invoke, view.RenderNotices, lifetimeToken);
        var operation = new OwnerConsoleScreenOperation(clock,
            label => view.Invoke(() => { if (!lifetimeToken.IsCancellationRequested) view.SetWorking("command", label); }),
            message => view.Invoke(() => { if (!lifetimeToken.IsCancellationRequested) view.ShowNotice(message, OwnerConsoleNoticeSeverity.Failure, OwnerConsoleNoticeSource.Action); }), options,
            message => view.Invoke(() => { if (!lifetimeToken.IsCancellationRequested) view.ShowNotice(message, OwnerConsoleNoticeSeverity.Success, OwnerConsoleNoticeSource.Action); }));
        var layout = new OwnerConsoleFullScreenLayout(decisionsPane.List, decisionsPane.EmptyLabel, board, activityPane.List);
        var renderCoordinator = new OwnerConsoleRenderCoordinator(layout, decisionsPane, board, activityPane, view.BoardTable, controller, noticeStrip,
            notice, status, statusPresenter, clock, refreshInterval, app, noticeExpiry, lifetimeToken, () => state.RefreshedAt, () => working.Values);
        var previewPane = new OwnerConsolePreviewPane(layout.PreviewFrame, clock, controller.LoadGoalSummaryAsync, view.Invoke,
            () => view.SelectedBoardRow, id => controller.Model?.Board.FirstOrDefault(row =>
                row.Project == view.SelectedProject && row.GoalId == id), lifetimeToken);
        var trace = new OwnerConsoleViewTrace(view, app, view.Window, controller, diagnostics);
        var selection = new OwnerConsoleBoardSelection(view, controller, previewPane, board, view.BoardTable, diagnostics, trace.State, () => state.SelectedFieldKey);
        var paneRenderer = new OwnerConsolePaneRenderer(state, controller, diagnostics, trace, collapse, clock, renderCoordinator,
            selection, layout, board, decisionsPane, activityPane, previewPane, view.RenderHints,
            view.GateView, view.Window, view.BoardTable, noticeStrip);
        board.Table = new DataTableSource(view.BoardTable);
        epicHint.Width = OwnerConsoleEpicFormatter.KeyHint.Length;
        epicHint.X = Pos.AnchorEnd(OwnerConsoleEpicFormatter.KeyHint.Length);
        hints.Width = Dim.Fill(OwnerConsoleEpicFormatter.KeyHint.Length + 2);
        AttachBoardOrderKey(view, hints);
        var epicView = new OwnerConsoleEpicDialog(controller, view.Invoke, token);
        List<IOwnerConsoleSettingsTab> settingsTabs = [];
        if (controller.Sound is { } sound) settingsTabs.Add(new OwnerConsoleAlertsSettingsTab(sound));
        if (controller.AgentsSettings is { } agents) settingsTabs.Add(agents);
        if (controller.ConductorPolicyPath is { } policyPath) settingsTabs.Add(new OwnerConsolePolicySettingsTab(policyPath));
        if (controller.Workspace is { } workspace)
            settingsTabs.Add(new OwnerConsoleExecutorsSettingsTab(workspace, new(workspace)));
        if (boardSource?.Registry is { } registry)
            settingsTabs.Add(new OwnerConsoleProjectsSettingsTab(registry, boardSource.HandleScope));
        var settingsView = new OwnerConsoleSettingsView(settingsTabs);
        return (noticeStrip, noticeExpiry, operation, layout, renderCoordinator, previewPane, trace, selection, paneRenderer, epicView, settingsView);
    }

    private static void AttachBoardOrderKey(OwnerConsoleFullScreenView view, Label hints)
    {
        var decorating = false;
        var baseHint = hints.Text;
        void Decorate()
        {
            if (decorating || view.FocusedPane != OwnerConsolePane.Board || view.EditingCommand ||
                view.CommandLine.HasFocus || hints.Viewport.Width <= 0) return;
            decorating = true;
            try { hints.Text = OwnerConsoleBoardOrderKey.Fit(hints.Viewport.Width) + "  " + baseHint; }
            finally { decorating = false; }
        }
        hints.TextChanged += (_, _) =>
        {
            if (decorating) return;
            baseHint = hints.Text;
            Decorate();
        };
        // Assigning unchanged base text raises no TextChanged event, including the first layout.
        hints.ViewportChanged += (_, _) => Decorate();
    }

    // Wiring invokes view callbacks immediately, so Build's results must already be stored on the view.
    internal static (OwnerConsoleKeyObserver Keys, OwnerConsoleBindingRunner BindingRunner, IKeyboard? Keyboard) Attach(
        OwnerConsoleFullScreenView view, IApplication app, OwnerConsoleScreenController controller,
        Func<Task> refresh, CancellationToken token, OwnerConsoleInputDiagnostics? diagnostics,
        OwnerConsoleBoardSource? boardSource, OwnerConsoleClipboard? clipboard,
        OwnerConsoleFullScreenLayout layout, OwnerConsolePreviewPane previewPane,
        OwnerConsoleViewTrace trace, OwnerConsoleBoardSelection selection, OwnerConsolePaneRenderer paneRenderer,
        OwnerConsoleScreenOperation operation, OwnerConsoleActivityPane activityPane, OwnerConsoleBoardPane board,
        Label notice, Label status, Label hints, Label epicHint, OwnerConsoleRenderState state,
        OwnerConsoleBoardCollapse collapse, Func<string?> completionHint, Action<string?> setCompletionHint)
    {
        layout.Compose(view.Window, status, notice, hints, epicHint, view.CommandLine, view.EpicView, view.GateView, view.SettingsView);
        new OwnerConsoleFullScreenWiring(
            previewPane.Body, notice, status, view.DecisionsPane, board, view.ActivityPane, view.CommandLine, activityPane,
            controller, diagnostics, trace.State,
            state, value => selection.CopyValid = value,
            completionHint, setCompletionHint,
            view.RenderPreview, view.RenderNotices, view.RenderStatus, view.RenderHints, view.RenderOverflow, paneRenderer.RenderDecisions,
            index => view.SelectBoard(index), view.PaneFocusChanged, view.Fit, view.ActivityRow).Attach();
        view.RenderHints();
        var keys = new OwnerConsoleKeyObserver(view, app, view.Window, new(view, controller, operation, clipboard ?? OwnerConsoleClipboard.For(app), collapse, token),
            diagnostics, trace.State, token);
        var bindingRunner = new OwnerConsoleBindingRunner(view, controller, operation, boardSource is null ? null : boardSource.HandleScope, refresh, token);
        var keyboard = app.Initialized ? app.Keyboard : null;
        if (keyboard is not null) keyboard.KeyDown += keys.OnKeyDown;
        if (diagnostics is not null) app.LayoutAndDrawComplete += trace.OnLayoutAndDrawComplete;
        return (keys, bindingRunner, keyboard);
    }
}
