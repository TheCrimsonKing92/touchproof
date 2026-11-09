using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: pure parsing with no shared mutable state.
public sealed class GoDeveloperSubtestIdentityTests
{
    [Fact]
    public void SubtestAndFollowingTopLevelIdentityArePreserved()
    {
        Assert.Equal(
            ["crosswalk.TestReadInvalidFixtures/invalid-duplicate-name", "crosswalk.TestNext"],
            GoDeveloperNoChangeCompletion.ParseTestIdentities(
                "deferred - crosswalk.TestReadInvalidFixtures/invalid-duplicate-name, crosswalk.TestNext"));
    }

    [Theory]
    [InlineData("pkg.TestA/sub/leaf")]
    [InlineData("pkg:TestA/sub")]
    [InlineData("pkg.TestA/value.+([\\]{}^$?*#")]
    [InlineData("pkg.TestA/日本語_入力")]
    [InlineData("pkg.TestA/left|TestB")]
    [InlineData("pkg:TestA/left|TestB/right|TestC")]
    public void SubtestSegmentsKeepTheirFullIdentity(string identity)
    {
        Assert.Equal([identity, "pkg.TestNext"], GoDeveloperNoChangeCompletion.ParseTestIdentities(
            $"deferred - `{identity}` and pkg.TestNext"));
    }

    [Theory]
    [InlineData("pkg.TestA/")]
    [InlineData("pkg.TestA//leaf")]
    [InlineData("pkg.TestA/sub//leaf")]
    [InlineData("pkg.TestA/has space")]
    [InlineData("pkg.TestA/has\ttab")]
    [InlineData("Pkg.TestA/sub")]
    [InlineData("pkg.Testlower/sub")]
    [InlineData("prose")]
    public void FirstInvalidEntryStopsTheRunAfterAValidSubtest(string invalid)
    {
        Assert.Equal(["pkg.TestA/sub"], GoDeveloperNoChangeCompletion.ParseTestIdentities(
            $"deferred - pkg.TestA/sub, {invalid}, pkg.TestIgnored"));
    }

    [Theory]
    [InlineData(";")]
    [InlineData("\n")]
    [InlineData("\r")]
    public void ExistingDeclarationTerminatorsStillStopParsing(string terminator)
    {
        Assert.Equal(["pkg.TestA/sub"], GoDeveloperNoChangeCompletion.ParseTestIdentities(
            $"deferred - pkg.TestA/sub{terminator}pkg.TestIgnored/leaf"));
    }
}
