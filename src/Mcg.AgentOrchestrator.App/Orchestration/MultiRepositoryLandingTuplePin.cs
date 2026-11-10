using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

/// <summary>Owns the accepted candidate tuple for one synchronous tuple landing attempt.</summary>
internal sealed class MultiRepositoryLandingTuplePin : IDisposable
{
    private static readonly AsyncLocal<MultiRepositoryLandingTuplePin?> Current = new();
    private readonly GoalId _goalId;
    private readonly string _canonical;
    private readonly MultiRepositoryLandingTuplePin? _previous;
    private bool _disposed;

    private MultiRepositoryLandingTuplePin(GoalId goalId, string canonical)
    {
        _goalId = goalId;
        _canonical = canonical;
        _previous = Current.Value;
        Current.Value = this;
    }

    internal static MultiRepositoryLandingTuplePin Enter(GoalId goalId, string canonical)
    {
        if (!RepositoryCandidateTuple.TryParse(canonical, out _))
            throw new ArgumentException("A valid candidate tuple is required for a landing pin.", nameof(canonical));
        return new(goalId, canonical);
    }

    internal static string? For(GoalId goalId) =>
        Current.Value is { _disposed: false } pin && pin._goalId == goalId ? pin._canonical : null;

    public void Dispose()
    {
        if (_disposed) return;
        if (!ReferenceEquals(Current.Value, this))
            throw new InvalidOperationException("Landing tuple pins must be disposed in scope order.");
        _disposed = true;
        Current.Value = _previous;
    }
}
