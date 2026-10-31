using Mcg.AgentOrchestrator.Infrastructure;

public sealed class RemoteLaneCostAdmissionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingBudgetAdmitsUpToSlotCapRegardlessOfLaneCost(bool measured)
    {
        var names = new[] { "A", "B", "C", "D" };
        var samples = measured
            ? names.Select(name => RemoteLaneCostTestFixture.Sample(name, cpu: 800)).ToArray()
            : [];
        using var fixture = new RemoteLaneCostTestFixture(samples, costBudget: null);
        Assert.Null(fixture.Entry.CostBudget);
        foreach (var name in names.Take(3)) Assert.Equal(fixture.Entry, await fixture.Claim(name));
        Assert.Null(await fixture.Claim("D"));
        fixture.Coordinator.Release("executor", RemoteLaneCostTestFixture.Lane("B").Name);
        Assert.Equal(fixture.Entry, await fixture.Claim("D"));
    }

    [Fact]
    public void MissingBudgetAllowsLaneLessClaimsUpToSlotCap()
    {
        using var fixture = new RemoteLaneCostTestFixture(costBudget: null);
        for (var index = 0; index < fixture.Entry.Slots; index++)
            Assert.Equal(fixture.Entry, fixture.Coordinator.TryClaimIdleExecutor());
        Assert.Null(fixture.Coordinator.TryClaimIdleExecutor());
    }

    [Fact]
    public async Task ThreeLightLanesFitTogetherButSlotCapStillBoundsAdmission()
    {
        var samples = new[] { "A", "B", "C", "D" }.Select(name => RemoteLaneCostTestFixture.Sample(name)).ToArray();
        using var fixture = new RemoteLaneCostTestFixture(samples);
        foreach (var name in new[] { "A", "B", "C" }) Assert.Equal(fixture.Entry, await fixture.Claim(name));
        Assert.Null(await fixture.Claim("D"));
        fixture.Coordinator.Release("executor", RemoteLaneCostTestFixture.Lane("B").Name);
        Assert.NotNull(await fixture.Claim("D"));

        using var capped = new RemoteLaneCostTestFixture(samples, slots: 2);
        Assert.NotNull(await capped.Claim("A")); Assert.NotNull(await capped.Claim("B"));
        Assert.Null(await capped.Claim("C"));
    }

    [Fact]
    public async Task TwoHeavyLanesRefuseThirdAndReleaseTheExactCost()
    {
        var samples = new[] { "A", "B", "C" }.Select(name => RemoteLaneCostTestFixture.Sample(name, 190))
            .Append(RemoteLaneCostTestFixture.Sample("Light"));
        using var fixture = new RemoteLaneCostTestFixture(samples);
        Assert.NotNull(await fixture.Claim("A")); Assert.NotNull(await fixture.Claim("B"));
        Assert.Null(await fixture.Claim("C")); Assert.Null(await fixture.Claim("Light"));
        fixture.Coordinator.Release("executor", RemoteLaneCostTestFixture.Lane("A").Name);
        Assert.NotNull(await fixture.Claim("C"));
    }

    [Fact]
    public async Task ReleasingLightPeerDoesNotReleaseHeavierPeersCost()
    {
        using var fixture = new RemoteLaneCostTestFixture([
            RemoteLaneCostTestFixture.Sample("A", 100), RemoteLaneCostTestFixture.Sample("B", 190),
            RemoteLaneCostTestFixture.Sample("C", 190)], costBudget: 3);
        Assert.NotNull(await fixture.Claim("A")); Assert.NotNull(await fixture.Claim("B"));
        fixture.Coordinator.Release("executor", RemoteLaneCostTestFixture.Lane("A").Name);
        fixture.Coordinator.Release("executor", RemoteLaneCostTestFixture.Lane("A").Name);
        Assert.Null(await fixture.Claim("C"));
        Assert.NotNull(await fixture.Claim("A"));
    }

    [Fact]
    public async Task ConcurrentClaimsCannotOverReserveCostBudget()
    {
        using var fixture = new RemoteLaneCostTestFixture();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var claims = new[] { "A", "B", "C" }.Select(name => Task.Run(async () =>
        {
            await start.Task;
            return await fixture.Claim(name);
        })).ToArray();
        start.SetResult();
        await RemoteLaneCostTestFixture.Event(Task.WhenAll(claims), "all simultaneous admission decisions");
        var decisions = await Task.WhenAll(claims);
        Assert.Equal(2, decisions.Count(entry => entry is not null));
    }

    [Theory]
    [InlineData(1, 3, 2, 2, 4d, true)]
    [InlineData(2, 2, 1, 0.5, 4d, false)]
    [InlineData(2, 3, 3.8, 1.9, 4d, false)]
    [InlineData(0, 3, 0, 5, 4d, false)]
    [InlineData(0, 3, 0, 8, null, true)]
    [InlineData(2, 3, 16, 8, null, true)]
    [InlineData(3, 3, 24, 8, null, false)]
    public void FitsRequiresBothSlotAndNumericBudget(int held, int slots, double cost,
        double candidate, double? budget, bool fits) =>
        Assert.Equal(fits, RemoteLaneAdmissionPolicy.Fits(held, slots, cost, candidate, budget));

    [Theory]
    [InlineData("executor-busy")]
    [InlineData("transport-unavailable")]
    public async Task FailureReleasesOnlyItsReservation(string failure)
    {
        using var fixture = new RemoteLaneCostTestFixture();
        Assert.NotNull(await fixture.Claim("A")); Assert.NotNull(await fixture.Claim("B"));
        fixture.Executor.Submit = (_, _) => Task.FromResult(new RemoteLaneSubmission(null, failure));
        Assert.Null((await fixture.Run("A")).Accepted);
        fixture.Coordinator.Release("executor", RemoteLaneCostTestFixture.Lane("B").Name);
        // Disposal must return every OS claim too, including a peer held during fallback.
        fixture.Coordinator.Dispose();
        using var replacement = new RemoteLaneCoordinator(new([fixture.Entry], [], null),
            new RemoteLaneCandidateIdentity("next", "goal", "commit", "tree", "main", "manifest"),
            fixture.Root, null, new FakeRemoteLaneExecutor(), fixture.Clock, TimeSpan.FromMilliseconds(1), null);
        Assert.NotNull(replacement.TryClaimIdleExecutor()); Assert.NotNull(replacement.TryClaimIdleExecutor());
        Assert.Null(replacement.TryClaimIdleExecutor());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAfterAbandon_PreservesCancellationOrMissingReservationFailure(bool cancel)
    {
        using var fixture = new RemoteLaneCostTestFixture();
        Assert.NotNull(await fixture.Claim("A"));
        using var stop = new CancellationTokenSource();
        using var registration = stop.Token.Register(fixture.Coordinator.AbandonAll);
        if (cancel) stop.Cancel();
        else fixture.Coordinator.AbandonAll();

        var exception = await Record.ExceptionAsync(() => fixture.Run("A", stop.Token));
        if (cancel)
            Assert.Equal(stop.Token, Assert.IsAssignableFrom<OperationCanceledException>(exception).CancellationToken);
        else
            Assert.IsType<InvalidOperationException>(exception);
        Assert.Empty(fixture.Executor.Requests);
        Assert.NotNull(await fixture.Claim("B"));
    }

    [Fact]
    public async Task CancellationReleasesReservationWithoutWaitingForDisposal()
    {
        using var fixture = new RemoteLaneCostTestFixture();
        var handle = new FakeRemoteLaneExecutor.Handle(fixture.Clock.GetUtcNow());
        fixture.Executor.Submit = (_, _) => Task.FromResult(new RemoteLaneSubmission(handle));
        Assert.NotNull(await fixture.Claim("A")); Assert.NotNull(await fixture.Claim("B"));
        using var stop = new CancellationTokenSource();
        var run = fixture.Run("A", stop.Token);
        await RemoteLaneCostTestFixture.Event(handle.Polled.Task, "remote lane first poll");
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.NotNull(await fixture.Claim("C"));
        Assert.Null(await fixture.Claim("D"));
    }
}
