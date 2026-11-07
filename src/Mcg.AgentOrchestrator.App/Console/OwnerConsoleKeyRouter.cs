using Terminal.Gui.Input;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleKeyRouter(IOwnerConsoleKeySurface _view,
    OwnerConsoleScreenController _controller, OwnerConsoleScreenOperation _operation,
    OwnerConsoleClipboard _clipboard, OwnerConsoleBoardCollapse _collapse, CancellationToken _token)
{
    private readonly OwnerConsoleDialogKeys _dialogs = new(_view, _controller);
    private readonly OwnerConsoleCommandLineKeys _commands = new(_view, _controller, _operation);
    private readonly OwnerConsolePaneKeys _panes = new(_view, _controller, _operation, _token);

    internal async Task RouteAsync(Key key)
    {
        if (await _dialogs.HandleAsync(key)) return;
        if (await _commands.HandleAsync(key)) return;
        if (key == OwnerConsoleBindings.OpenFields)
        {
            key.Handled = true;
            if (_view.ActionRunning) return;
            var fields = _view.BoardPane.HasFocus ? _view.SelectedBoardFields : _view.DecisionsPane.HasFocus ? _view.DecisionFields :
                _view.ActivityPane.HasFocus ? _view.ActivityModel.SelectedFields : null;
            var owner = _view.PaneView(_view.FocusedPane);
            if (fields is not null) await _controller.ShowFieldsAsync(fields, _view.BoardPane.HasFocus ? _view.SelectedFieldKey : null);
            else _view.ShowNotice("No selected structured item.", OwnerConsoleNoticeSeverity.Failure, OwnerConsoleNoticeSource.Action);
            owner.SetFocus();
            return;
        }
        if (key == OwnerConsoleBindings.CopyField && _view.BoardPane.HasFocus)
        {
            key.Handled = true;
            var field = _view.SelectedBoardFields?.Fields.FirstOrDefault(item => item.Key == _view.SelectedFieldKey);
            var result = _clipboard.Copy(field);
            _view.ShowNotice(result.Message, result.Copied ? OwnerConsoleNoticeSeverity.Success : OwnerConsoleNoticeSeverity.Failure,
                OwnerConsoleNoticeSource.Action);
            return;
        }
        if (_view.BoardPane.HasFocus && (key == Key.CursorLeft || key == Key.CursorRight))
        {
            key.Handled = true;
            var currentColumn = OwnerConsoleBoardRenderer.FieldKeys.ToList().IndexOf(_view.SelectedFieldKey ?? "");
            var direction = key == Key.CursorLeft ? -1 : 1;
            var next = currentColumn + direction;
            while (next >= 0 && next < _view.BoardTable.Columns.Count && !_view.BoardPane.Style.GetOrCreateColumnStyle(next).Visible) next += direction;
            if (next >= 0 && next < _view.BoardTable.Columns.Count && _view.SelectedBoardIndex >= 0)
            {
                _view.SelectedFieldKey = OwnerConsoleBoardRenderer.FieldKeys[next]; _view.CopySelectionValid = true;
                _view.BoardPane.SetSelection(next, _view.SelectedBoardIndex, false);
            }
            return;
        }
        if (key.AsRune.Value == ':' && !_view.ActionRunning)
        {
            key.Handled = true;
            _view.CommandReturnFocus = _view.PaneView(_view.FocusedPane);
            _view.EditingCommand = true;
            _view.CommandLine.SetFocus();
            _view.RenderHints();
            return;
        }
        var character = (char)key.AsRune.Value;
        if (!key.IsCtrl && !key.IsAlt && char.ToLowerInvariant(character) == 'c' &&
            _view.FocusedPane == OwnerConsolePane.Board && OwnerConsoleBoardCollapse.Enabled(_view.ArrangedBoard))
        {
            key.Handled = true;
            if (_view.ActionRunning || _view.SelectedProject is not { } project || _view.ArrangedBoard is null ||
                !_view.ArrangedBoard.Board.Any(row => row.IsDiagnostic &&
                    string.Equals(row.Project, project, StringComparison.OrdinalIgnoreCase))) return;
            _collapse.Toggle(project);
            _view.Rendering = true;
            try
            {
                var collapsed = _collapse.Apply(_view.ArrangedBoard);
                _controller.Apply(collapsed);
                _view.RenderBoard();
                var header = Array.FindIndex(collapsed.Board.ToArray(), row => row.IsDiagnostic &&
                    string.Equals(row.Project, project, StringComparison.OrdinalIgnoreCase));
                _view.SelectBoard(header, allowHeader: true);
            }
            finally { _view.Rendering = false; }
            _view.RenderHints();
            _view.RenderOverflow();
            return;
        }
        if (!key.IsCtrl && !key.IsAlt && OwnerConsoleBindings.ForKey(character, _view.FocusedPane) is { } keyBinding)
        {
            key.Handled = true;
            await _view.RunBindingAsync(keyBinding, fromKey: true);
            return;
        }
        if (!key.IsCtrl && !key.IsAlt && OwnerConsoleBindings.IsTopLevelKey(character))
        {
            key.Handled = true;
            if (!_view.ActionRunning)
            {
                _view.StartAction();
                _view.ShowNotice(OwnerConsoleKeyHints.UnavailableKey(character, _view.FocusedPane), OwnerConsoleNoticeSeverity.Failure, OwnerConsoleNoticeSource.Action);
            }
            return;
        }
        if (key == Key.Tab.WithShift)
        {
            key.Handled = true;
            if (!_view.ActionRunning) _view.FocusPane(OwnerConsoleKeyHints.Previous(_view.FocusedPane));
            return;
        }
        if (key == Key.Tab)
        {
            key.Handled = true;
            if (!_view.ActionRunning) _view.FocusPane(OwnerConsoleKeyHints.Next(_view.FocusedPane));
            return;
        }
        if (key == Key.Home && _view.FocusedPane == OwnerConsolePane.Activity)
        { key.Handled = true; if (!_view.ActionRunning) _view.FollowLatest(); return; }
        if (await _panes.HandleAsync(key)) return;
        if (!_view.ActionRunning && !key.IsCtrl && !key.IsAlt && !char.IsControl(character) && !char.IsWhiteSpace(character))
        {
            _view.StartAction();
            _view.ShowNotice($"{character}: not bound; press : for commands or ? for help", OwnerConsoleNoticeSeverity.Failure, OwnerConsoleNoticeSource.Action);
        }
    }
}
