using System.Text;
using Terminal.Gui.Input;
using Terminal.Gui.Text;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Full text belongs to the model; wrapping and the viewport belong to this dialog instance.
internal sealed class OwnerConsoleTextPage
{
    private string _text;
    private IReadOnlyList<int> _choiceLines;
    private int _width;
    private int _height;
    private int _anchorSource;
    private int _anchorOffset;
    internal IReadOnlyList<string> Lines { get; private set; } = [];
    internal IReadOnlyList<int> SourceLines { get; private set; } = [];
    internal IReadOnlyList<int> SourceOffsets { get; private set; } = [];
    internal int ContinuationIndent { get; set; }
    internal IReadOnlyList<string> Visible => Lines.Skip(Offset).Take(_height).ToArray();
    internal int Offset { get; private set; }
    internal OwnerConsoleOverflow Overflow => OwnerConsoleOverflow.Of(Lines.Count, Offset, _height);
    internal int Selected { get; private set; }
    internal int? Choice => _choiceLines.ToList().IndexOf(SourceLines.Count == 0 ? -1 : SourceLines[Selected]) is var index && index >= 0 ? index : null;

    internal OwnerConsoleTextPage(string text, int width, int height, IReadOnlyList<int>? choiceLines = null)
    { _text = text; _choiceLines = choiceLines ?? []; Resize(width, height); }

    internal void Resize(int width, int height)
    {
        width = Math.Max(2, width);
        _width = width;
        _height = Math.Max(1, height);
        var selectedSource = _anchorSource;
        var selectedOffset = _anchorOffset;
        var lines = new List<string>();
        var sources = new List<int>();
        var offsets = new List<int>();
        var source = 0;
        foreach (var paragraph in _text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var start = 0;
            do
            {
                var indent = start == 0 ? 0 : Math.Min(ContinuationIndent, width - 2);
                var cells = indent;
                var length = 0;
                var space = 0;
                foreach (var rune in paragraph.AsSpan(start).EnumerateRunes())
                {
                    if (cells + rune.GetColumns() > width) break;
                    cells += rune.GetColumns();
                    length += rune.Utf16SequenceLength;
                    if (rune.Value == ' ') space = length;
                }
                if (start + length < paragraph.Length && space > 0) length = space;
                lines.Add(new string(' ', indent) + paragraph.Substring(start, length));
                sources.Add(source); offsets.Add(start);
                start += length;
            } while (start < paragraph.Length);
            source++;
        }
        Lines = lines; SourceLines = sources; SourceOffsets = offsets;
        var selected = sources.FindLastIndex(index => index == selectedSource);
        while (selected > 0 && offsets[selected] > selectedOffset && sources[selected - 1] == selectedSource) selected--;
        Select(Math.Max(0, selected));
        _anchorSource = selectedSource; _anchorOffset = selectedOffset;
    }

    internal void Replace(string text, IReadOnlyList<int>? choiceLines = null)
    {
        var offset = Offset;
        _text = text;
        _choiceLines = choiceLines ?? [];
        Resize(_width, _height);
        Offset = Math.Clamp(offset, 0, Math.Max(0, Lines.Count - _height));
    }

    internal void Select(int row)
    {
        Selected = Math.Clamp(row, 0, Math.Max(0, Lines.Count - 1));
        _anchorSource = SourceLines.Count > 0 ? SourceLines[Selected] : 0;
        _anchorOffset = SourceOffsets.Count > 0 ? SourceOffsets[Selected] : 0;
        if (Selected < Offset) Offset = Selected;
        if (Selected >= Offset + _height) Offset = Selected - _height + 1;
        Offset = Math.Clamp(Offset, 0, Math.Max(0, Lines.Count - _height));
    }

    internal bool HandleKey(Key key)
    {
        var delta = key == Key.CursorUp ? -1 : key == Key.CursorDown ? 1 :
            key == Key.PageUp ? -_height : key == Key.PageDown ? _height : 0;
        if (delta == 0) return false;
        Select(Selected + delta);
        return true;
    }
}
