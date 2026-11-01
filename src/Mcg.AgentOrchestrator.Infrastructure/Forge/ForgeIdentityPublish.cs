namespace Mcg.AgentOrchestrator.Infrastructure;

// One publish's environment, never persisted or cached. Do not print its entries.
public sealed record ForgeIdentityPublish(IReadOnlyDictionary<string, string> Environment, string Login)
{
    public override string ToString() => $"Forge identity: {Login}";
}
