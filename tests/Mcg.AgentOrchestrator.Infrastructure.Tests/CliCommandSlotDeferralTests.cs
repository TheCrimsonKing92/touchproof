public sealed class CliCommandSlotDeferralTests
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task PureBodyCompletesThroughDisposalWhileEverySlotIsHeldElsewhere()
    {
        var budget = new HostCapacityTestBudget(1, _ => null);
        var held = await budget.EnterAsync();
        var probe = new CliCommandSlotProbe(budget);
        var lifecycle = Task.Run(async () =>
        {
            await probe.InitializeAsync();
            Assert.Equal(1, probe.PureBody());
            await probe.DisposeAsync();
        });

        try
        {
            await lifecycle.WaitAsync(HangGuard);
            Assert.Equal(0, probe.AcquisitionRequests);
            Assert.Equal(0, budget.AvailableSlots);
        }
        finally
        {
            // Unblock even the eager-initialization negative case before draining it.
            await held.DisposeAsync();
            await lifecycle.WaitAsync(HangGuard);
            await probe.DisposeAsync();
        }

        Assert.Equal(1, budget.AvailableSlots);
    }

    [Fact]
    public async Task InitializationTakesOneSlotHelpersReuseItAndDisposalReturnsItOnce()
    {
        var budget = new HostCapacityTestBudget(2, _ => null);
        var probe = new CliCommandSlotProbe(budget, usesGit: true);
        await probe.InitializeAsync();
        Assert.Equal(1, budget.AvailableSlots);
        Assert.Equal(1, probe.AcquisitionRequests);
        try
        {
            Assert.Contains("git version", probe.FirstHelper());
            Assert.Equal(1, budget.AvailableSlots);
            Assert.Equal(1, probe.AcquisitionRequests);
            probe.SecondHelper();
            Assert.Equal(1, budget.AvailableSlots);
            Assert.Equal(1, probe.AcquisitionRequests);
        }
        finally
        {
            await probe.DisposeAsync();
        }

        Assert.Equal(2, budget.AvailableSlots);
        await probe.DisposeAsync();
        Assert.Equal(2, budget.AvailableSlots);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await probe.InitializeAsync());
    }

    [Fact]
    public async Task OverlappingInitializationsShareOnePendingAcquisition()
    {
        var budget = new HostCapacityTestBudget(1, _ => null);
        var held = await budget.EnterAsync();
        var probe = new CliCommandSlotProbe(budget, usesGit: true);
        var first = probe.InitializeAsync().AsTask();
        await probe.AcquisitionRequested.Task.WaitAsync(HangGuard);
        var second = probe.InitializeAsync().AsTask();
        try
        {
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            Assert.Equal(1, probe.AcquisitionRequests);
        }
        finally
        {
            await held.DisposeAsync();
            try
            {
                await Task.WhenAll(first, second).WaitAsync(HangGuard);
                Assert.Equal(1, probe.AcquisitionRequests);
                Assert.Equal(0, budget.AvailableSlots);
            }
            finally
            {
                await probe.DisposeAsync();
            }
        }

        Assert.Equal(1, budget.AvailableSlots);
    }
}
