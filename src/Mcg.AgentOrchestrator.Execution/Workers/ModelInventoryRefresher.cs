using System.ComponentModel;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static class ModelInventoryRefresher
{
    private static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;

    public static async Task<ModelInventory> RefreshAsync(
        string path,
        WorkerProfileCatalog profiles,
        IReadOnlyList<ModelDeclaration> declarations,
        Func<WorkerProfile, ModelDiscoverySource, CancellationToken, Task<ModelListing>> list,
        Func<WorkerProfile, string, CancellationToken, Task<ModelProbeResult>> probe,
        TimeProvider? clock = null,
        CancellationToken cancellationToken = default)
    {
        var observedAt = (clock ?? TimeProvider.System).GetUtcNow();
        var entries = new List<ModelInventoryEntry>();
        var sources = new List<ModelInventorySource>();
        foreach (var (profile, provider, source) in SupportedProfiles(profiles))
        {
            var listing = await ObserveAsync(token => list(profile, source, token),
                detail => new ModelListing(ModelSourceStatus.Unavailable, [], Detail: detail), cancellationToken);
            sources.Add(new(provider, profile.Name, source, listing.Status, observedAt, listing.Detail));

            var models = new Dictionary<string, ModelInventoryEntry>(Names);
            if (listing.Status == ModelSourceStatus.Available)
            {
                foreach (var model in listing.Models)
                    models.TryAdd(model, new(provider, model, profile.Name, source, observedAt, listing.SourceTimestamp));
            }

            foreach (var declaration in declarations.Where(declaration =>
                ModelInventory.Same(declaration.Provider, provider) &&
                (declaration.Profile is null || ModelInventory.Same(declaration.Profile, profile.Name)))
                .DistinctBy(declaration => declaration.Model, Names))
            {
                var entry = models.GetValueOrDefault(declaration.Model)
                    ?? new(provider, declaration.Model, profile.Name, ModelDiscoverySource.DeclarationProbe, observedAt);
                var result = await ObserveAsync(token => probe(profile, entry.Model, token),
                    detail => new ModelProbeResult(ModelProbeStatus.Failed, detail), cancellationToken);
                models[entry.Model] = entry with { ProbeStatus = result.Status, ProbeDetail = result.Detail };
            }

            entries.AddRange(models.Values.OrderBy(entry => entry.Model, Names));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var inventory = new ModelInventory(entries, sources);
        ModelInventoryStore.Save(path, inventory);
        return inventory;
    }

    public static IEnumerable<(WorkerProfile Profile, string Provider, ModelDiscoverySource Source)> SupportedProfiles(
        WorkerProfileCatalog profiles)
    {
        var catalog = WorkerProviderCatalog.Default();
        foreach (var profile in profiles.Profiles.OrderBy(profile => profile.Name, Names))
        {
            if (!WorkerProfileDiagnostics.IsPatchCapableCommand(profile.CommandTemplate) ||
                !WorkerProfileDiagnostics.UsesSubscriptionModelPlaceholder(profile.CommandTemplate))
                continue;

            var provider = catalog.ResolveProfile(profile.Name);
            ModelDiscoverySource? source = provider.Identity.Kind switch
            {
                ProviderKind.OpenAICodexCli or ProviderKind.OpenAICodexLuna => ModelDiscoverySource.CodexCache,
                ProviderKind.XaiGrokCli => ModelDiscoverySource.GrokModels,
                ProviderKind.AnthropicClaudeCli => ModelDiscoverySource.AnthropicModels,
                _ => null
            };
            if (source is { } discoverySource)
                yield return (profile, provider.ProviderName, discoverySource);
        }
    }

    private static async Task<T> ObserveAsync<T>(
        Func<CancellationToken, Task<T>> observe, Func<string, T> unavailable, CancellationToken token)
    {
        try { return await observe(token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return unavailable("Timed out");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or HttpRequestException or Win32Exception or InvalidOperationException)
        {
            return unavailable(error is JsonException ? "Invalid source response" : error.GetType().Name);
        }
    }
}
