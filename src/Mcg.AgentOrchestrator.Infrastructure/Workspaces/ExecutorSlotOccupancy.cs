namespace Mcg.AgentOrchestrator.Infrastructure;

internal interface IExecutorSlotOccupancy
{
    IDisposable? TryClaimExclusive(string root, string executor, int slot);
    void Claim(string root, string executor, string attempt, int slot, RemoteExecutorClaimClass claimClass);
    void Release(string root, string executor, string attempt, int slot);
    IReadOnlyList<(string Attempt, string? Class)> LiveMarkers(string root, string executor);
    bool IsOccupied(string root, string executor);
}

internal sealed class ExecutorSlotOccupancy : IExecutorSlotOccupancy
{
    internal static ExecutorSlotOccupancy Instance { get; } = new();

    private ExecutorSlotOccupancy() { }

    public IDisposable? TryClaimExclusive(string root, string executor, int slot) =>
        RemoteExecutorOccupancy.TryClaimExclusive(root, executor, slot);

    public void Claim(string root, string executor, string attempt, int slot, RemoteExecutorClaimClass claimClass) =>
        RemoteExecutorOccupancy.Claim(root, executor, attempt, slot, claimClass);

    public void Release(string root, string executor, string attempt, int slot) =>
        RemoteExecutorOccupancy.Release(root, executor, attempt, slot);

    public IReadOnlyList<(string Attempt, string? Class)> LiveMarkers(string root, string executor) =>
        RemoteExecutorOccupancy.LiveMarkers(root, executor);

    public bool IsOccupied(string root, string executor) => RemoteExecutorOccupancy.IsOccupied(root, executor);
}
