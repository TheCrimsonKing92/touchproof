using Mcg.SemanticImpactSpike;
using Xunit;

public sealed class SemanticImpactSpikeReceiptPathTests
{
    [Fact]
    public void NestedReceiptPathsAndErrorsArePortableWhileMeasurementsArePreserved()
    {
        Assert.All(SpikeChecks.ReceiptPaths(), check => Assert.True(check.Passed, check.Name));
    }
}
