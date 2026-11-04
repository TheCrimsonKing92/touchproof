using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Keeps workspace cleanup local until the background gate explicitly takes ownership.
internal sealed class TransferableAcceptanceCohortWorkspace(AcceptanceCohortWorkspace workspace) : IDisposable
{
    private AcceptanceCohortWorkspace? _workspace = workspace;

    internal AcceptanceCohortWorkspace Transfer() =>
        Interlocked.Exchange(ref _workspace, null)
        ?? throw new InvalidOperationException("Acceptance cohort workspace ownership was already transferred.");

    public void Dispose() => Interlocked.Exchange(ref _workspace, null)?.Dispose();
}
