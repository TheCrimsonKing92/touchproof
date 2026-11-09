using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: fake clock and recording setter; never touches Terminal.Gui's static rate.
public sealed class OwnerConsoleLoopBackoffTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Tick_IdleThenActivity_RestoresDefaultAndResetsDeadline(bool input)
    {
        var clock = new OwnerConsoleTestClock();
        var rates = new List<int>();
        using var policy = new OwnerConsoleLoopBackoff(clock, rates.Add, 25);
        clock.Now = clock.Now.AddMilliseconds(1900);
        policy.Tick();
        Assert.Empty(rates);
        clock.Now = clock.Now.AddMilliseconds(100);
        policy.Tick();
        policy.Tick();
        Assert.Equal([5], rates);

        if (input) policy.OnInput(); else policy.OnRender();
        Assert.Equal([5, 25], rates);
        clock.Now = clock.Now.AddMilliseconds(1999);
        policy.Tick();
        Assert.Equal([5, 25], rates);
        clock.Now = clock.Now.AddMilliseconds(1);
        policy.Tick();
        Assert.Equal([5, 25, 5], rates);
        Assert.All(rates, rate => Assert.True(rate >= 5));
    }

    [Fact]
    public void OnInput_Active_ResetsIdleDeadlineWithoutChangingRate()
    {
        var clock = new OwnerConsoleTestClock();
        var rates = new List<int>();
        using var policy = new OwnerConsoleLoopBackoff(clock, rates.Add, 25);
        clock.Now = clock.Now.AddSeconds(1);
        policy.OnInput();
        clock.Now = clock.Now.AddSeconds(1);
        policy.Tick();
        Assert.Empty(rates);
        clock.Now = clock.Now.AddSeconds(1);
        policy.Tick();
        Assert.Equal([5], rates);
    }

    [Fact]
    public void Dispose_Idle_RestoresRateAndIgnoresLateCallbacks()
    {
        var clock = new OwnerConsoleTestClock();
        var rates = new List<int>();
        var policy = new OwnerConsoleLoopBackoff(clock, rates.Add, 25);
        clock.Now = clock.Now.AddSeconds(2);
        policy.Tick();
        policy.Dispose();
        clock.Now = clock.Now.AddSeconds(10);
        policy.Tick();
        policy.OnInput();
        policy.OnRender();
        policy.Dispose();
        Assert.Equal([5, 25], rates);
    }

    [Fact]
    public void Tick_DefaultBelowIdleFloor_NeverSetsRateBelowFive()
    {
        var clock = new OwnerConsoleTestClock();
        var rates = new List<int>();
        var policy = new OwnerConsoleLoopBackoff(clock, rates.Add, 3);
        clock.Now = clock.Now.AddSeconds(2);
        policy.Tick();
        policy.OnInput();
        clock.Now = clock.Now.AddSeconds(2);
        policy.Tick();
        policy.OnRender();
        policy.Dispose();
        Assert.Equal([5, 5, 5, 5, 5], rates);
    }
}
