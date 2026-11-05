using Mcg.AgentOrchestrator.App.OwnerConsole;
using Terminal.Gui.Input;

// Parallel-safe: isolated dialogs and injected clipboard callbacks.
public sealed class OwnerConsoleFieldsDialogTests
{
    [Fact]
    public void NamedSelectionCopiesUnicodeMultilineAndOmitsEmptyFields()
    {
        var copies = new List<string>();
        var snapshot = OwnerConsoleFieldSnapshot.Create("identity",
            new("title", "Title", " 日本語 🦊\nsecond line\t "), new("empty", "Empty", ""), new("missing", "Missing", null));
        using var dialog = new OwnerConsoleFieldsDialog(snapshot, new(value => { copies.Add(value); return true; }));
        Assert.True(dialog.Value.ReadOnly);
        Assert.True(dialog.Value.WordWrap);
        dialog.HandleKey(OwnerConsoleBindings.CopyField);
        dialog.HandleKey(Key.CursorDown);
        dialog.CopyButton.InvokeCommand(Command.Accept);
        dialog.HandleKey(Key.CursorDown);
        dialog.HandleKey(OwnerConsoleBindings.CopyField);
        Assert.Equal(Enumerable.Repeat(" 日本語 🦊\nsecond line\t ", 3), copies);
        Assert.Equal("title", Assert.Single(dialog.Snapshot.Fields).Key);
        Assert.Equal("title", dialog.SelectedField?.Key);
        Assert.StartsWith("Copied", dialog.Feedback);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FalseAndExceptionKeepSelectionAndReportFailure(bool throws)
    {
        var calls = 0;
        using var dialog = new OwnerConsoleFieldsDialog(OwnerConsoleFieldSnapshot.Create("a", new OwnerConsoleField("title", "Title", "value")),
            new(_ => { calls++; if (throws) throw new IOException("refused"); return false; }));
        dialog.HandleKey(OwnerConsoleBindings.CopyField);
        Assert.Equal(1, calls);
        Assert.Equal("title", dialog.SelectedField?.Key);
        Assert.StartsWith("Copy failed", dialog.Feedback);
        Assert.DoesNotContain("Copied", dialog.Feedback);
    }

    [Fact]
    public void UninitializedApplicationClipboardAndNoSelectionAreTruthful()
    {
        using var app = Terminal.Gui.App.Application.Create();
        using var dialog = new OwnerConsoleFieldsDialog(OwnerConsoleFieldSnapshot.Create("a", new OwnerConsoleField("id", "ID", "a")), OwnerConsoleClipboard.For(app));
        dialog.HandleKey(OwnerConsoleBindings.CopyField);
        Assert.StartsWith("Copy failed", dialog.Feedback);
        using var empty = new OwnerConsoleFieldsDialog(OwnerConsoleFieldSnapshot.Create("none"), new(_ => throw new Exception("must not write")));
        empty.Copy();
        Assert.Equal("No selected field.", empty.Feedback);
    }

    [Fact]
    public void EscAndDisposeDetachCallbacksAndStopCopying()
    {
        var closed = 0; var writes = 0;
        var dialog = new OwnerConsoleFieldsDialog(OwnerConsoleFieldSnapshot.Create("a", new OwnerConsoleField("id", "ID", "a")), new(_ => { writes++; return true; }));
        dialog.CloseRequested += () => closed++;
        Assert.True(dialog.HandleKey(Key.Esc));
        Assert.Equal(1, closed);
        dialog.Dispose();
        Assert.False(dialog.HandleKey(Key.Esc));
        dialog.Copy();
        Assert.Equal(1, closed); Assert.Equal(0, writes);
    }
}
