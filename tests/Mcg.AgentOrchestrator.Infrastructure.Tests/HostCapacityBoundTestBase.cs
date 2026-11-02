/// <summary>
/// Base for fixtures that hold one slot of <see cref="HostCapacityTestBudget"/> from
/// their first observed git launch until disposal. Tests without git never take a slot.
///
/// This replaces the removed all-or-nothing GoalWorktreeCleanupHooks serialization. It is
/// deliberately weaker: it admits several tests at once and imposes no ordering, so it
/// cannot stand in for a nonparallel collection guarding process-global state.
/// </summary>
public abstract class HostCapacityBoundTestBase : Xunit.IAsyncLifetime
{
    private static readonly AsyncLocal<HostCapacityBoundTestBase?> CurrentOwner = new();
    private readonly object capacityGate = new();
    private Task<IAsyncDisposable>? capacityAcquisition;
    private Task? capacityDisposal;
    private bool disposed;

    static HostCapacityBoundTestBase() =>
        HostCapacitySlotGitCensus.RegisterAcquisitionResolver(() =>
        {
            var owner = CurrentOwner.Value ?? Xunit.TestContext.Current.TestClassInstance as HostCapacityBoundTestBase;
            return owner is null ? null : owner.EnsureHostCapacity;
        });

    protected HostCapacityBoundTestBase() => CurrentOwner.Value = this;

    /// <summary>
    /// The budget this fixture draws from. Overridden only by the controls that verify the
    /// lifetime contract against an isolated budget instead of the shared process one.
    /// </summary>
    private protected virtual HostCapacityTestBudget CapacityBudget => HostCapacityTestBudget.Shared;

    public virtual ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <summary>One per-test acquisition shared by concurrent first git launches.</summary>
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

    // Blocking the launching thread is safe: the semaphore is process-local and this test
    // holds no other capacity slot, so it cannot form a circular slot wait. Continuations
    // use ConfigureAwait(false); acquisition does not need the blocked runner context.
    private void EnsureHostCapacity() =>
        EnsureHostCapacityAsync().GetAwaiter().GetResult();

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
