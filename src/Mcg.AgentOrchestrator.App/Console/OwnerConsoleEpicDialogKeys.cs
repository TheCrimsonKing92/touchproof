using Terminal.Gui.Input;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleEpicDialogKeys
{
    internal static async Task<bool> HandleAsync(OwnerConsoleEpicDialog dialog, Key key)
    {
        key.Handled = true;
        var binding = key.IsAlt ? null : OwnerConsoleBindings.ForDialogKey(
            OwnerConsoleScope.EpicView, (char)key.NoCtrl.AsRune.Value, key.IsCtrl);
        if (key == Key.Esc || binding?.Command == "quit")
            return await CloseOrBackAsync(dialog);
        if (binding?.Command == "window")
        {
            dialog.TimeWindow = dialog.TimeWindow switch
            {
                OwnerConsoleEpicWindow.Day => OwnerConsoleEpicWindow.Week,
                OwnerConsoleEpicWindow.Week => OwnerConsoleEpicWindow.AllTime,
                _ => OwnerConsoleEpicWindow.Day
            };
            dialog.BeginLoading();
            await dialog.ReloadAsync();
        }
        else if (binding?.Command == "reload")
        {
            dialog.BeginLoading();
            await dialog.ReloadAsync();
        }
        else if (dialog.ShowingDetail)
        {
            var delta = key == Key.CursorUp ? -1 : key == Key.CursorDown ? 1 :
                key == Key.PageUp ? -Math.Max(1, dialog.BodyPane.Viewport.Height) :
                key == Key.PageDown ? Math.Max(1, dialog.BodyPane.Viewport.Height) : 0;
            dialog._scroll = Math.Clamp(dialog._scroll + delta, 0, Math.Max(0, dialog.Lines.Count - 1));
            dialog.Format(select: true);
        }
        else if (dialog._model is { Epics.Count: > 0, SelectedDetail: null })
        {
            await SelectAsync(dialog, key);
        }
        return false;
    }

    private static async Task<bool> CloseOrBackAsync(OwnerConsoleEpicDialog dialog)
    {
        if (dialog.ShowingDetail)
        {
            dialog.ShowingDetail = false;
            dialog._loadingDetailTitle = null;
            dialog._scroll = 0;
            dialog.BeginLoading();
            await dialog.ReloadAsync();
            return false;
        }
        dialog.Close();
        return true;
    }

    private static async Task SelectAsync(OwnerConsoleEpicDialog dialog, Key key)
    {
        if (key == Key.CursorUp || key == Key.CursorDown)
        {
            dialog._selectedIndex = Math.Clamp(dialog._selectedIndex + (key == Key.CursorUp ? -1 : 1), 0, dialog._model!.Epics.Count - 1);
            dialog.SelectedEpicId = dialog._model.Epics[dialog._selectedIndex].Epic.Id;
            dialog.Format(select: true);
        }
        else if (key == Key.Enter)
        {
            dialog.ShowingDetail = true;
            dialog._loadingDetailTitle = dialog._model!.Epics[dialog._selectedIndex].Epic.Title;
            dialog._scroll = 0;
            dialog.BeginLoading();
            await dialog.ReloadAsync();
        }
    }
}
