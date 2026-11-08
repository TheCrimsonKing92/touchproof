using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Shared registry/binding inventory. A failed resolution never supplies a guessed store path.
internal sealed record AttentionWorkspaceEntry(string Name, OrchestratorWorkspace? Workspace,
    Exception? Error = null, bool DeliveryAnswered = false, bool UseLearnedBranch = false);
internal sealed record AttentionWorkspaceCatalog(IReadOnlyList<AttentionWorkspaceEntry> Projects,
    Exception? RegistryError = null)
{
    // Equivalence comes only from resolved canonical state paths; unresolved entries stay separate.
    internal IEnumerable<AttentionWorkspaceEntry[]> CanonicalGroups()
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return Projects.Where(entry => entry.Workspace is not null)
            .GroupBy(entry => WorkspaceInvocationBinding.CanonicalPath(entry.Workspace!.SqliteStatePath), comparer)
            .Select(group => group.OrderBy(entry => entry.Name.Equals("home", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Name, StringComparer.Ordinal).ToArray())
            .Concat(Projects.Where(entry => entry.Workspace is null).Select(entry => new[] { entry }))
            .OrderBy(group => group[0].Name, StringComparer.Ordinal);
    }

    internal static string[] Aliases(AttentionWorkspaceEntry[] group) => group.Skip(1)
        .Select(entry => entry.Name).Distinct(StringComparer.Ordinal).Where(name => name != group[0].Name)
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ThenBy(name => name, StringComparer.Ordinal).ToArray();
}

internal static class OwnerConsoleWorkspaceCatalog
{
    internal static AttentionWorkspaceCatalog ReadWorkspaceCatalog(string repositoryRoot, string? tenant,
        OrchestratorProjectRegistry registry, OrchestratorWorkspace? launchWorkspace = null,
        CancellationToken cancellationToken = default)
    {
        var entries = new List<AttentionWorkspaceEntry>();
        cancellationToken.ThrowIfCancellationRequested();
        try { _ = File.GetAttributes(registry.RegistryPath); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Registry.LoadFile's legacy File.Exists probe cannot distinguish access denial.
            // Preserve a supplied launch binding, but never guess a default state location.
            if (launchWorkspace is { IsProjectScoped: false }) entries.Add(new("home", launchWorkspace));
            else entries.Add(new("home", null, ex));
            return new(entries, ex);
        }
        AttentionWorkspaceEntry Describe(string name, Func<OrchestratorWorkspace> resolve,
            bool deliveryAnswered = false, bool useLearnedBranch = false)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var target = launchWorkspace is not null && name.Equals(
                    OwnerConsoleProjectLabel.ForWorkspace(launchWorkspace), StringComparison.OrdinalIgnoreCase)
                    ? launchWorkspace : resolve();
                return new(name, target, DeliveryAnswered: deliveryAnswered, UseLearnedBranch: useLearnedBranch);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { return new(name, null, ex); }
        }
        entries.Add(Describe("home", () => OrchestratorWorkspace.ForDirectory(repositoryRoot, null, tenant, registry)));
        Exception? inventoryError = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var project in registry.ListProjects())
            {
                if (project.Name.Equals("home", StringComparison.OrdinalIgnoreCase))
                {
                    inventoryError = new InvalidOperationException(
                        $"Registered project '{project.Name}' conflicts with the fleet home identity; rename the registration to include it in the inventory.");
                    continue;
                }
                entries.Add(Describe(project.Name, () => OrchestratorWorkspace.ForProject(project.Name,
                    project.RootDirectory, tenantName: tenant, integrationBranch: project.IntegrationBranch,
                    dataRootDirectory: registry.DataRootDirectory), project.DeliveryAnswer is not null,
                    project.UseLearnedIntegrationBranch));
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new(entries, inventoryError);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return new(entries, ex); }
    }
}
