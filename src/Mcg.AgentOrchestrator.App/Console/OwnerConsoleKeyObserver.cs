using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleKeyObserver(
    IOwnerConsoleKeySurface surface, IApplication app, Window window, OwnerConsoleKeyRouter router,
    OwnerConsoleInputDiagnostics? diagnostics, Func<OwnerConsoleTraceState> traceState, CancellationToken token)
{
    internal async void OnKeyDown(object? sender, Key key)
    {
        if (diagnostics is null && app.TopRunnableView != window) return;
        try
        {
            await (diagnostics is null ? HandleKeyCoreAsync(key) :
                ObserveKeyAsync(key, app.TopRunnableView == window, "keyboard-callback"));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { surface.ShowNotice($"key action failed: {ex.Message}", OwnerConsoleNoticeSeverity.Failure, OwnerConsoleNoticeSource.Action); }
    }

    // The production callback and headless tests share routing; their input boundaries remain distinct.
    internal Task HandleKeyAsync(Key key) => diagnostics is null ? HandleKeyCoreAsync(key) :
        ObserveKeyAsync(key, true, "headless-input");

    internal async Task ObserveKeyAsync(Key key, bool routeToWindow, string boundary)
    {
        using var input = diagnostics?.Begin(boundary, traceState, key.ToString(), input: true);
        if (!routeToWindow)
        {
            if (input is not null) input.Outcome = "excluded-top-window";
            return;
        }
        using var handler = diagnostics?.Begin("handler", traceState);
        try
        {
            await HandleKeyCoreAsync(key);
            if (handler is not null) handler.Outcome = key.Handled ? "handled" : "unhandled";
            if (input is not null) input.Outcome = key.Handled ? "handled" : "unhandled";
        }
        catch (Exception ex)
        {
            if (handler is not null) handler.Outcome = ex is OperationCanceledException ? "cancelled" : "faulted";
            if (input is not null) input.Outcome = handler?.Outcome ?? "faulted";
            throw;
        }
    }

    private Task HandleKeyCoreAsync(Key key) => router.RouteAsync(key);
}
