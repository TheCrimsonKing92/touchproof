using Terminal.Gui.Input;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleCommandLineKeys(IOwnerConsoleKeySurface _view,
    OwnerConsoleScreenController _controller, OwnerConsoleScreenOperation _operation)
{
    internal async Task<bool> HandleAsync(Key key)
    {
        if (_view.EditingCommand || _view.CommandLine.HasFocus)
        {
            if (key == Key.Esc) { key.Handled = true; FinishCommand(); return true; }
            if (key == Key.Tab)
            {
                key.Handled = true;
                var completion = OwnerConsoleCommandCompletion.Complete(_view.CommandLine.Text, OwnerConsoleBindings.Commands,
                    _view.HintWidth > 0 ? _view.HintWidth : 80);
                _view.CommandLine.Text = completion.Text;
                _view.CommandLine.InsertionPoint = _view.CommandLine.Text.Length;
                _view.CompletionHint = completion.Hint;
                _view.RenderHints();
                return true;
            }
            if (key != Key.Enter || _view.ActionRunning) return true;
            key.Handled = true;
            var line = _view.CommandLine.Text;
            FinishCommand();
            if (OwnerConsoleBindings.ForLine(line) is { Owner: OwnerConsoleCommandOwner.View } binding)
                await _view.RunBindingAsync(binding, line.Trim().TrimStart(':').TrimStart()
                    .Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1));
            else if (!(OwnerConsoleBindings.ForLine(line)?.Command == "goal" && _view.RefuseSelectedBoardRow()))
                await _view.ActAsync(ct => _controller.RunCommandAsync(line, _operation, ct));
            return true;
        }
        return false;
    }

    private void FinishCommand()
    {
        _view.EditingCommand = false;
        _view.CompletionHint = null;
        _view.CommandLine.Text = string.Empty;
        (_view.CommandReturnFocus ?? _view.DecisionsPane).SetFocus();
        _view.RenderHints();
    }
}
