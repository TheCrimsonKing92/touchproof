using Xunit;

public sealed class SemanticImpactSpikeStaticCallBindingTests
{
    [Fact]
    public async Task QualifiedCallReachesOnlyTheBoundType()
    {
        var checks = (await SpikeTestContext.Fixture()).Where(c => c.Name.StartsWith("static-", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, checks.Length);
        Assert.Equal(["SpikeFixtureStaticTests"], checks.Single(c => c.Name == "static-bound").Returned);
        Assert.Empty(checks.Single(c => c.Name == "static-unrelated").Returned);
    }

    [Fact]
    public async Task DelegatePartialAndInheritedFactsUseTheSameDiscovery()
    {
        var checks = (await SpikeTestContext.Fixture()).Where(c => !c.Name.StartsWith("static-", StringComparison.Ordinal)
            && !c.Name.StartsWith("interface-", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, checks.Length);
        Assert.All(checks, c => Assert.True(c.Passed, c.Name));
    }
}
