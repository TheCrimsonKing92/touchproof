using Terminal.Gui.Input;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleDialogKeys(IOwnerConsoleKeySurface _view, OwnerConsoleScreenController _controller)
{
    internal async Task<bool> HandleAsync(Key key)
    {
        if (_view.GateView.IsOpen)
        {
            if (key == OwnerConsoleBindings.OpenFields)
            {
                key.Handled = true;
                if (_view.GateView.SelectedFields is { } fields) await _controller.ShowFieldsAsync(fields);
                else _view.GateView.FieldsUnavailable();
                _view.GateView.BodyPane.SetFocus(); return true;
            }
            if (_view.GateView.HandleKey(key)) _view.FocusPane(_view.GateView.ReturnPane);
            return true;
        }
        if (_view.EpicView.IsOpen)
        {
            if (key == OwnerConsoleBindings.OpenFields)
            {
                key.Handled = true;
                if (_view.EpicView.SelectedFields is { } fields) await _controller.ShowFieldsAsync(fields);
                else _view.EpicView.FieldsUnavailable();
                _view.EpicView.BodyPane.SetFocus(); return true;
            }
            if (await _view.EpicView.HandleKeyAsync(key)) _view.FocusPane(_view.EpicView.ReturnPane);
            return true;
        }
        return false;
    }
}
