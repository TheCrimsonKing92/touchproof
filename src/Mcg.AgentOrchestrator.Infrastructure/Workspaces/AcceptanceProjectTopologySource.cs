using static Mcg.AgentOrchestrator.Infrastructure.AcceptancePolicyShardPlanner;

namespace Mcg.AgentOrchestrator.Infrastructure;

// A gate's topology authority: only home gates may infer consumers or cache eligibility.
internal sealed class AcceptanceProjectTopologySource
{
    internal static AcceptanceProjectTopologySource Home { get; } = new(true, true, []);

    private AcceptanceProjectTopologySource(bool isHome, bool manifestAvailable, string[] declaredProjects)
    {
        IsHome = isHome;
        ManifestAvailable = manifestAvailable;
        DeclaredProjects = Array.AsReadOnly(declaredProjects);
        Graph = isHome ? ProjectTopology : new Dictionary<string, ProjectTopologyEntry>();
    }

    internal bool IsHome { get; }
    internal bool ManifestAvailable { get; }
    internal IReadOnlyList<string> DeclaredProjects { get; }
    internal IReadOnlyDictionary<string, ProjectTopologyEntry> Graph { get; }

    internal static AcceptanceProjectTopologySource Create(
        string? projectHomeDirectory, IEnumerable<string>? manifestProjects) =>
        GoalAcceptanceVerifierHomeImpactScope.Applies(projectHomeDirectory) ? Home :
            new(false, manifestProjects is not null, (manifestProjects ?? [])
                .Where(project => !string.IsNullOrWhiteSpace(project))
                .Select(project => NormalizePath(project)!)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray());

    internal string? MapPath(string path)
    {
        if (IsHome) return TryMapPathToProject(path);
        var normalized = NormalizePath(path)!;
        var matches = DeclaredProjects.Where(project =>
        {
            var directory = ProjectDirectory(project);
            return directory is not null && normalized.StartsWith(directory, StringComparison.OrdinalIgnoreCase);
        }).OrderByDescending(project => ProjectDirectory(project)!.Length).ToArray();
        if (matches.Length == 0) return null;
        var longest = ProjectDirectory(matches[0])!;
        return matches.Count(project => ProjectDirectory(project)!.Length == longest.Length) == 1 ? matches[0] : null;
    }

    internal IEnumerable<string> LinkedConsumers(string path) =>
        IsHome ? AcceptancePolicyShardPlannerLinkedSources.Consumers(path) : [];

    private static string? ProjectDirectory(string project)
    {
        if (Path.IsPathRooted(project) || project.Contains(':') ||
            !project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
            project.Split('/').Any(segment => segment is "." or "..")) return null;
        var separator = project.LastIndexOf('/');
        return separator > 0 ? project[..(separator + 1)] : null;
    }
}
