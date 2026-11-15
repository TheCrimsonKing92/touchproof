using SpikeFixture;
using Xunit;

public sealed class SpikeFixtureDelegateTests
{
    [Fact]
    public void PassedMethodGroup() => DelegateSeam.Invoke(Callback.Target);
}
