using static MtpTestRunnerScriptTests;

// Parallel-safe: each test owns its ledger and fake launcher; no test host or shared state.
public sealed class ManagedDiscoveryLaunchLedgerTests
{
    public static IEnumerable<object[]> DistinctArguments()
    {
        yield return new object[] { new[] { "--filter-class", "a" } }; // case
        yield return new object[] { new[] { "A", "--filter-class" } }; // order
        yield return new object[] { new[] { "--filter-class A" } }; // boundaries
        yield return new object[] { new[] { "--filter-class", "A", "A" } }; // duplicates
        yield return new object[] { new[] { "--filter-class", "A", "" } }; // empty value
    }

    [Xunit.Theory]
    [Xunit.MemberData(nameof(DistinctArguments))]
    public void Request_ExactOrderedArguments_DistinguishesEachSequence(string[] different)
    {
        var launches = 0;
        var ledger = new ManagedDiscoveryLaunchLedger(_ => new(++launches, "", ""), reuse: true);
        var original = ledger.Request("--filter-class", "A");
        Assert.Same(original, ledger.Request(new[] { "--filter-class", "A" }));
        Assert.NotSame(original, ledger.Request(different));
        Assert.Equal(2, launches);
        Assert.Equal(new[] { 2, 1 }, ledger.Snapshot().Select(count => count.Requests));
        Assert.All(ledger.Snapshot(), count => Assert.Equal(1, count.Launches));
    }

    [Xunit.Fact]
    public void Request_MutatedCallerAndSnapshotArguments_PreservesOriginalIdentity()
    {
        var launches = 0;
        var ledger = new ManagedDiscoveryLaunchLedger(arguments =>
        {
            arguments[0] = "launcher mutation";
            return new(++launches, "", "");
        }, reuse: true);
        var arguments = new[] { "original", "" };
        var result = ledger.Request(arguments);
        arguments[0] = "caller mutation";
        ledger.Snapshot()[0].Arguments[0] = "snapshot mutation";
        Assert.Same(result, ledger.Request("original", ""));
        Assert.Equal(1, launches);
        Assert.Equal(new[] { "original", "" }, Assert.Single(ledger.Snapshot()).Arguments);
    }

    [Xunit.Fact]
    public async Task Request_ConcurrentIdenticalArguments_SharesOneLaunch()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var launches = 0;
        var ledger = new ManagedDiscoveryLaunchLedger(_ =>
        {
            Interlocked.Increment(ref launches);
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromMinutes(1)), "Fake launch release was not signaled.");
            return new(0, "shared output", "");
        }, reuse: true);
        var first = Task.Factory.StartNew(() => ledger.Request("same"),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task<ProcessResult>? second = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromMinutes(1)), "First fake launch was not entered.");
            second = Task.Factory.StartNew(() => ledger.Request("same"),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.True(SpinWait.SpinUntil(() => ledger.Snapshot().Single().Requests == 2, TimeSpan.FromMinutes(1)),
                "The second identical request did not enter the ledger while the first launch was gated.");
        }
        finally
        {
            release.Set();
            await first.WaitAsync(TimeSpan.FromMinutes(1));
            if (second is not null)
                await second.WaitAsync(TimeSpan.FromMinutes(1));
        }
        Assert.Same(await first, await second!);
        Assert.Equal(1, launches);
        var count = Assert.Single(ledger.Snapshot());
        Assert.Equal(2, count.Requests);
        Assert.Equal(1, count.Launches);
    }

    [Xunit.Fact]
    public void Request_ThrowingLaunch_PreservesFailureWithoutRetry()
    {
        var failure = new InvalidOperationException("launch failed");
        var launches = 0;
        var ledger = new ManagedDiscoveryLaunchLedger(_ =>
        {
            launches++;
            throw failure;
        }, reuse: true);
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => ledger.Request("same")));
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => ledger.Request("same")));
        Assert.Equal(1, launches);
        var count = Assert.Single(ledger.Snapshot());
        Assert.Equal(2, count.Requests);
        Assert.Equal(1, count.Launches);
    }

    [Xunit.Fact]
    public void Request_FailedOutcome_PreservesExitCodeAndOutput()
    {
        var failure = new ProcessResult(8, "empty catalog", "diagnostic");
        var ledger = new ManagedDiscoveryLaunchLedger(_ => failure, reuse: true);
        Assert.Same(failure, ledger.Request("same"));
        Assert.Same(failure, ledger.Request("same"));
        Assert.Equal(1, Assert.Single(ledger.Snapshot()).Launches);
    }

    [Xunit.Fact]
    public void Request_ReuseDisabled_CountsEveryRequestAsALaunch()
    {
        var launches = 0;
        var ledger = new ManagedDiscoveryLaunchLedger(_ => new(++launches, "", ""), reuse: false);
        Assert.NotSame(ledger.Request("same"), ledger.Request("same"));
        ledger.Request("distinct");
        Assert.Equal(3, launches);
        Assert.Equal(2, ledger.Snapshot().Count);
        Assert.All(ledger.Snapshot(), count => Assert.Equal(count.Requests, count.Launches));
    }

    [Xunit.Fact]
    public void Request_SeparateClassRuns_DoNotShareResultsOrCounters()
    {
        var launches = 0;
        ProcessResult Launch(string[] _) => new(++launches, "", "");
        var firstRun = new ManagedDiscoveryLaunchLedger(Launch, reuse: true);
        var secondRun = new ManagedDiscoveryLaunchLedger(Launch, reuse: true);
        Assert.NotSame(firstRun.Request("same"), secondRun.Request("same"));
        Assert.Equal(2, launches);
        Assert.Equal(1, Assert.Single(firstRun.Snapshot()).Requests);
        Assert.Equal(1, Assert.Single(secondRun.Snapshot()).Requests);
    }

    [Xunit.Fact]
    public void Catalog_ReusedEmptyOutcome_ValidatesAllowEmptyForEveryRequest()
    {
        var launches = 0;
        var ledger = new ManagedDiscoveryLaunchLedger(_ =>
        {
            launches++;
            return new(0, "{\"tests\":[]}", "");
        }, reuse: true);
        Assert.Empty(DiscoverManagedTestCatalog(ledger, true, "same"));
        Assert.Throws<Xunit.Sdk.NotEmptyException>(() => DiscoverManagedTestCatalog(ledger, false, "same"));
        Assert.Equal(1, launches);
        Assert.Equal(2, Assert.Single(ledger.Snapshot()).Requests);
    }
}
