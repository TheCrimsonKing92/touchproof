using Xunit;

public sealed class SemanticImpactSpikeInterfaceDispatchTests
{
    [Fact]
    public async Task BothImplementersReachInterfaceCallerAndUnrelatedMemberDoesNot()
    {
        var checks = (await SpikeTestContext.Fixture()).Where(c => c.Name.StartsWith("interface-", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, checks.Length);
        Assert.Equal(["SpikeFixtureInterfaceTests"], checks.Single(c => c.Name == "interface-first").Returned);
        Assert.Equal(["SpikeFixtureInterfaceTests"], checks.Single(c => c.Name == "interface-second-explicit").Returned);
        Assert.Empty(checks.Single(c => c.Name == "interface-unrelated").Returned);
    }
}
