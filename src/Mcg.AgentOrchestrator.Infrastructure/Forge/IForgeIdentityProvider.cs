namespace Mcg.AgentOrchestrator.Infrastructure;

public interface IForgeIdentityProvider
{
    ForgeIdentityPublish Acquire(string settingsPath);
}
