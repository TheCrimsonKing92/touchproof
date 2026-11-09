namespace Mcg.AgentOrchestrator.Core;

public static class GateTestObligationRepositoryKey
{
    public static string Resolve(string? key) => string.IsNullOrWhiteSpace(key) ? "default" : key;

    public static bool IsHome(string? key) => string.Equals(Resolve(key), "default", StringComparison.Ordinal);
}
