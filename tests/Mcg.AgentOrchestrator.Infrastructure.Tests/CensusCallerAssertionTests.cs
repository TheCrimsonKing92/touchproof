// Parallel-safe: constructed caller dictionaries only; no shared state or external resources.
public sealed class CensusCallerAssertionTests
{
    [Theory]
    [InlineData("test:HostCapacitySlotGitCallerTests.Record_ForwardingObserver_UsesCallerOutsideLaunchBoundary|rev-parse")]
    [InlineData("test:HostCapacitySlotGitCallerTests.Record_ForwardingObserver_UsesCallerOutsideLaunchBoundary<-SomeType.SomeParent|rev-parse")]
    public void SingleCaller_OptionalParent_AcceptsExpectedAttribution(string key)
    {
        HostCapacitySlotGitCallerTests.AssertSingleCaller(
            new Dictionary<string, int> { [key] = 1 }, ExpectedKey(), 1);
    }

    [Theory]
    [InlineData("test:HostCapacitySlotGitCallerTests.OtherMethod|rev-parse", 1)]
    [InlineData("test:Other.Method<-HostCapacitySlotGitCallerTests.Record_ForwardingObserver_UsesCallerOutsideLaunchBoundary|rev-parse", 1)]
    [InlineData("test:HostCapacitySlotGitCallerTests.Record_ForwardingObserver_UsesCallerOutsideLaunchBoundary|commit", 1)]
    [InlineData("test:HostCapacitySlotGitCallerTests.Record_ForwardingObserver_UsesCallerOutsideLaunchBoundary|rev-parse", 2)]
    public void SingleCaller_UnexpectedAttributionOrCount_RejectsWindow(string key, int count)
    {
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => HostCapacitySlotGitCallerTests.AssertSingleCaller(
            new Dictionary<string, int> { [key] = count }, ExpectedKey(), 1));
    }

    [Fact]
    public void SingleCaller_TwoParentsOfSameCaller_RejectsMultipleEntries()
    {
        var callers = new Dictionary<string, int>
        {
            ["test:HostCapacitySlotGitCallerTests.Record_ForwardingObserver_UsesCallerOutsideLaunchBoundary<-First.Parent|rev-parse"] = 1,
            ["test:HostCapacitySlotGitCallerTests.Record_ForwardingObserver_UsesCallerOutsideLaunchBoundary<-Second.Parent|rev-parse"] = 1
        };

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            HostCapacitySlotGitCallerTests.AssertSingleCaller(callers, ExpectedKey(), 2));
    }

    [Fact]
    public void SingleCaller_NullWindow_ReportsMissingCallers()
    {
        var exception = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            HostCapacitySlotGitCallerTests.AssertSingleCaller(null, ExpectedKey(), 1));

        Assert.Contains("<null>", exception.Message);
    }

    [Fact]
    public void SingleCaller_EmptyWindow_RejectsMissingEntry()
    {
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => HostCapacitySlotGitCallerTests.AssertSingleCaller(
            new Dictionary<string, int>(), ExpectedKey(), 1));
    }

    [Fact]
    public void SingleCaller_FailedWindow_ReportsEveryFullKey()
    {
        const string first = "test:HostCapacitySlotGitCallerTests.Record_ForwardingObserver_UsesCallerOutsideLaunchBoundary<-SomeType.ParentWithALongNameThatMustRemainVisible|rev-parse";
        const string second = "test:AnotherType.UnexpectedCaller<-AnotherType.AnotherParentThatMustRemainVisible|commit";
        var callers = new Dictionary<string, int> { [first] = 1, [second] = 2 };

        var exception = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            HostCapacitySlotGitCallerTests.AssertSingleCaller(callers, ExpectedKey(), 1));

        Assert.Contains(first + " = 1", exception.Message);
        Assert.Contains(second + " = 2", exception.Message);
    }

    private static string ExpectedKey() =>
        "test:HostCapacitySlotGitCallerTests.Record_ForwardingObserver_UsesCallerOutsideLaunchBoundary|rev-parse";
}
