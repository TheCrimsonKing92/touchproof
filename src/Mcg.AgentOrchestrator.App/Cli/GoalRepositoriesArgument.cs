using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class GoalRepositoriesArgument
{
    internal static GoalRepositoryDeclaration? Resolve(IReadOnlyList<string> parts,
        OrchestratorWorkspace workspace, OrchestratorProjectRegistry? registry = null)
    {
        var flags = parts.Count(part => part.Equals("--repositories", StringComparison.OrdinalIgnoreCase) ||
            part.StartsWith("--repositories=", StringComparison.OrdinalIgnoreCase));
        if (flags == 0) return null;
        if (flags > 1) throw new ArgumentException("--repositories must be supplied once.");
        if (parts.Any(part => part.Equals("--simple", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("--run", StringComparison.OrdinalIgnoreCase) || part.Equals("--from-backlog", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("--repositories cannot be combined with --simple, --run or --from-backlog.");
        var value = CliCommandHandlers.GetFlagValue(parts, "--repositories");
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal) || value.Split(',').Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("--repositories requires comma-separated repository keys without empty elements.");
        registry ??= OrchestratorProjectRegistry.CreateDefault();
        var roots = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var supplied in value.Split(',', StringSplitOptions.TrimEntries))
        {
            var isDefault = supplied.Equals("default", StringComparison.OrdinalIgnoreCase);
            var project = isDefault ? null : registry.GetRequiredProject(supplied);
            var key = isDefault ? "default" : project!.Name.ToLowerInvariant();
            if (!GoalRepositorySet.IsCanonicalKey(key))
                throw new ArgumentException($"Invalid repository key '{supplied}'.");
            roots[key] = isDefault ? workspace.ExecutionDirectory : project!.RootDirectory;
        }
        if (!roots.ContainsKey("default")) throw new ArgumentException("--repositories: default is required.");
        return roots.Count == 1 ? null : new GoalRepositoryDeclaration(roots.Keys.ToArray(), roots);
    }
}
