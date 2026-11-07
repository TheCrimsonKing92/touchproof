using Terminal.Gui.Input;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsolePaneKeys(IOwnerConsoleKeySurface _view,
    OwnerConsoleScreenController _controller, OwnerConsoleScreenOperation _operation, CancellationToken _token)
{
    internal async Task<bool> HandleAsync(Key key)
    {
        var pane = _view.FocusedPane;
        var count = pane == OwnerConsolePane.Activity ? _view.ActivityLines.Count : pane == OwnerConsolePane.Board ?
            _view.BoardTable.Rows.Count : _controller.Model?.Decisions.Length ?? 0;
        var current = pane == OwnerConsolePane.Activity ? _view.ActivityPane.SelectedItem ?? 0 : pane == OwnerConsolePane.Board ?
            _view.SelectedBoardIndex : _controller.SelectedIndex;
        var pageHeight = pane == OwnerConsolePane.Board ? _view.BoardBodyRows() : _view.PaneView(pane).Viewport.Height;
        if (OwnerConsolePaneNavigator.Target(key, current, count, pageHeight) is { } target)
        {
            key.Handled = true;
            if (_view.ActionRunning || target < 0) return true;
            if (pane == OwnerConsolePane.Board) _view.SelectBoard(target, direction:
                key == Key.Home ? 1 : key == Key.End ? -1 : Math.Sign(target - current));
            else if (pane == OwnerConsolePane.Activity) { _view.ActivityPane.SelectedItem = target; _view.ActivityPane.EnsureSelectedItemVisible(); }
            else { _controller.SelectIndex(target); _view.DecisionsPane.SelectedItem = target; _view.DecisionsPane.EnsureSelectedItemVisible(); }
            _view.RenderHints();
            _view.RenderOverflow();
            return true;
        }
        if (key == Key.CursorUp || key == Key.CursorDown)
        {
            key.Handled = true;
            // Production modal keys are excluded by OnKeyDown's top-window guard. Keep the
            // existing headless decision-navigation contract while a fake dialog is pending.
            if (_view.ActionRunning && pane != OwnerConsolePane.Decisions) return true;
            var delta = key == Key.CursorUp ? -1 : 1;
            if (pane == OwnerConsolePane.Board) _view.SelectBoard(_view.SelectedBoardIndex + delta, direction: delta);
            else if (pane == OwnerConsolePane.Activity)
            {
                if (_view.ActivityLines.Count > 0)
                {
                    _view.ActivityPane.SelectedItem = Math.Clamp((_view.ActivityPane.SelectedItem ?? 0) + delta, 0, _view.ActivityLines.Count - 1);
                    _view.ActivityPane.EnsureSelectedItemVisible();
                }
            }
            else
            {
                await _controller.HandleKeyAsync(delta < 0 ? ConsoleKey.UpArrow : ConsoleKey.DownArrow, cancellationToken: _token);
                _view.DecisionsPane.SelectedItem = _controller.SelectedIndex < 0 ? null : _controller.SelectedIndex;
                _view.RenderHints();
            }
            _view.RenderOverflow();
            return true;
        }
        if (key == Key.Enter)
        {
            key.Handled = true;
            if (_view.ActionRunning) return true;
            if (pane == OwnerConsolePane.Board && _view.RefuseSelectedBoardRow()) return true;
            if (pane == OwnerConsolePane.Board && _view.SelectedGoalId is { } goalId)
                await _view.ActAsync(ct => _controller.ShowGoalDetailAsync(goalId, _operation, ct));
            else if (pane == OwnerConsolePane.Decisions && _controller.SelectedIndex >= 0)
                await _view.ActAsync(ct => _controller.HandleKeyAsync(ConsoleKey.Enter, operation: _operation, cancellationToken: ct));
            else if (pane == OwnerConsolePane.Activity && _controller.Model?.ActivityState is null &&
                _view.ActivityPane.SelectedItem is { } selected && selected >= 0 &&
                selected < _view.ActivityModel.Rows.Count)
            {
                if (_view.ActivityModel.Rows[selected].ItemIndex is { } itemIndex)
                    await _view.ActAsync(ct => _controller.ShowActivityMeaningAsync(_controller.Model!.Activity[itemIndex], _operation, ct));
                else if (_view.ActivityModel.Rows[selected].Key.Kind == OwnerConsoleActivityFilter.EmptyKind)
                {
                    _view.StartAction();
                    _view.ShowNotice(OwnerConsoleActivityFilter.EmptyEnterNotice(_view.ActivityModel.Filter!), OwnerConsoleNoticeSeverity.Failure, OwnerConsoleNoticeSource.Action);
                }
                else
                {
                    _view.StartAction();
                    _view.ShowNotice("Enter: a day separator has no detail; select an event row", OwnerConsoleNoticeSeverity.Failure, OwnerConsoleNoticeSource.Action);
                }
            }
            return true;
        }
        return false;
    }
}
