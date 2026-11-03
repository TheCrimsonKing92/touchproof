using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleFieldsDialog : Dialog
{
    private readonly OwnerConsoleClipboard _clipboard;
    internal OwnerConsoleFieldSnapshot Snapshot { get; }
    internal ListView Fields { get; } = new() { Width = Dim.Percent(35), Height = Dim.Fill(3) };
    internal TextView Value { get; } = new() { X = Pos.Percent(35), Width = Dim.Fill(), Height = Dim.Fill(3), ReadOnly = true };
    private readonly Label _feedback = new() { Y = Pos.AnchorEnd(3), Width = Dim.Fill(), Height = 1 };
    internal string Feedback => _feedback.Text;
    internal Button CopyButton { get; } = new() { Text = "Copy" };
    internal event Action? CloseRequested;
    private bool _disposed;

    internal OwnerConsoleFieldsDialog(OwnerConsoleFieldSnapshot snapshot, OwnerConsoleClipboard clipboard, string? selectedKey = null)
    {
        Snapshot = snapshot with { Fields = [.. snapshot.Fields] };
        _clipboard = clipboard;
        Title = "Fields (snapshot; reopen to refresh)";
        Width = Dim.Percent(90); Height = Dim.Percent(80);
        Fields.SetSource(new System.Collections.ObjectModel.ObservableCollection<string>(
            Snapshot.Fields.Select(field => field.Label + (field.Available ? "" : " (unavailable)"))));
        Fields.SelectedItem = Snapshot.Fields.Length == 0 ? null : Math.Max(0,
            Array.FindIndex(Snapshot.Fields.ToArray(), field => field.Key == selectedKey));
        Fields.ValueChanged += SelectionChanged;
        Add(Fields, Value, _feedback, new Label { Y = Pos.AnchorEnd(2), Width = Dim.Fill(), Text = "Up/Down field  Ctrl+C copy  Esc close" });
        AddButton(CopyButton);
        var close = new Button { Text = "Close" }; AddButton(close);
        CopyButton.Accepting += (_, args) => { args.Handled = true; Copy(); };
        close.Accepting += (_, args) => { args.Handled = true; CloseRequested?.Invoke(); };
        Initialized += (_, _) => Fields.SetFocus();
        Preview();
    }

    private void SelectionChanged(object? sender, Terminal.Gui.App.ValueChangedEventArgs<int?> args) => Preview();
    private void Preview() => Value.Text = SelectedField is { } field ? field.Value ?? "Value unavailable." : "No selected field.";
    internal OwnerConsoleField? SelectedField => Fields.SelectedItem is { } index && index >= 0 && index < Snapshot.Fields.Length
        ? Snapshot.Fields[index] : null;
    internal void Copy() { if (!_disposed) _feedback.Text = _clipboard.Copy(SelectedField).Message; }

    internal bool HandleKey(Key key)
    {
        if (_disposed) return false;
        if (key == OwnerConsoleBindings.CopyField) Copy();
        else if (key == Key.Esc) CloseRequested?.Invoke();
        else if (key == Key.CursorUp || key == Key.CursorDown)
        {
            if (Snapshot.Fields.Length > 0)
                Fields.SelectedItem = Math.Clamp((Fields.SelectedItem ?? 0) + (key == Key.CursorUp ? -1 : 1), 0, Snapshot.Fields.Length - 1);
        }
        else return false;
        key.Handled = true; return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _disposed = true; Fields.ValueChanged -= SelectionChanged; CloseRequested = null; }
        base.Dispose(disposing);
    }
}
