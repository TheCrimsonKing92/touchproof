using System.Drawing;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;

// Parallel-safe: real layout on instance-owned views, injected clipboard, no application globals.
public sealed class OwnerActivityDetailDialogTests
{
    [Theory]
    [InlineData(60)]
    [InlineData(100)]
    [InlineData(160)]
    public void LayoutResizeScrollAndF2PreserveFieldIdentityAndFullValues(int columns)
    {
        var detail = OwnerActivityNarrator.ExplainWithFields(OwnerActivityDetailFixture.Failure());
        using var dialog = new OwnerConsoleTextDialog("What this means", detail.Text) { Structured = detail.Structured };
        dialog.ShadowStyle = ShadowStyles.Transparent;
        dialog.Layout(new Size(columns, 24));
        var width = dialog.Body.Viewport.Width;
        var height = dialog.Body.Viewport.Height;
        Assert.Equal(OwnerConsoleDialogWidth.TextColumns(columns), width);
        Assert.True(height > 0);
        AssertLayout(dialog, detail.Text, width);
        var key = "event/0/diagnostic/detail/0";
        var line = Assert.Single(detail.Structured.FieldKeys!.Where(pair => pair.Value == key)).Key;
        var rows = Enumerable.Range(0, dialog.Page.Lines.Count).Where(row => dialog.Page.SourceLines[row] == line).ToArray();
        Assert.True(rows.Length > 1, "The retained detail must actually wrap at every requested viewport width.");
        dialog.Page.Select(rows[^1]); dialog.Render();
        var sourceOffset = dialog.Page.SourceOffsets[dialog.Page.Selected];
        Assert.True(sourceOffset > 0);
        Assert.Equal(key, dialog.SelectedFieldKey);
        var writes = new List<string>();
        var opens = 0;
        dialog.FieldsRequested += snapshot =>
        {
            opens++;
            using var modal = new OwnerConsoleFieldsDialog(snapshot, new(value => { writes.Add(value); return true; }), dialog.SelectedFieldKey);
            Assert.Equal(key, modal.SelectedField!.Key);
            Assert.True(modal.HandleKey(OwnerConsoleBindings.CopyField));
            Assert.True(modal.HandleKey(Key.CursorUp)); Assert.True(modal.HandleKey(Key.CursorDown));
            Assert.Equal(key, modal.SelectedField!.Key);
            var closed = 0; modal.CloseRequested += () => closed++;
            Assert.True(modal.HandleKey(Key.Esc)); Assert.Equal(1, closed);
            dialog.Body.SetFocus();
        };
        foreach (var resized in new[] { columns, 160, 60, 100, columns })
        {
            dialog.Layout(new Size(resized, 24));
            AssertLayout(dialog, detail.Text, dialog.Body.Viewport.Width);
            Assert.Equal(line, dialog.Page.SourceLines[dialog.Page.Selected]);
            Assert.Equal(key, dialog.SelectedFieldKey);
            Assert.Same(detail.Fields, dialog.SelectedFields);
            Assert.True(dialog.HandleKey(OwnerConsoleBindings.OpenFields));
            Assert.True(dialog.Body.HasFocus);
        }
        Assert.Equal(sourceOffset, dialog.Page.SourceOffsets[dialog.Page.Selected]);
        Assert.Equal(5, opens);
        var value = detail.Fields.Fields.Single(field => field.Key == key).Value;
        Assert.All(writes, copied => Assert.Equal(value, copied));
        Assert.Equal(5, writes.Count);
        Assert.True(dialog.HandleKey(Key.CursorUp)); Assert.True(dialog.HandleKey(Key.CursorDown));
        Assert.True(dialog.HandleKey(Key.PageUp)); Assert.True(dialog.HandleKey(Key.PageDown));
        for (var row = 0; row < dialog.Page.Lines.Count; row++) dialog.HandleKey(Key.PageDown);
        Assert.Equal(dialog.Page.Lines.Count - 1, dialog.Page.Selected);
        Assert.EndsWith("pattern\"", dialog.Page.Visible[^1]);
        var closes = 0; dialog.CloseRequested += () => closes++;
        Assert.True(dialog.HandleKey(Key.Enter)); Assert.True(dialog.HandleKey(Key.Esc));
        Assert.Equal(2, closes);
        dialog.Dispose();
        Assert.False(dialog.HandleKey(OwnerConsoleBindings.OpenFields));
        Assert.False(dialog.HandleKey(Key.Enter)); Assert.False(dialog.HandleKey(Key.Esc));
        Assert.Equal(5, opens); Assert.Equal(2, closes);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(100)]
    [InlineData(160)]
    public void MultilineEmptyAndUnknownRowsCopyLogicalValuesAtRealSelectionBoundary(int columns)
    {
        const string raw = "opaque unknown = none_\r\n  second = \"x\"  ";
        const string value = "  leading none_ = 日本語 \"x\"\r\nsecond " +
            "12345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890";
        var evidence = OwnerActivityDetailEvidence.From(raw, new("value", "Opaque value", value), new("empty", "Empty", ""));
        var source = new OwnerConductEvent(DateTimeOffset.UnixEpoch, "unknown", "full-goal", "", evidence);
        var item = new OwnerConsoleActivityItem(source.Timestamp, "unknown", "tag", "full-goa", "", Evidence: [source]);
        var detail = OwnerActivityNarrator.ExplainWithFields(item);
        using var dialog = new OwnerConsoleTextDialog("What this means", detail.Text) { Structured = detail.Structured };
        dialog.Layout(new Size(columns, 24));
        AssertLayout(dialog, detail.Text, dialog.Body.Viewport.Width);
        var writes = new List<string>();
        Assert.DoesNotContain(detail.Fields.Fields, field => field.Key == "event/0/empty");
        foreach (var key in new[] { "event/0/value", "event/0/original" })
        {
            var sourceLine = detail.Structured.FieldKeys!.Last(pair => pair.Value == key).Key;
            dialog.Page.Select(dialog.Page.SourceLines.ToList().FindLastIndex(line => line == sourceLine));
            void Open(OwnerConsoleFieldSnapshot fields)
            {
                using var modal = new OwnerConsoleFieldsDialog(fields, new(text => { writes.Add(text); return true; }), dialog.SelectedFieldKey);
                modal.CopyButton.InvokeCommand(Command.Accept);
            }
            dialog.FieldsRequested += Open;
            dialog.HandleKey(OwnerConsoleBindings.OpenFields);
            dialog.FieldsRequested -= Open;
        }
        Assert.Equal([value, raw], writes);
    }

    private static void AssertLayout(OwnerConsoleTextDialog dialog, string text, int width)
    {
        Assert.All(dialog.Page.Lines, row => Assert.True(row.GetColumns() <= width, row));
        var source = text.Split('\n');
        foreach (var group in Enumerable.Range(0, dialog.Page.Lines.Count).GroupBy(row => dialog.Page.SourceLines[row]))
        {
            var rows = group.ToArray();
            Assert.All(rows.Skip(1), row => Assert.StartsWith("    ", dialog.Page.Lines[row]));
            var reconstructed = string.Concat(rows.Select((row, index) => index == 0 ? dialog.Page.Lines[row] : dialog.Page.Lines[row][4..]));
            Assert.Equal(source[group.Key], reconstructed); // Full terminal text, not clipped prefixes.
        }
        Assert.All(dialog.Structured!.FieldKeys!, pair => Assert.True(source[pair.Key].StartsWith("    ") || source[pair.Key].Contains(": ")));
        Assert.Contains("\n\nReason: ", text); Assert.Contains("\n\nDiagnostics\n", text);
        Assert.NotEmpty(dialog.Body.Source!.ToList());
    }
}
