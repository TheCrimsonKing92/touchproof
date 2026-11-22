namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal readonly record struct OwnerConsoleKeySpan(string Text, bool IsKey = false)
{
    internal static IReadOnlyList<OwnerConsoleKeySpan> Split(string navigation)
    {
        var spans = new List<OwnerConsoleKeySpan>();
        var start = 0;
        while (navigation.IndexOf('`', start) is var open && open >= 0 &&
               navigation.IndexOf('`', open + 1) is var close && close >= 0)
        {
            if (start < open) spans.Add(new(navigation[start..open]));
            if (open + 1 < close) spans.Add(new(navigation[(open + 1)..close], IsKey: true));
            start = close + 1;
        }
        if (start < navigation.Length) spans.Add(new(navigation[start..]));
        return spans;
    }

    internal static string ToPlainText(IEnumerable<OwnerConsoleKeySpan> spans) =>
        string.Concat(spans.Select(span => span.Text));
}

internal sealed class OwnerConsoleMarkedText
{
    private readonly IReadOnlyList<Range>[] _keyRanges;
    internal string PlainText { get; }

    internal OwnerConsoleMarkedText(IReadOnlyList<IReadOnlyList<OwnerConsoleKeySpan>> lines)
    {
        PlainText = string.Join("\n", lines.Select(OwnerConsoleKeySpan.ToPlainText));
        _keyRanges = lines.Select(KeyRanges).ToArray();
    }

    internal IReadOnlyList<Range> KeysAt(int sourceLine) =>
        sourceLine < _keyRanges.Length ? _keyRanges[sourceLine] : [];

    private static IReadOnlyList<Range> KeyRanges(IReadOnlyList<OwnerConsoleKeySpan> line)
    {
        var keys = new List<Range>();
        var offset = 0;
        foreach (var span in line)
        {
            var end = offset + span.Text.Length;
            if (span.IsKey) keys.Add(offset..end);
            offset = end;
        }
        return keys;
    }
}
