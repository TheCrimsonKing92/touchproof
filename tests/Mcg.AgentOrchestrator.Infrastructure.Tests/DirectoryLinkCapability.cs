internal sealed class DirectoryLinkCapability
{
    internal const string UnavailablePrefix = "capability-unavailable:directory-link";

    private readonly Action<string, string> createLink;

    internal DirectoryLinkCapability(Action<string, string>? createLink = null)
    {
        this.createLink = createLink ?? ((linkPath, targetPath) => Directory.CreateSymbolicLink(linkPath, targetPath));
    }

    internal Result Create(string linkPath, string targetPath)
    {
        try
        {
            createLink(linkPath, targetPath);
            return new Result(null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return new Result($"{UnavailablePrefix}: {exception.Message}");
        }
    }

    internal sealed record Result(string? UnavailableReason)
    {
        internal bool IsAvailable => UnavailableReason is null;
    }
}
