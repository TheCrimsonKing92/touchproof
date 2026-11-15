using SpikeFixture;
using Xunit;

public sealed class SpikeFixtureStaticTests
{
    [Fact]
    public void QualifiedCall() => StaticTarget.Run();
}
