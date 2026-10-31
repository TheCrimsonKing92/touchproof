using System.Collections;
using System.Diagnostics;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>An explicit child environment snapshot; never mutates the conductor's ambient environment.</summary>
public sealed class ConductorChildEnvironment
{
    public IReadOnlyDictionary<string, string?> Variables { get; }

    public ConductorChildEnvironment(IEnumerable<KeyValuePair<string, string?>> variables) =>
        Variables = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string?>(
            variables.ToDictionary(pair => pair.Key, pair => pair.Value,
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal));

    public static ConductorChildEnvironment Capture() => new(Environment.GetEnvironmentVariables()
        .Cast<DictionaryEntry>().Select(pair => new KeyValuePair<string, string?>((string)pair.Key, pair.Value?.ToString())));

    public void Apply(ProcessStartInfo startInfo)
    {
        startInfo.Environment.Clear();
        foreach (var pair in Variables) startInfo.Environment[pair.Key] = pair.Value;
    }
}
