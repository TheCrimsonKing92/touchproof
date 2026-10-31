using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static class EnvironmentFingerprint
{
    public static string Create(string buildIdentity, EnvironmentContract contract, ConductorChildEnvironment environment,
        string workingDirectory)
    {
        // Include the complete block: unnamed input changes must not accidentally reuse a pass either.
        var inputs = new { buildIdentity, workingDirectory, Contract = contract.ToJson(),
            Variables = environment.Variables.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray() };
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(inputs))));
    }
}
