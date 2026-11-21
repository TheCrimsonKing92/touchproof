using System.Text.Json.Serialization;

namespace Mcg.AgentOrchestrator.Infrastructure;

public enum ModelProbeStatus { NotProbed, Succeeded, Failed }
public enum ModelSourceStatus { Available, Unauthenticated, Unavailable, NotConfigured }
public enum ModelDiscoverySource { CodexCache, GrokModels, AnthropicModels, DeclarationProbe }

public sealed record ModelInventoryEntry(
    string Provider, string Model, string Profile, ModelDiscoverySource Source,
    DateTimeOffset DiscoveredAt, DateTimeOffset? SourceTimestamp = null,
    ModelProbeStatus ProbeStatus = ModelProbeStatus.NotProbed, string? ProbeDetail = null)
{
    [JsonIgnore]
    public bool Eligible => ProbeStatus == ModelProbeStatus.Succeeded ||
        (Source != ModelDiscoverySource.DeclarationProbe && ProbeStatus == ModelProbeStatus.NotProbed);
}

public sealed record ModelInventorySource(
    string Provider, string Profile, ModelDiscoverySource Source, ModelSourceStatus Status,
    DateTimeOffset ObservedAt, string? Detail = null);

public sealed record ModelInventory(IReadOnlyList<ModelInventoryEntry> Entries, IReadOnlyList<ModelInventorySource> Sources)
{
    public static ModelInventory Empty { get; } = new([], []);

    public IEnumerable<ModelInventoryEntry> Find(string provider, string model) =>
        Entries.Where(entry => Same(entry.Provider, provider) && Same(entry.Model, model));

    public static bool Same(string left, string right) => left.Equals(right, StringComparison.OrdinalIgnoreCase);
}

public sealed record ModelDeclaration(string Provider, string Model, string? Profile);

public sealed record ModelListing(
    ModelSourceStatus Status, IReadOnlyList<string> Models, DateTimeOffset? SourceTimestamp = null, string? Detail = null);

public sealed record ModelProbeResult(ModelProbeStatus Status, string? Detail = null);
