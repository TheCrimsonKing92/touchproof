namespace Mcg.AgentOrchestrator.Core;

/// <summary>An explicit task assignment stored in the existing assignment slot, outside the roster.</summary>
public sealed record InventoryModelAssignment(string Provider, string Model, string Profile, string? Effort, AgentRole Role)
{
    private const string Prefix = "inventory:";

    public AgentId ToAgentId() => new($"{Prefix}{Role}:{Profile}:{Effort}:{Provider}/{Model}");

    public AgentDefinition ToDispatchAgent()
    {
        var model = new ModelProfile(Provider, Model, ModelCapability.Text | ModelCapability.Code | ModelCapability.ToolUse,
            SubscriptionMode.ApiKey, Effort);
        return new(ToAgentId(), $"{Provider}/{Model}", Role, model, ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            Subscription: new(Profile, Model, Effort), IsProviderRoutingConstrained: true);
    }

    public static bool IsInventoryId(AgentId? id) =>
        id?.Value.StartsWith(Prefix, StringComparison.Ordinal) == true;

    public static InventoryModelAssignment? Parse(AgentId? id)
    {
        if (!IsInventoryId(id)) return null;
        var parts = id!.Value[Prefix.Length..].Split(':', 4);
        return parts.Length == 4 && Enum.TryParse<AgentRole>(parts[0], out var role) && parts[1].Length > 0 &&
            TrySplitModel(parts[3], out var provider, out var model)
            ? new(provider, model, parts[1], parts[2].Length == 0 ? null : parts[2], role)
            : throw new InvalidOperationException($"Invalid inventory assignment '{id.Value}'.");
    }

    public static bool TrySplitModel(string value, out string provider, out string model)
    {
        var slash = value.IndexOf('/');
        provider = slash > 0 ? value[..slash] : "";
        model = slash >= 0 ? value[(slash + 1)..] : "";
        return !string.IsNullOrWhiteSpace(provider) && !string.IsNullOrWhiteSpace(model);
    }
}
