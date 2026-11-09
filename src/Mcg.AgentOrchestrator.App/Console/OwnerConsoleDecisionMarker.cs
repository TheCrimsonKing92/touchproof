namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// A decision read can join the board's earlier snapshot loads. Only accept a
// marker that was already current before those loads, never a later store write.
internal sealed class OwnerConsoleDecisionMarker(Func<string?>? source)
{
    private bool _boardObserved;
    private string? _beforeBoard;

    internal void BeforeBoard()
    {
        _beforeBoard = source?.Invoke();
        _boardObserved = true;
    }

    internal string? Read()
    {
        var current = source?.Invoke();
        return !_boardObserved || current == _beforeBoard ? current : null;
    }
}
