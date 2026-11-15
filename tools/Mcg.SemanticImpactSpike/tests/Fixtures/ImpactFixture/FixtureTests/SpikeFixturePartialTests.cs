using SpikeFixture;
using Xunit;

public sealed partial class SpikeFixturePartialTests
{
    [Fact]
    public void CallsOtherPart() => Helper();
}
