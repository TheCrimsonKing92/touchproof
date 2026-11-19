using System.Collections.Frozen;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Credential and hook homes reserved by provider environment seeding.</summary>
public static class WorkerProviderHomeEnvironment
{
    public static IReadOnlySet<string> Names { get; } = new[]
    {
        "CODEX_HOME", "CLAUDE_CONFIG_DIR", "GROK_HOME", "HERMES_HOME",
        HarnessHookRootContract.EnvironmentVariableName
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    internal static string CodexSeedDirectory() => UserProfileDirectory(".codex");

    internal static string GrokSeedDirectory() => UserProfileDirectory(".grok");

    internal static void ApplyDesktopCredentialSource(IDictionary<string, string?> environment, ProviderKind provider)
    {
        (string name, string? directory) = provider switch
        {
            ProviderKind.OpenAICodexCli or ProviderKind.OpenAICodexLuna => ("CODEX_HOME", CodexSeedDirectory()),
            ProviderKind.AnthropicClaudeCli => (ClaudeCredentialSource.ConfigDirectoryEnvironmentVariable,
                ClaudeCredentialSource.ResolveLocation(null, null).DirectoryPath),
            ProviderKind.XaiGrokCli => ("GROK_HOME", GrokSeedDirectory()),
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        };
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("The desktop provider credential source could not be resolved.");
        environment[name] = directory;
    }

    private static string UserProfileDirectory(string directory) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), directory);
}
