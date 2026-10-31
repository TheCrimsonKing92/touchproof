using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static class EnvironmentContractComparer
{
    public static IReadOnlyList<string> Compare(EnvironmentContract learned, EnvironmentContract manifest)
    {
        var left = learned.Tools.ToDictionary(tool => tool.Name, StringComparer.OrdinalIgnoreCase);
        var right = manifest.Tools.ToDictionary(tool => tool.Name, StringComparer.OrdinalIgnoreCase);
        return left.Keys.Union(right.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).Select(name =>
        {
            var kind = !left.ContainsKey(name) ? "MissingFromLearned" : !right.ContainsKey(name) ? "MissingFromManifest" :
                Signature(left[name]) == Signature(right[name]) ? "Match" : "Differs";
            return $"{kind} tool {name}";
        }).ToArray();
    }

    private static string Signature(EnvironmentToolRequirement tool) => JsonSerializer.Serialize(new
    { tool.Command, tool.Arguments, tool.OutputPattern, Variables = (tool.Variables ?? []).Order(StringComparer.Ordinal).ToArray() });
}
