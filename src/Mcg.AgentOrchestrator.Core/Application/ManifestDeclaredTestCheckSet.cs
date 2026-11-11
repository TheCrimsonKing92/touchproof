namespace Mcg.AgentOrchestrator.Core;

/// <summary>One manifest's test declarations; filtering preserves plan order and check instances.</summary>
public sealed class ManifestDeclaredTestCheckSet
{
    public IReadOnlyList<ManifestDeclaredTestCheck> Checks { get; }
    private readonly IReadOnlyList<string> otherKinds;
    public ManifestDeclaredTestProjects DotnetProjects { get; }

    public ManifestDeclaredTestCheckSet(
        IEnumerable<ManifestDeclaredTestCheck> checks, IEnumerable<string>? otherKinds = null)
    {
        Checks = checks.ToArray();
        this.otherKinds = (otherKinds ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        DotnetProjects = ManifestDeclaredTestProjects.From(Checks
            .Where(check => check.Kind == ManifestTestCheckKinds.Dotnet).Select(check => check.Project));
    }

    public string Kinds => ManifestTestCheckKinds.Describe(Checks.Select(check => check.Kind));

    public RepositoryTestImpactPlan Filter(RepositoryTestImpactPlan plan)
    {
        var dotnetChecks = DotnetProjects.Filter(plan).Checks;
        return plan with
        {
            Checks = plan.Checks.Where(check => ManifestTestCheckKinds.FromCommand(check.Command) switch
            {
                ManifestTestCheckKinds.Dotnet => dotnetChecks.Contains(check),
                ManifestTestCheckKinds.Crosswalk => Checks.Any(declared =>
                    declared.Kind == ManifestTestCheckKinds.Crosswalk &&
                    string.Equals(NormalizeCrosswalkProject(declared.Project),
                        NormalizeCrosswalkProject(check.Command.Count > 1 ? check.Command[1] : null),
                        StringComparison.OrdinalIgnoreCase)),
                _ => false
            }).ToArray()
        };
    }

    public string ScopedSummary(string displayPath)
    {
        var noun = Kinds == ManifestTestCheckKinds.Dotnet ? "dotnet-test projects" : $"{Kinds} checks";
        // Command checks are outside test scoping; preserve the existing scoped-project diagnostic.
        var unsupported = otherKinds.Where(kind => !kind.Equals("command", StringComparison.OrdinalIgnoreCase)).ToArray();
        return $"checks scoped to {noun} declared by {displayPath}{OtherKindsSummary(unsupported)}";
    }

    public string NoChecksSummary(string displayPath) =>
        $"acceptance manifest at {displayPath} declares no test checks (dotnet-test or crosswalk-test); no project checks derived{OtherKindsSummary()}";

    public string FallbackSummary(string displayPath) =>
        $"no change-scoped checks for this target; using the manifest's declared test checks ({Kinds}) from {displayPath}{OtherKindsSummary()}";

    private string OtherKindsSummary() => OtherKindsSummary(otherKinds);

    private static string OtherKindsSummary(IReadOnlyList<string> kinds) => kinds.Count == 0
        ? string.Empty : $"; declared kinds not derived: {string.Join(", ", kinds)}";

    private static string NormalizeCrosswalkProject(string? project) =>
        string.IsNullOrWhiteSpace(project) ? "." : project.Replace('\\', '/');
}
