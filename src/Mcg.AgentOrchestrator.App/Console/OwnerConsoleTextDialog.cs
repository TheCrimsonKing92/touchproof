using System.Collections.ObjectModel;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleTextDialog : Dialog
{
    internal ListView Body { get; } = new() { Width = Dim.Fill(), Height = Dim.Fill(1) };
    internal OwnerConsoleTextPage Page { get; }
    internal event Action<int>? ChoiceAccepted;
    internal event Action? CloseRequested;
    private OwnerConsoleStructuredText? _structured;
    private readonly Label _fieldsHint = new() { Y = Pos.AnchorEnd(2), Width = Dim.Fill(), Height = 1, Text = "F2 fields (snapshot)", Visible = false };
    internal OwnerConsoleStructuredText? Structured
    {
        get => _structured;
        set
        {
            _structured = value; _fieldsHint.Visible = value is not null; Body.Height = Dim.Fill(value is null ? 1 : 2);
            _fieldsHint.Text = value?.FooterHint ?? "F2 fields (snapshot)";
            Page.ContinuationIndent = value?.ContinuationIndent ?? 0;
        }
    }
    internal event Action<OwnerConsoleFieldSnapshot>? FieldsRequested;
    internal OwnerConsoleFieldSnapshot? SelectedFields => Page.SourceLines.Count == 0 ? null :
        Structured?.At(Page.SourceLines[Page.Selected]);
    internal string? SelectedFieldKey => Page.SourceLines.Count == 0 ? null :
        Structured?.FieldKeys?.GetValueOrDefault(Page.SourceLines[Page.Selected]);
    private readonly string _baseTitle;
    private readonly OwnerConsoleListRenderer _source = new();
    private readonly OwnerConsoleMarkedText? _markedText;
    internal string? TitleStatus { get; set; }
    private bool _formatting;
    private bool _disposed;

    internal OwnerConsoleTextDialog(string title, string text, IReadOnlyList<int>? choiceLines = null,
        OwnerConsoleTextPage? page = null)
        : this(title, text, choiceLines, page, null) { }

    internal OwnerConsoleTextDialog(string title, OwnerConsoleMarkedText text)
        : this(title, text.PlainText, null, null, text) { }

    private OwnerConsoleTextDialog(string title, string text, IReadOnlyList<int>? choiceLines,
        OwnerConsoleTextPage? page, OwnerConsoleMarkedText? markedText)
    {
        _baseTitle = title;
        _markedText = markedText;
        Title = title; Width = Dim.Percent(OwnerConsoleDialogWidth.Percent); Height = Dim.Percent(80);
        Page = page ?? new(text, 60, 15, choiceLines);
        Add(Body, _fieldsHint);
        Body.ValueChanged += (_, _) =>
        { if (!_formatting && Body.SelectedItem is { } row) Page.Select(Page.Offset + row); };
        Body.ViewportChanged += (_, _) => Format(Body.Viewport.Width, Body.Viewport.Height);
        Initialized += (_, _) => Body.SetFocus();
        Format(60, 15);
    }

    internal void AddConditionalButton(Button button)
    {
        AddButton(button);
        var alignment = button.X;
        // Terminal.Gui includes invisible Pos.Align views in the button row's width.
        button.VisibleChanged += (_, _) => button.X = button.Visible ? alignment : Pos.Absolute(0);
        if (!button.Visible) button.X = Pos.Absolute(0);
    }

    internal void Format(int width, int height)
    {
        if (_formatting) return;
        _formatting = true;
        try { Page.Resize(width, height); Render(); }
        finally { _formatting = false; }
    }

    internal bool HandleKey(Key key)
    {
        if (_disposed) return false;
        if (key == OwnerConsoleBindings.OpenFields && Structured is not null)
        {
            if (SelectedFields is { } fields) FieldsRequested?.Invoke(fields);
            else { TitleStatus = "No selected structured item"; Render(); }
            key.Handled = true; return true;
        }
        if (key == Key.Enter && Page.Choice is { } choice)
        { ChoiceAccepted?.Invoke(choice); key.Handled = true; return true; }
        if ((key == Key.Esc || key == Key.Enter) && CloseRequested is not null)
        { CloseRequested.Invoke(); key.Handled = true; return true; }
        if (!Page.HandleKey(key)) return false;
        Render(); key.Handled = true; return true;
    }
    internal bool HandleFocusedKey(Key key) => Body.HasFocus && HandleKey(key);

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _disposed = true; FieldsRequested = null; ChoiceAccepted = null; CloseRequested = null; }
        base.Dispose(disposing);
    }

    internal void Render()
    {
        var wasFormatting = _formatting;
        _formatting = true;
        try
        {
            var changed = _markedText is null ? _source.Apply(Body, Page.Visible) : ApplyMarkedSource(_markedText);
            var selected = Page.Selected - Page.Offset;
            if (changed || Body.SelectedItem != selected) Body.SelectedItem = selected;
            var title = OwnerConsoleOverflow.Title(_baseTitle + (TitleStatus is null ? "" : $" ({TitleStatus})"),
                Page.Overflow, Frame.Width - OwnerConsoleOverflow.TitleChrome);
            if (Title != title) Title = title;
        }
        finally { _formatting = wasFormatting; }
    }

    private bool ApplyMarkedSource(OwnerConsoleMarkedText text)
    {
        if (Body.Source is MarkedListSource source && source.Matches(Page)) return false;
        Body.Source = new MarkedListSource(Page, text);
        return true;
    }

    private sealed class MarkedListSource(OwnerConsoleTextPage page, OwnerConsoleMarkedText text)
        : ListWrapper<string>(new ObservableCollection<string>(page.Visible)), IListDataSource
    {
        private readonly IReadOnlyList<string> _lines = page.Lines;
        private readonly IReadOnlyList<int> _sourceLines = page.SourceLines;
        private readonly IReadOnlyList<int> _sourceOffsets = page.SourceOffsets;
        private readonly int _offset = page.Offset;

        internal bool Matches(OwnerConsoleTextPage current) =>
            ReferenceEquals(_lines, current.Lines) && _offset == current.Offset;

        void IListDataSource.Render(ListView list, bool selected, int item, int col, int row, int width, int viewportX)
        {
            var wrappedRow = _offset + item;
            var keys = text.KeysAt(_sourceLines[wrappedRow]);
            if (keys.Count == 0)
            {
                base.Render(list, selected, item, col, row, width, viewportX);
                return;
            }

            var normal = list.GetCurrentAttribute();
            var bold = new Attribute(normal.Foreground, normal.Background, normal.Style | TextStyle.Bold);
            var sourceOffset = _sourceOffsets[wrappedRow];
            var column = 0;
            list.Move(Math.Max(0, col - viewportX), row);
            foreach (var grapheme in GraphemeHelper.GetGraphemes(_lines[wrappedRow]))
            {
                var start = sourceOffset;
                sourceOffset += grapheme.Length;
                // Match the standard list renderer's zero-width tabs.
                if (grapheme == "\t") continue;
                var columns = grapheme.GetColumns();
                if (column >= viewportX)
                {
                    if (columns > width) break;
                    var isKey = keys.Any(key => key.Start.Value <= start && start < key.End.Value);
                    list.SetAttribute(isKey ? bold : normal);
                    list.AddStr(grapheme);
                    width -= columns;
                }
                column += columns;
            }
            list.SetAttribute(normal);
            if (width > 0) list.AddStr(new string(' ', width));
        }
    }
}
