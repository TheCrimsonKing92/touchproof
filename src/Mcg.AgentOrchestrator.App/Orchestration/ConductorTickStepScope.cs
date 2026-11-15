namespace Mcg.AgentOrchestrator.App.Orchestration;

// Ends one lexical timing frame once, including continue/return/exception exits.
internal sealed class ConductorTickStepScope(Action finish) : IDisposable
{
    private Action? _finish = finish;
    public void Dispose() => Interlocked.Exchange(ref _finish, null)?.Invoke();
}
