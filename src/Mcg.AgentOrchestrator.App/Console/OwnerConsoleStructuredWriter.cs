namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// A producer attaches typed metadata at its existing write boundary. Only source-line
// positions are counted here; field values are never recovered from rendered output.
internal sealed class OwnerConsoleStructuredWriter : StringWriter
{
    private readonly Dictionary<int, OwnerConsoleFieldSnapshot> _rows = [];
    private OwnerConsoleFieldSnapshot? _nextRow;
    internal void Mark(OwnerConsoleFieldSnapshot item) => _nextRow = item;

    // Mark belongs to the next emitted row, including every embedded logical line.
    // Use the same CR/LF normalization as TextPage; do not let ownership leak to
    // an unmarked heading or summary following that row.
    public override void WriteLine(string? value)
    {
        if (_nextRow is { } item)
        {
            _nextRow = null;
            var first = LogicalBreaks(GetStringBuilder().ToString());
            var last = first + LogicalBreaks(value ?? "");
            for (var line = first; line <= last; line++) _rows[line] = item;
        }
        base.WriteLine(value);
    }

    private static int LogicalBreaks(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Count(ch => ch == '\n');
    internal OwnerConsoleStructuredText? Build(OwnerConsoleStructuredText? fallback)
    {
        if (_rows.Count == 0) return fallback;
        var rows = fallback?.Rows?.ToDictionary(pair => pair.Key, pair => pair.Value) ?? [];
        foreach (var (line, item) in _rows) rows[line] = item;
        return new(fallback?.Detail, rows);
    }
}
