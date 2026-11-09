namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleDecisionCache
{
    private string? _marker;
    private OwnerDecisionRead? _read;

    internal async Task<OwnerDecisionRead> ReadAsync(IOwnerQuestionSource questions, string? marker,
        bool force, CancellationToken token)
    {
        if (!force && marker is not null && marker == _marker && _read is not null) return _read;
        var read = await OwnerDecisionReader.ReadAsync(questions, token);
        _read = read;
        _marker = marker;
        return read;
    }
}
