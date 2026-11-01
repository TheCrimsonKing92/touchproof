namespace Mcg.AgentOrchestrator.Infrastructure;

// Only safe, deliberately constructed messages belong here; never wrap raw provider errors.
public sealed class ForgeIdentityUnavailableException(string message) : Exception(message);
