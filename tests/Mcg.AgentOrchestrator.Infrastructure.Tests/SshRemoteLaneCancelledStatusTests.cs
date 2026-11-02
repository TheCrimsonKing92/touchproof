using Mcg.AgentOrchestrator.Infrastructure;

// Parallel safe: all transport, filesystem and clock state belongs to each fixture.
public sealed class SshRemoteLaneCancelledStatusTests
{
    [Xunit.Theory]
    [Xunit.InlineData("executor-checkout-failed", "remote job cancelled: executor-checkout-failed")]
    [Xunit.InlineData("", "remote job cancelled")]
    [Xunit.InlineData(null, "remote job cancelled")]
    public async Task CancelledStatus_MatchingAttempt_EndsPollWithCancelledReason(
        string? error, string expectedMessage)
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        var handle = await fake.Submit();
        var status = FakeSshRemoteLaneTransport.Status("cancelled");
        if (error is null) status.Remove("error");
        else status["error"] = error;

        var observation = await fake.PollOnce(new(status));

        Assert.Equal(1, observation.PollIndex);
        var fault = Assert.Throws<RemoteLaneTransportException>(() => handle.TryGetResult());
        Assert.StartsWith("remote job cancelled", fault.Message);
        Assert.Equal(expectedMessage, fault.Message);
        await AwaitPollLoopCompletion(handle);
    }

    [Xunit.Fact]
    public async Task CancelledStatus_MatchingAttempt_DoesNotPollAgain()
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        var handle = await fake.Submit();
        await fake.PollOnce(new(FakeSshRemoteLaneTransport.Status("cancelled")));
        Assert.Throws<RemoteLaneTransportException>(() => handle.TryGetResult());
        await AwaitPollLoopCompletion(handle);
        var polls = fake.Calls.Count(call => call.Arguments.Any(arg =>
            arg.EndsWith("/status.json", StringComparison.Ordinal)));
        Assert.Equal(1, polls);

        await fake.Polls.Writer.WriteAsync(new(FakeSshRemoteLaneTransport.Status("running")));

        Assert.Equal(polls, fake.Calls.Count(call => call.Arguments.Any(arg =>
            arg.EndsWith("/status.json", StringComparison.Ordinal))));
        Assert.Equal(1, fake.Polls.Reader.Count);
        handle.RequestQueuedJobCancellation();
        Assert.Null(handle.QueuedJobCancellation);
        Assert.DoesNotContain(fake.Calls, call =>
            call.Arguments[^1].EndsWith(".cancel", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public async Task CancelledStatus_ForeignAttempt_KeepsPolling()
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        var handle = await fake.Submit();
        var status = FakeSshRemoteLaneTransport.Status("cancelled");
        status["attemptId"] = "another-attempt";
        await fake.PollOnce(new(status));
        Assert.Null(handle.TryGetResult());
        Assert.False(handle.PollLoop.IsCompleted);

        var next = await fake.PollOnce(new(FakeSshRemoteLaneTransport.Status("running")));

        Assert.Equal(2, next.PollIndex);
        Assert.Null(handle.TryGetResult());
        Assert.False(handle.PollLoop.IsCompleted);
    }

    [Xunit.Fact]
    public async Task FailedStatus_MatchingAttempt_StillThrowsErrorText()
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        var handle = await fake.Submit();

        await fake.PollOnce(new(FakeSshRemoteLaneTransport.Status("failed")));

        var fault = Assert.Throws<RemoteLaneTransportException>(() => handle.TryGetResult());
        Assert.Equal("executor-checkout-failed", fault.Message);
        await AwaitPollLoopCompletion(handle);
    }

    [Xunit.Fact]
    public async Task RunningStatus_MatchingAttempt_KeepsPolling()
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        var handle = await fake.Submit();
        await fake.PollOnce(new(FakeSshRemoteLaneTransport.Status("running")));
        Assert.Null(handle.TryGetResult());
        Assert.False(handle.PollLoop.IsCompleted);

        var next = await fake.PollOnce(new(FakeSshRemoteLaneTransport.Status("running")));

        Assert.Equal(2, next.PollIndex);
        Assert.Null(handle.TryGetResult());
        Assert.False(handle.PollLoop.IsCompleted);
    }

    private static async Task AwaitPollLoopCompletion(SshRemoteLaneHandle handle)
    {
        try { await handle.PollLoop.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException) { throw new TimeoutException("Missing event: terminal ssh poll loop completed"); }
    }
}
