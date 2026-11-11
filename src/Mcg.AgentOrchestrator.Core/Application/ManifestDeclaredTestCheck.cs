namespace Mcg.AgentOrchestrator.Core;

/// <summary>A manifest test check's identity and execution context relative to the target worktree.</summary>
public sealed record ManifestDeclaredTestCheck(
    string Name, string Kind, string? Project, IReadOnlyList<string> Arguments)
{
    public RepositoryTestImpactCheck ToImpactCheck(string displayPath) =>
        new(Name, ManifestTestCheckKinds.Command(Kind, Project, Arguments),
            $"Declared {Kind} check from {displayPath}.");
}
