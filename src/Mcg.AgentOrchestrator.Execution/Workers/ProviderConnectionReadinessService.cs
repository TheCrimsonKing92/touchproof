using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public interface IProviderConnectionEvidenceQueries
{
    Task<IReadOnlyList<ProviderConnectionObservation>> ReadProviderConnectionEvidenceAsync(string profileId,
        CancellationToken cancellationToken = default) => throw new NotSupportedException("Provider connection queries are unavailable.");

    async Task<IReadOnlyDictionary<string, IReadOnlyList<ProviderConnectionObservation>>> ReadProviderConnectionEvidenceAsync(
        IReadOnlyList<string> profileIds, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, IReadOnlyList<ProviderConnectionObservation>>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in profileIds) result.Add(id, await ReadProviderConnectionEvidenceAsync(id, cancellationToken));
        return result;
    }
}

public interface IProviderConnectionEvidenceStore : IProviderConnectionEvidenceQueries
{
    Task SaveProviderConnectionObservationAsync(string profileId, ProviderConnectionObservation observation,
        CancellationToken cancellationToken = default);
}

/// <summary>One invocation scope. Listing reads metadata only; checks never enter a sandbox.</summary>
public sealed class ProviderConnectionReadinessService(
    WorkerProfileCatalog catalog,
    IProviderConnectionEvidenceQueries evidence,
    ProviderConnectionStatusAdapters adapters,
    TimeProvider? time = null,
    Func<ClaudeCliAuthState>? claudeProbe = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public WorkerProfile Select(string name)
    {
        var normalized = LunaLaneNames.NormalizeProfileName(name);
        var matches = catalog.Profiles.Where(p => string.Equals(LunaLaneNames.NormalizeProfileName(p.Name),
            normalized, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) throw new ArgumentException("Select one configured, unambiguous profile.");
        return catalog.GetRequired(name);
    }

    public async Task<ProviderConnectionReport> ListAsync(CancellationToken cancellationToken = default)
    {
        var reports = new List<ProviderConnectionProfileReport>();
        var observations = await evidence.ReadProviderConnectionEvidenceAsync(catalog.Profiles.Select(p => p.Name).ToArray(), cancellationToken);
        foreach (var profile in catalog.Profiles)
            reports.Add(Compose(profile, observations[profile.Name]));
        return new(reports);
    }

    public async Task<ProviderConnectionReport> CheckAsync(string profileId,
        IProviderConnectionEvidenceStore store, CancellationToken cancellationToken = default)
    {
        var profile = Select(profileId); // Validate before any observation or write.
        var host = await adapters.CheckAsync(profile, cancellationToken);
        var observations = new List<ProviderConnectionObservation> { host };
        await store.SaveProviderConnectionObservationAsync(profile.Name, host, cancellationToken);
        if (ProviderConnectionStatusAdapters.Provider(profile) == "Anthropic")
        {
            var probe = claudeProbe ?? ClaudeCliAuthProbe.ForOneDispatchPreflight();
            var state = probe();
            var material = new ProviderConnectionObservation(ProviderConnectionContext.WorkerCredentialMaterial,
                ProviderConnectionSource.ClaudePreflight,
                state.HasAnthropicApiKey || state.HasCliCredentialArtifact
                    ? ProviderConnectionResult.MaterialPresent : ProviderConnectionResult.MaterialUnavailable,
                state.HasAnthropicApiKey ? ProviderConnectionAuthMode.ApiKey : state.HasCliCredentialArtifact
                    ? ProviderConnectionAuthMode.OAuth : ProviderConnectionAuthMode.Unknown,
                _time.GetUtcNow());
            await store.SaveProviderConnectionObservationAsync(profile.Name, material, cancellationToken);
            observations.Add(material);
        }
        // Concurrent checks can advance the listing rows while this check is still running.
        var recorded = await evidence.ReadProviderConnectionEvidenceAsync(profile.Name, cancellationToken);
        observations.AddRange(recorded.Where(o => o.EvidenceSource == ProviderConnectionSource.RecordedInvocation));
        return new([Compose(profile, observations)]);
    }

    private ProviderConnectionProfileReport Compose(WorkerProfile profile, IReadOnlyList<ProviderConnectionObservation> evidence)
    {
        var observations = evidence.ToList();
        if (observations.All(o => o.ExecutionContext != ProviderConnectionContext.Host))
            observations.Insert(0, new(ProviderConnectionContext.Host, ProviderConnectionSource.None, ProviderConnectionResult.Unknown));
        if (observations.All(o => o.EvidenceSource != ProviderConnectionSource.RecordedInvocation))
            observations.Add(new(ProviderConnectionContext.WorkerUnspecified, ProviderConnectionSource.RecordedInvocation,
                ProviderConnectionResult.Unknown, IsHistorical: true));
        return new(profile.Name, ProviderConnectionStatusAdapters.Provider(profile),
            observations.Select(o => o.AtReadTime(_time.GetUtcNow())).ToArray());
    }
}
