// Parallel-safe: delegates and observations are local to each test; no links are created.
public sealed class DirectoryLinkCapabilityTests
{
    [Fact]
    public void Create_IoFailure_ReturnsClassifiedUnavailable()
    {
        AssertUnavailable(new IOException("I/O failure"),
            "capability-unavailable:directory-link: I/O failure");
    }

    [Fact]
    public void Create_AccessDenied_ReturnsClassifiedUnavailable()
    {
        AssertUnavailable(new UnauthorizedAccessException("Access denied"),
            "capability-unavailable:directory-link: Access denied");
    }

    [Fact]
    public void Create_PlatformUnsupported_ReturnsClassifiedUnavailable()
    {
        AssertUnavailable(new PlatformNotSupportedException("Platform unsupported"),
            "capability-unavailable:directory-link: Platform unsupported");
    }

    [Fact]
    public void Create_IoSubclass_ReturnsClassifiedUnavailable()
    {
        AssertUnavailable(new DirectoryNotFoundException("Target missing"),
            "capability-unavailable:directory-link: Target missing");
    }

    [Fact]
    public void Create_Success_InvokesDelegateOnceWithPathsAndReturnsAvailable()
    {
        var calls = new List<(string Link, string Target)>();
        var capability = new DirectoryLinkCapability((link, target) => calls.Add((link, target)));

        var result = capability.Create("link-path", "target-path");

        Assert.True(result.IsAvailable);
        Assert.Null(result.UnavailableReason);
        Assert.Equal(("link-path", "target-path"), Assert.Single(calls));
    }

    [Fact]
    public void Create_NotSupported_PropagatesOriginalException()
    {
        AssertPropagates(new NotSupportedException("Unexpected unsupported operation"));
    }

    [Fact]
    public void Create_InvalidOperation_PropagatesOriginalException()
    {
        AssertPropagates(new InvalidOperationException("Unexpected operation"));
    }

    private static void AssertUnavailable(Exception exception, string expectedReason)
    {
        var calls = new List<(string Link, string Target)>();
        var capability = new DirectoryLinkCapability((link, target) =>
        {
            calls.Add((link, target));
            throw exception;
        });

        var result = capability.Create("link-path", "target-path");

        Assert.False(result.IsAvailable);
        Assert.Equal(expectedReason, result.UnavailableReason);
        Assert.Equal(("link-path", "target-path"), Assert.Single(calls));
    }

    private static void AssertPropagates(Exception exception)
    {
        var capability = new DirectoryLinkCapability((_, _) => throw exception);

        var caught = Record.Exception(() => capability.Create("link-path", "target-path"));

        Assert.Same(exception, caught);
    }
}
