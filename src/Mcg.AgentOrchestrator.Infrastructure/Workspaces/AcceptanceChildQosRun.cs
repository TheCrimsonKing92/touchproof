namespace Mcg.AgentOrchestrator.Infrastructure;

// One verifier invocation's policy snapshot and totals, shared only by its async flow.
internal sealed class AcceptanceChildQosRun : IDisposable
{
    private static readonly AsyncLocal<AcceptanceChildQosRun?> CurrentRun = new();
    private readonly AcceptanceChildQosRun? previous;
    private int tried;
    private int optedOut;
    private bool disposed;

    internal static AcceptanceChildQosRun? Current => CurrentRun.Value;
    internal bool Enabled { get; }
    internal int TriedCount => Volatile.Read(ref tried);
    internal int OptedOutCount => Volatile.Read(ref optedOut);

    internal AcceptanceChildQosRun(bool enabled)
    {
        Enabled = enabled;
        previous = CurrentRun.Value;
        CurrentRun.Value = this;
    }

    internal void Record(int triedCount, int optedOutCount)
    {
        Interlocked.Add(ref tried, triedCount);
        Interlocked.Add(ref optedOut, optedOutCount);
    }

    public void Dispose()
    {
        if (disposed) return;
        if (!ReferenceEquals(CurrentRun.Value, this))
            throw new InvalidOperationException("Verifier QoS scopes must be disposed in owning-flow order.");
        CurrentRun.Value = previous;
        disposed = true;
    }
}
