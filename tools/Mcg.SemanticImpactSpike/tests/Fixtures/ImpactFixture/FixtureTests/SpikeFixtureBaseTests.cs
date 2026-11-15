using SpikeFixture;
using Xunit;

public abstract class SpikeFixtureBaseTests
{
    [Fact]
    public void InheritedFact() => InheritedTarget.Run();
}
