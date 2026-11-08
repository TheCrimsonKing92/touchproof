using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class TerminalGuiOwnerConsoleDialogs(IApplication app, CancellationToken token,
    OwnerConsoleClipboard? clipboard = null) : IOwnerConsoleDialogs
{
    private readonly OwnerConsoleClipboard _clipboard = clipboard ?? OwnerConsoleClipboard.For(app);
    public Task ShowFieldsAsync(OwnerConsoleFieldSnapshot snapshot, string? selectedKey = null) => OnUiAsync(() =>
    {
        using var dialog = new OwnerConsoleFieldsDialog(snapshot, _clipboard, selectedKey);
        void Close() => app.RequestStop(dialog);
        void Handle(object? sender, Key key) { if (app.TopRunnableView == dialog) dialog.HandleKey(key); }
        dialog.CloseRequested += Close;
        using var cancellation = token.Register(() => { if (app.Initialized) app.Invoke(Close); });
        var keyboard = app.Keyboard;
        if (keyboard is not null) keyboard.KeyDown += Handle;
        try { app.Run(dialog); }
        finally
        {
            if (keyboard is not null) keyboard.KeyDown -= Handle;
            dialog.CloseRequested -= Close;
        }
        return true;
    });

    public Task<int?> ShowStructuredAsync(string title, string text, OwnerConsoleStructuredText fields,
        IReadOnlyList<int>? choiceLines = null) => ShowPageCoreAsync(title, text, choiceLines, fields);
    private readonly int _uiThread = Environment.CurrentManagedThreadId;
    public int? TextWidth => OwnerConsoleDialogWidth.TextColumns(app.Screen.Width);

    public Task<bool> ConfirmAsync(string title, string text) => OnUiAsync(() =>
    {
        using var dialog = new OwnerConsoleTextDialog(title, text);
        var accepted = false;
        var accept = new Button { Text = "Accept", IsDefault = true };
        var cancel = new Button { Text = "Cancel" };
        accept.Accepting += (_, args) => { args.Handled = true; accepted = true; app.RequestStop(dialog); };
        cancel.Accepting += (_, args) => { args.Handled = true; app.RequestStop(dialog); };
        dialog.AddButton(accept); dialog.AddButton(cancel);
        Run(dialog);
        return accepted;
    });

    public async Task ShowTextAsync(string title, string text) => await ShowPageAsync(title, text);

    public async Task ShowDecisionAsync(OwnerConsoleDecisionDetail detail) => await OnUiAsync(() =>
    {
        using var dialog = new OwnerConsoleDecisionDialog(detail, action => app.Invoke(action));
        void Fields(OwnerConsoleFieldSnapshot fields) { _ = ShowFieldsAsync(fields); dialog.BodyPane.SetFocus(); }
        dialog.FieldsRequested += Fields;
        dialog.Closed += () => app.RequestStop(dialog);
        void Handle(object? sender, Key key)
        { if (app.TopRunnableView == dialog) _ = dialog.HandleKeyAsync(key); }
        var keyboard = app.Keyboard;
        if (keyboard is not null) keyboard.KeyDown += Handle;
        try { app.Run(dialog); }
        finally { if (keyboard is not null) keyboard.KeyDown -= Handle; dialog.FieldsRequested -= Fields; }
        return true;
    });

    public Task<int?> ShowPageAsync(string title, string text, IReadOnlyList<int>? choiceLines = null) => OnUiAsync(() =>
        ShowPageCore(title, text, choiceLines, null));

    public Task ShowFleetAsync(OwnerFleetViewKeys keys, OwnerFleetViewModel model, int width) => OnUiAsync(() =>
    {
        var rendered = keys.Render(model, width);
        using var dialog = new OwnerConsoleTextDialog("Fleet", rendered.Text) { Structured = rendered.Fields };
        dialog.CloseRequested += () => app.RequestStop(dialog);
        var close = new Button { Text = "Close", IsDefault = true };
        close.Accepting += (_, args) => { args.Handled = true; app.RequestStop(dialog); };
        dialog.AddButton(close);
        Run(dialog, key =>
        {
            if (key.IsAlt || !keys.Handle((char)key.AsRune.Value, key.IsCtrl)) return false;
            var updated = keys.Render(model, width);
            dialog.Page.Replace(updated.Text);
            dialog.Structured = updated.Fields;
            dialog.Render();
            return true;
        });
        return true;
    });

    private Task<int?> ShowPageCoreAsync(string title, string text, IReadOnlyList<int>? choiceLines,
        OwnerConsoleStructuredText? fields) => OnUiAsync(() => ShowPageCore(title, text, choiceLines, fields));

    private int? ShowPageCore(string title, string text, IReadOnlyList<int>? choiceLines, OwnerConsoleStructuredText? fields)
    {
        using var dialog = new OwnerConsoleTextDialog(title, text, choiceLines);
        dialog.Structured = fields;
        int? selected = null;
        dialog.ChoiceAccepted += choice => { selected = choice; app.RequestStop(dialog); };
        dialog.CloseRequested += () => app.RequestStop(dialog);
        var close = new Button { Text = "Close", IsDefault = true };
        close.Accepting += (_, args) => { args.Handled = true; app.RequestStop(dialog); };
        dialog.AddButton(close);
        Run(dialog);
        return selected;
    }

    public Task<string?> PromptTextAsync(string title, string text) => OnUiAsync(() =>
    {
        using var dialog = new OwnerConsoleTextDialog(title, text);
        dialog.Body.Height = Dim.Fill(4);
        var input = new TextField { Y = Pos.Bottom(dialog.Body), Width = Dim.Fill(), Height = 1 };
        string? result = null;
        var submit = new Button { Text = "Submit", IsDefault = true };
        var cancel = new Button { Text = "Cancel" };
        submit.Accepting += (_, args) => { args.Handled = true; result = input.Text; app.RequestStop(dialog); };
        cancel.Accepting += (_, args) => { args.Handled = true; app.RequestStop(dialog); };
        dialog.Add(input);
        dialog.AddButton(submit);
        dialog.AddButton(cancel);
        dialog.Initialized += (_, _) => input.SetFocus();
        Run(dialog);
        return result;
    });

    public Task<OwnerExperimentDecisionForm?> PromptExperimentDecisionAsync(string title, string text, string defaultEvidence) =>
        OnUiAsync(() =>
        {
            using var dialog = new OwnerConsoleExperimentDecisionDialog(title, text, defaultEvidence);
            dialog.CloseRequested += () => app.RequestStop(dialog);
            app.Run(dialog);
            return dialog.Result;
        });

    public async Task ShowGoalAsync(OwnerConsoleGoalDialog goal) => await OnUiAsync(() =>
    {
        var acting = false;
        OwnerConsoleTextDialog? built = null;
        var construction = goal.CreateView(() => app.RequestStop(built!), key => ActAsync(() => goal.HandleKeyAsync(key)));
        using var dialog = construction.Dialog;
        built = dialog;
        var buttons = construction.Buttons;
        void Refresh()
        {
            dialog.Structured = goal.Current.Fields;
            foreach (var button in buttons.Values) button.Visible = goal.Actions.Contains(button.Text.ToString());
            dialog.TitleStatus = goal.TitleStatus;
            dialog.Render();
        }
        void Close() => app.RequestStop(dialog);
        async Task ActAsync(Func<Task> action)
        {
            if (acting) return;
            acting = true;
            try { await action(); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { await ShowTextAsync("Goal action failed", ex.Message); }
            finally { acting = false; }
        }
        async void Handle(object? sender, Key key)
        {
            if (app.TopRunnableView != dialog) return;
            if (key == OwnerConsoleBindings.OpenFields && dialog.Body.HasFocus)
            { dialog.HandleKey(key); return; }
            var character = char.ToLowerInvariant((char)key.AsRune.Value);
            var binding = OwnerConsoleBindings.ForDialogKey(OwnerConsoleScope.GoalDetail, character, key.IsCtrl);
            if (!key.IsAlt && binding is not null && (binding.Command == "quit" ||
                buttons.TryGetValue(character, out var button) && button.Visible))
            { key.Handled = true; await ActAsync(() => goal.HandleKeyAsync(character)); }
            else if (dialog.Body.HasFocus) dialog.HandleKey(key);
        }
        dialog.ChoiceAccepted += async index => await ActAsync(() => goal.OpenResolutionAsync(index));
        void Fields(OwnerConsoleFieldSnapshot fields) { _ = ShowFieldsAsync(fields, dialog.SelectedFieldKey); dialog.Body.SetFocus(); }
        dialog.FieldsRequested += Fields;
        goal.Changed += Refresh; goal.CloseRequested += Close;
        goal.Start(action => { if (app.Initialized) app.Invoke(action); else action(); });
        Refresh();
        var keyboard = app.Keyboard;
        if (keyboard is not null) keyboard.KeyDown += Handle;
        try { app.Run(dialog); }
        finally
        {
            if (keyboard is not null) keyboard.KeyDown -= Handle;
            goal.Changed -= Refresh; goal.CloseRequested -= Close;
            dialog.FieldsRequested -= Fields;
        }
        return true;
    });

    private void Run(OwnerConsoleTextDialog dialog, Func<Key, bool>? handle = null)
    {
        void Fields(OwnerConsoleFieldSnapshot fields) { _ = ShowFieldsAsync(fields, dialog.SelectedFieldKey); dialog.Body.SetFocus(); }
        dialog.FieldsRequested += Fields;
        void Handle(object? sender, Key key)
        {
            if (app.TopRunnableView != dialog) return;
            if (handle?.Invoke(key) == true) key.Handled = true;
            else dialog.HandleFocusedKey(key);
        }
        var keyboard = app.Keyboard;
        if (keyboard is not null) keyboard.KeyDown += Handle;
        try { app.Run(dialog); }
        finally { if (keyboard is not null) keyboard.KeyDown -= Handle; dialog.FieldsRequested -= Fields; }
    }

    private Task<T> OnUiAsync<T>(Func<T> action)
    {
        token.ThrowIfCancellationRequested();
        if (Environment.CurrentManagedThreadId == _uiThread) return Task.FromResult(action());
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellation = token.Register(() => completion.TrySetCanceled(token));
        app.Invoke(() =>
        {
            try { if (!token.IsCancellationRequested) completion.TrySetResult(action()); }
            catch (Exception ex) { completion.TrySetException(ex); }
            finally { cancellation.Dispose(); }
        });
        return completion.Task;
    }
}
