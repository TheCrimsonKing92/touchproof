namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Only passes are cached. Failed environments are always observed again on the next admission.</summary>
public sealed class EnvironmentProbeCache(IEnvironmentProbeRunner runner, TimeProvider? timeProvider = null)
{
    public static readonly TimeSpan PassLifetime = TimeSpan.FromMinutes(15);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<string, (DateTimeOffset At, IReadOnlyList<EnvironmentProbeResult> Results)> _passes = new(StringComparer.Ordinal);

    public IReadOnlyList<EnvironmentProbeResult> Probe(EnvironmentContract contract, string fingerprint,
        ConductorChildEnvironment environment, string workingDirectory)
    {
        lock (_passes)
        {
            if (_passes.TryGetValue(fingerprint, out var cached) && _time.GetUtcNow() - cached.At < PassLifetime)
                return cached.Results;
        }
        var results = Task.WhenAll(contract.Tools.Select(tool => runner.RunAsync(tool, environment, workingDirectory)))
            .ConfigureAwait(false).GetAwaiter().GetResult();
        lock (_passes)
        {
            foreach (var key in _passes.Where(pair => _time.GetUtcNow() - pair.Value.At >= PassLifetime).Select(pair => pair.Key).ToArray())
                _passes.Remove(key);
            if (results.All(result => result.Passed)) _passes[fingerprint] = (_time.GetUtcNow(), results);
        }
        return results;
    }
}
