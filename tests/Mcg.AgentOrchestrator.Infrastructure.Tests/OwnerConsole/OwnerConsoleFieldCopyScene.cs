using System.Drawing;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Terminal.Gui.App;

// Instance-only fixtures; no initialized driver or external clipboard.
internal sealed class OwnerConsoleFieldCopyScene : IDisposable
{
    internal const string FirstId = "11111111111111111111111111111111";
    internal const string SecondId = "22222222222222222222222222222222";
    internal readonly OwnerConsoleHarness Harness = new();
    internal readonly IApplication App = Application.Create();
    internal readonly List<string> Copies = [];
    internal readonly Dialogs Dialog;
    internal readonly OwnerConsoleScreenController Controller;
    internal readonly OwnerConsoleFullScreenView View;
    internal OwnerConsoleViewModel Model;

    internal OwnerConsoleFieldCopyScene(IOwnerConsoleEpicSource? epics = null)
    {
        Harness.AddGoal(FirstId, "First", AgentRole.Developer);
        Harness.AddGoal(SecondId, "Second", AgentRole.Tester);
        Dialog = new(new(value => { Copies.Add(value); return true; }));
        Controller = OwnerConsoleControllerFactory.Create(Harness.Questions, Harness.Answers, Dialog,
            Harness.State, Harness.Tail, Harness.Conductor, Harness.DigestReport, Harness.Digest, Harness.Clock, epics);
        View = new(App, Controller, () => Task.CompletedTask, clipboard: Dialog.Clipboard);
        Model = new(new(false, 2, 0, 0, null, 0), [], [
            new("11111111", "Epic sentinel", "  Unicode 日本語 🦊\nsecond line  " + new string('x', 100), "Active", "Developer", TimeSpan.FromMinutes(3), FirstId, Priority: "Critical"),
            new("22222222", "Other epic", "Second title", "Active", "Tester", null, SecondId)], []);
        View.BoardPane.Frame = new Rectangle(0, 0, 180, 15);
        View.Render(Model);
    }

    public void Dispose() { View.Dispose(); Controller.Dispose(); Dialog.Fields?.Dispose(); App.Dispose(); }

    internal sealed class Dialogs(OwnerConsoleClipboard clipboard) : IOwnerConsoleDialogs
    {
        internal OwnerConsoleClipboard Clipboard { get; } = clipboard;
        internal OwnerConsoleFieldsDialog? Fields;
        internal int TextOpens;
        internal int FieldOpens;
        internal TaskCompletionSource? Hold;
        public Task<bool> ConfirmAsync(string title, string text) => throw new InvalidOperationException("Copy must not confirm an action.");
        public Task<string?> PromptTextAsync(string title, string text) => throw new InvalidOperationException("Copy must not answer.");
        public Task ShowTextAsync(string title, string text) { TextOpens++; return Task.CompletedTask; }
        public Task ShowFieldsAsync(OwnerConsoleFieldSnapshot snapshot, string? selectedKey = null)
        {
            FieldOpens++;
            Fields?.Dispose(); Fields = new(snapshot, Clipboard, selectedKey);
            if (Hold is { } hold) Fields.CloseRequested += () => hold.TrySetResult();
            return Hold?.Task ?? Task.CompletedTask;
        }
    }
}
