using System.Collections.Immutable;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Execution metadata captured once per attempt; never an input to candidate identity.
internal sealed class AcceptanceHostTestSettings
{
    internal const string LocalHost = "local";
    public sealed record Binding(string Lane, string Host, IReadOnlyDictionary<string, string> Settings);
    private readonly IReadOnlyDictionary<string, string> _manifest;
    private readonly ImmutableDictionary<string, IReadOnlyDictionary<string, string>> _executors;
    internal IReadOnlyDictionary<string, string> Local { get; }

    private AcceptanceHostTestSettings(IReadOnlyDictionary<string, string> manifest,
        RemoteLaneExecutorConfiguration configuration)
    {
        _manifest = manifest;
        Local = Overlay(manifest, configuration.LocalTestSettings);
        _executors = configuration.Executors.ToImmutableDictionary(entry => entry.Id,
            entry => Overlay(manifest, entry.TestSettings), StringComparer.Ordinal);
    }

    internal static AcceptanceHostTestSettings Resolve(IReadOnlyDictionary<string, string> manifest,
        RemoteLaneExecutorConfiguration configuration) => new(manifest, configuration);

    internal Binding ForLocal(string lane) => new(lane, LocalHost, Local);
    internal Binding ForExecutor(string lane, string executorId) =>
        new(lane, executorId, _executors.GetValueOrDefault(executorId) ?? _manifest);

    internal static IReadOnlyDictionary<string, string> Overlay(IReadOnlyDictionary<string, string> manifest,
        IReadOnlyDictionary<string, string>? host)
    {
        if (host is null || host.Count == 0) return manifest;
        var merged = AcceptanceDeclaredTestSettings.Empty.ToBuilder();
        foreach (var pair in manifest) merged[pair.Key] = pair.Value;
        foreach (var pair in host) merged[pair.Key] = pair.Value;
        return merged.ToImmutable();
    }
}
