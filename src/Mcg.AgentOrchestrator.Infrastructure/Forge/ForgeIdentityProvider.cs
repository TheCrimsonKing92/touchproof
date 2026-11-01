using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed class ForgeIdentityProvider(IForgeHttpTransport? transport = null, IClock? clock = null) : IForgeIdentityProvider
{
    public const string GitHubApp = "github-app";
    public const string GrantFile = "grant-file";

    public ForgeIdentityPublish Acquire(string settingsPath)
    {
        try
        {
            ProjectForgeIdentity.Validate(settingsPath);
            using var settings = JsonDocument.Parse(File.ReadAllText(settingsPath));
            var root = settings.RootElement;
            var kind = root.GetProperty("kind").GetString();
            return kind switch
            {
                GitHubApp => new GitHubAppForgeIdentity(transport ?? new ForgeHttpTransport(), clock ?? new SystemClock()).Acquire(root),
                // Future scoped, long-lived bot grant plus display login. No fallback or implementation here.
                GrantFile => throw new ForgeIdentityUnavailableException("forge identity kind 'grant-file' is reserved and not implemented"),
                _ => throw new ForgeIdentityUnavailableException("unknown forge identity kind")
            };
        }
        catch (ForgeIdentityUnavailableException) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new ForgeIdentityUnavailableException($"forge identity settings could not be read ({exception.GetType().Name})");
        }
    }
}
