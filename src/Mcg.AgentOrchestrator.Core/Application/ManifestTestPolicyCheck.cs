namespace Mcg.AgentOrchestrator.Core;

/// <summary>Preserves crosswalk command token boundaries across policy compilation and persistence.</summary>
public static class ManifestTestPolicyCheck
{
    public static VerificationPolicyCheck FromImpactCheck(RepositoryTestImpactCheck check)
    {
        var kind = check.Command.Count == 0 ? "no-op" :
            ManifestTestCheckKinds.FromCommand(check.Command) ?? ManifestTestCheckKinds.Dotnet;
        return new(check.Name, kind, check.Command.Count > 0, check.CommandLine, check.Reason)
        {
            CommandTokens = kind == ManifestTestCheckKinds.Crosswalk ? check.Command.ToArray() : null
        };
    }

    public static bool TryReadCrosswalkCommand(
        VerificationPolicyCheck check, out string? project, out IReadOnlyList<string> arguments)
    {
        // Older policies and hand-written checks carry only a space-separated command line.
        // Compiled policies use the original tokens, never reparsed display text.
        var parts = check.CommandTokens ?? check.CommandLine.Split(
            ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        project = parts.Count > 1 ? parts[1] : null;
        arguments = parts.Skip(2).ToArray();
        return ManifestTestCheckKinds.FromCommand(parts) == ManifestTestCheckKinds.Crosswalk;
    }
}
