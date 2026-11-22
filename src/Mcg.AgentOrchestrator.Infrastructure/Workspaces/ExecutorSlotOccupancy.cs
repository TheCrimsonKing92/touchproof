namespace Mcg.AgentOrchestrator.Infrastructure;

internal interface IExecutorSlotOccupancy
{
    // Null means another owner holds the lock. Acquisition faults propagate to the caller.
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

    public IDisposable? TryClaimExclusive(string root, string executor, int slot)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        if (string.IsNullOrWhiteSpace(executor) || executor is "." or ".." ||
            executor.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Invalid executor id.", nameof(executor));

        // Use the same cross-process locks as RemoteExecutorOccupancy, with faults visible to screening.
        var folder = RemoteExecutorOccupancy.Folder(root, executor);
        Directory.CreateDirectory(folder);
        var name = slot == 0 ? "claim.lock" : $"slot-{slot}.lock";
        try
        {
            return new FileStream(Path.Combine(folder, name), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) when (IsLockContention(ex))
        {
            return null;
        }
    }

    private static bool IsLockContention(IOException exception) =>
        (OperatingSystem.IsWindows(), exception.HResult & 0xffff) switch
        {
            (true, 32 or 33) => true,
            (false, 11 or 35) => true,
            _ => false
        };

    public void Claim(string root, string executor, string attempt, int slot, RemoteExecutorClaimClass claimClass) =>
        RemoteExecutorOccupancy.Claim(root, executor, attempt, slot, claimClass);

    public void Release(string root, string executor, string attempt, int slot) =>
        RemoteExecutorOccupancy.Release(root, executor, attempt, slot);

    public IReadOnlyList<(string Attempt, string? Class)> LiveMarkers(string root, string executor) =>
        RemoteExecutorOccupancy.LiveMarkers(root, executor);

    public bool IsOccupied(string root, string executor) => RemoteExecutorOccupancy.IsOccupied(root, executor);
}
