namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Recognizes only complete, explicit missing-tool reasons for contracted names.</summary>
internal static class ContractedToolSkipPolicy
{
    internal static string? MissingTool(string? reason, EnvironmentContract? contract)
    {
        if (reason is null || contract is null) return null;
        var message = reason.Trim();
        foreach (var tool in contract.Tools)
        {
            string[] forms = [$"{tool.Name} not found", $"{tool.Name} is not installed",
                $"{tool.Name} unavailable", $"missing required tool: {tool.Name}"];
            if (forms.Any(form => message.Equals(form, StringComparison.OrdinalIgnoreCase) ||
                message.Equals(form + ".", StringComparison.OrdinalIgnoreCase)))
                return tool.Name;
        }
        return null;
    }
}
