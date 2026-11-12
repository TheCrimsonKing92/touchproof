namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleDecisionCache
{
    private string? _marker;
    private OwnerDecisionRead? _read;

    internal async Task<OwnerDecisionRead> ReadAsync(IOwnerQuestionSource questions, string? marker,
        bool force, CancellationToken token, Func<string?>? validate = null)
    {
        token.ThrowIfCancellationRequested();
        if (!force && marker is not null && marker == _marker && _read is not null) return _read;
        _marker = null;
        _read = null;
        var read = await OwnerDecisionReader.ReadAsync(questions, token);
        token.ThrowIfCancellationRequested();
        _read = read;
        _marker = validate is null || validate() == marker ? marker : null;
        return read;
    }
}
