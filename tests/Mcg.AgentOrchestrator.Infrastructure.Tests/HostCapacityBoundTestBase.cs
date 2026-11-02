/// <summary>
/// Base for fixtures that hold one slot of <see cref="HostCapacityTestBudget"/> from
/// asynchronous initialization until disposal. Declared no-git fixtures skip the budget.
///
/// This replaces the removed all-or-nothing GoalWorktreeCleanupHooks serialization. It is
/// deliberately weaker: it admits several tests at once and imposes no ordering, so it
/// cannot stand in for a nonparallel collection guarding process-global state.
/// </summary>
public abstract class HostCapacityBoundTestBase : Xunit.IAsyncLifetime
{
    private readonly object capacityGate = new();
    private Task<IAsyncDisposable>? capacityAcquisition;
    private Task? capacityDisposal;
    private bool disposed;

    /// <summary>
    /// The budget this fixture draws from. Overridden only by the controls that verify the
    /// lifetime contract against an isolated budget instead of the shared process one.
    /// </summary>
    private protected virtual HostCapacityTestBudget CapacityBudget => HostCapacityTestBudget.Shared;

    // Conservative default: opt out only for fixtures whose scenarios never launch git.
    // Keep the declaration with the fixture, rather than maintaining a central census list.
    private protected virtual bool UsesGit => true;

    // Yield the runner thread while queued. Synchronous git observers must never acquire:
    // blocked launch threads can prevent the async disposal that returns capacity.
    public virtual ValueTask InitializeAsync() => UsesGit ? EnsureHostCapacityAsync() : ValueTask.CompletedTask;

    /// <summary>One per-test acquisition shared by concurrent initialization calls.</summary>
    private async ValueTask EnsureHostCapacityAsync()
    {
        Task<IAsyncDisposable> acquisition;
        lock (capacityGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (capacityAcquisition is null || capacityAcquisition.IsFaulted || capacityAcquisition.IsCanceled)
            {
                capacityAcquisition = CapacityBudget.EnterAsync().AsTask();
            }

            acquisition = capacityAcquisition;
        }

        await acquisition.ConfigureAwait(false);
        lock (capacityGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
        }
    }

    public virtual ValueTask DisposeAsync()
    {
        lock (capacityGate)
        {
            disposed = true;
            return new ValueTask(capacityDisposal ??= ReleaseCapacityAsync(capacityAcquisition));
        }
    }

    private static async Task ReleaseCapacityAsync(Task<IAsyncDisposable>? acquisition)
    {
        if (acquisition is null || acquisition.IsFaulted || acquisition.IsCanceled)
        {
            return;
        }

        var slot = await acquisition.ConfigureAwait(false);
        await slot.DisposeAsync().ConfigureAwait(false);
    }
}
