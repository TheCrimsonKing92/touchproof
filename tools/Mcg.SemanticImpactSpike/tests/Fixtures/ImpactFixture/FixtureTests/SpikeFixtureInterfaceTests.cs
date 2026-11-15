using SpikeFixture;
using Xunit;

public sealed class SpikeFixtureInterfaceTests
{
    [Fact]
    public void ThroughInterface()
    {
        IService service = new First();
        service.Run();
    }
}
